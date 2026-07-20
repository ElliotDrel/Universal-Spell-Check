# Replacements, Prompt-Leak Guard, and Logging

## Target formatting hooks

`TargetFormattingPipeline` resolves one rule from its explicit ordered list after clipboard-history
exclusion. Its after-copy hook runs before literal protection and the API request. After the existing
post-processing restores protected literals, the optional before-paste hook runs against a second set
of formatter-neutral private-use placeholders. Missing or duplicated placeholders abort the paste;
an unexpected hook exception retains the unmodified text and is recorded asynchronously.

Unmatched runs perform only the short resolver scan and a null branch. They do not perform the second
literal-protection scan. Headless benchmark runs remain unmatched unless a test explicitly supplies a
target context.

## Terminal input normalization (pre-processing)

Before the API call, `TerminalFormattingRule.AfterCopy` is invoked when the active process is a terminal (`WindowsTerminal`, `Code`, `powershell`, `pwsh`, `cmd`, `bash`). It runs three ordered passes to remove soft-wrap artifacts while preserving intentional structure:

1. **Double CRLF** (`\r\n\r\n[ \t]*`) → `\n\n` — preserves paragraph/section breaks.
2. **List items** (`\r\n[ \t]+` before `-`, `*`, `•`, or `N.`) → `\n` — preserves bullet and numbered list structure.
3. **Soft-wrap continuation** (` *\r\n[ \t]+`) → ` ` — collapses lines that wrapped purely due to terminal width.

Tabs are matched alongside spaces in all three passes. Bare `\r\n` without trailing whitespace is left untouched.

- Applied on the **hot path only** (`ExecuteHotPathAsync`). Headless/bench runs are unaffected.
- Bare `\r\n` without trailing whitespace is left untouched.
- Result is recorded through the common formatting result; the legacy `terminal_normalized=true terminal_norm_chars_removed=N` line fields and `terminal_normalization` JSON object remain unchanged.
- If not applied (non-terminal process or no artifacts found), `terminal_normalization.applied = false` and `chars_removed = 0`.

Full pipeline order: **resolve target** → **after-copy format** → **protect literals** → API call →
post-process (replacements + prompt-leak guard) → **restore literals** → **validate target** →
**before-paste format with protected literals** → **final target validation**.

## Protected literals

Before the API call, `ProtectedText.Protect` replaces exact literals with collision-safe numbered
placeholders. It protects:

- `http://` and `https://` URLs
- UUIDs and long opaque IDs containing both letters and digits
- common prefixed API keys/tokens, JWTs, and values assigned to key/token/secret/session fields
- Windows drive paths, UNC paths, POSIX paths, and slash-delimited relative paths

Quoted file paths may contain spaces. Unquoted paths stop at whitespace. After the AI response,
replacements and the prompt-leak guard run while placeholders are still present, then
`ProtectedText.Restore` restores the original byte-for-byte values. If a placeholder is missing
or duplicated, the run fails with `protected_text_restore_failed` and does not paste potentially
corrupted text.

---

## Replacements system

`replacements.json` lives at the repo root and is copied next to the exe at publish time via a `<None Include>` itemgroup in the csproj. `AppPaths.ReplacementsPath` finds it by walking up from `AppContext.BaseDirectory` until the file is found — works for both dev checkout (`src/bin/...` walks up to repo root) and Velopack-installed prod (found on the first try, next to the exe).

Format — canonical key maps to an array of variants to replace:

```json
{
  "GitHub": ["Git Hub", "git hub", "GitHUB", "GITHUB", "Github", "github"],
  "OpenAI": ["Open AI", "Open Ai", "open ai", "openai"]
}
```

### `TextPostProcessor` behavior (`src/TextPostProcessor.cs`)

- Parses the JSON, strips BOM, flattens to `(variant, canonical)` pairs, sorts longest-first so longer variants win over shorter overlapping ones.
- Uses case-sensitive ordinal comparisons and replacements (`StringComparison.Ordinal`).
- Protected literals remain as placeholders during replacements, so replacement variants cannot alter them.
- Reloads when `FileInfo.LastWriteTimeUtc` or `Length` changes. Keeps last known-good cache on reload failure. Clears cache when the file is missing.
- Reload logged as `replacements_reloaded count={N}`. Reload failure logged as `replacements_reload_failed`.

### Prompt-leak guard

The request prompt is structured as:

```
instructions: <PromptInstruction>
text input: <selected text>
```

If the model echoes the instruction preamble back in its output, `StripPromptLeak` removes the `instructions: ...` line and then strips a leading `text input:` label. Logged via `prompt_leak_triggered` / `prompt_leak_removed_chars` on the `replace_succeeded` line.

### Watchlist for replacements

- **Repeated reload failures** (`replacements_reload_failed` in logs) → malformed JSON, file locked by editor, or save-timing issue.
- **Same-size fast-edit edge case** — cache key is `mtime + size`; a very fast same-size edit can be missed until a later detectable save.
- **Read-while-writing risk** — if the file is edited mid-read, stale data can be cached under newer metadata.

---

## Logging (JSONL)

### Location

Both Prod and Dev write to the same directory:

```
%LocalAppData%\UniversalSpellCheck.Data\logs\spellcheck-{yyyy-MM-dd}.jsonl
```

`AppPaths.LogDirectory` always returns this path regardless of `BuildChannel.AppDataFolder`. Logs are **never split by channel** — the unified corpus is required for fine-tune dataset use. The `.Data` suffix is mandatory because the unsuffixed directory is owned and replaced by Velopack.

### Line format

Every line written by `DiagnosticsLogger.Log()`:

```
{ISO8601 timestamp} channel={prod|dev} app_version={semver} pid={int} {event_name} {key=value ...}
```

`DiagnosticsLogger.LogData(eventName, obj)` appends a JSON-serialized object after the event name on the same line.

### Required fields on every line

| Field | Source |
|---|---|
| `channel` | `BuildChannel.ChannelName` — `"prod"` or `"dev"` |
| `app_version` | `BuildChannel.AppVersion` — semver for prod, and the same base version with `-dev` appended for dev (for example `0.1.6-dev`) |
| `pid` | `Process.GetCurrentProcess().Id` — disambiguates two simultaneously running channels |

### Concurrent write safety

`DiagnosticsLogger.AppendWithRetry` opens the file with `FileMode.Append + FileShare.ReadWrite` and retries up to 5 times with linear backoff (10ms × attempt) on `IOException`. A per-instance `lock` prevents concurrent writes from the same process. The combination handles two channels (Prod + Dev) appending to the same file without data loss.

### Key log events

| Event | When |
|---|---|
| `started` | App boot; includes `channel`, `version`, `hotkey_vk` |
| `hotkey_pressed` | Every hotkey activation |
| `run_started` | Pipeline begins; includes `active_process`, `window_title` |
| `capture_succeeded` / `capture_failed` | After clipboard capture attempt. On failure, the `capture_failed` entry in `spellcheck_detail.events[]` carries per-attempt forensics: clipboard sequence numbers (`seq_before`/`seq_at_timeout`), physical modifier state (`mods_at_send`/`mods_at_timeout`), and foreground process + elevation (`fg_at_timeout`). See `docs/watchlist.md` § Capture-failure forensics |
| `capture_history_excluded` / `capture_history_exclude_failed` | Whether the captured (incorrect) text was tagged out of Windows clipboard history |
| `guard_rejected reason=already_running` | Overlapping hotkey press |
| `request_failed` / `request_retrying` | API errors |
| `replace_succeeded` | Full pipeline success; includes all timing fields |
| `paste_failed` | Focus changed before paste, or Ctrl+V failed |
| `spellcheck_detail` | JSON blob with full input/output/tokens/timings on every run |
| `replacements_reloaded` / `replacements_reload_failed` | Replacements file change detection |
| `update_check_start` / `update_download_done` / `update_apply_now` | UpdateService flow |
| `finalize_failed` | The post-run finalize step threw — the run's `run_completed`/`spellcheck_detail` lines were **lost**. Logs status, active process, and full stack. Treat as a missing-telemetry bug |
| `dashboard_open step=construct|show|activate|done` | Dashboard lifecycle |
| `loading_overlay_show` / `loading_overlay_hide` | Overlay visibility |
| `stopping` | Clean shutdown |

### `spellcheck_detail` fields

Each run emits a `spellcheck_detail` JSON blob containing: `status`, `error`, `model`, `active_app`, `active_exe`, `paste_target_app`, `paste_target_exe`, `paste_method`, `corrected_text_on_clipboard`, `original_clipboard_restored`, `captured_text_history_excluded`, `history_exclude_detail`, `text_changed`, `input_text`, `input_chars`, `output_text`, `output_chars`, `raw_ai_output`, `clipboard_html` (see below), `raw_response`, `request_payload`, `tokens` (input/output/total/cached/reasoning), `timings` (clipboard_ms, after_copy_format_ms, before_paste_format_ms, payload_ms, request_ms, api_ms, request_send_ms, request_wait_ms, response_download_ms, parse_ms, replacements_ms, prompt_guard_ms, paste_ms, total_ms), `replacements` (count/applied/protected_values plus per-kind protected counts), `prompt_leak` (triggered/occurrences/text_input_removed/removed_chars/before_length/after_length), the backward-compatible `terminal_normalization` object, `target_formatting` (rule/match identity plus per-hook application, character counts, stable operations, and failures), and `events[]`.

`target_formatting` may contain a parsed hostname for a site rule. It never contains a raw browser
path, query, fragment, page title, selected text, or extension message.

### `clipboard_html`

Every capture reads both clipboard flavors. `input_text` is the `CF_UNICODETEXT` flavor;
`clipboard_html` is the `CF_HTML` flavor of the same selection, verbatim including its header, or
`""` when the source offered none (Notepad, terminals, most code editors). Siblings
`clipboard_html_chars` (true pre-truncation size) and `clipboard_html_truncated` accompany it; the
logged field is capped at 512K chars so one pathological selection cannot produce a multi-megabyte
log line.

This is the input to the rich-text pipeline (`.planning/rich-text-clipboard-pipeline.md`). Nothing
downstream consumes it yet — the plain-text path is unchanged — but it is logged from now on so the
representation and model comparison runs against real captured markup.

`logs.py --has-html` filters to runs that carried markup. The formatted view prints only the size;
`--json` includes the markup. Plain `--grep-detail` deliberately does not search this field (it would
match CSS noise on nearly every row) — scope it with `clipboard_html:<needle>`.

### Dashboard Activity feed

The WPF **Home** page (`ActivityPage`) renders successful `spellcheck_detail` rows from the unified log directory:

- **Included:** `status == "success"` with non-empty `input_text` and `output_text`
- **Diff display:** uses `input_text` vs `output_text` when `text_changed` is true; otherwise shows `output_text` only
- **Timing breakdown:** hover a row and click the clock button to expand its persisted pipeline timings (clipboard capture, request construction, AI send/wait/download, post-processing, paste, and total). Older rows without a `timings` object do not show the button.
- **Stats bar:** all-time counts from every `spellcheck-*.jsonl` file (checks = success lines; corrections = success lines with `text_changed: true`)
- **Pagination:** 30 entries per page, newest first; not a live tail — refresh or scroll to see new/historical data

For visual layout and interaction (hover actions, inline diff, infinite scroll), see `DESIGN.md` § Home (Activity) and `docs/architecture.md` § WPF dashboard.
