# Replacements, Prompt-Leak Guard, and Logging

## Target formatting hooks

`TargetFormattingPipeline` resolves one rule from its explicit ordered list after clipboard-history
exclusion. Its after-copy hook runs before literal protection and the API request. After the existing
post-processing restores protected literals, the optional before-paste hook runs against a second set
of formatter-neutral private-use placeholders. Missing or duplicated placeholders skip that optional hook and paste the corrected text;
an unexpected hook exception retains the unmodified text and is recorded asynchronously.

Unmatched runs perform only the short resolver scan and a null branch. They do not perform the second
literal-protection scan. Headless benchmark runs remain unmatched unless a test explicitly supplies a
target context.

## Terminal input normalization (pre-processing)

Before the API call, `TerminalFormattingRule.AfterCopy` is invoked when the active process is a terminal (`WindowsTerminal`, `Code`, `powershell`, `pwsh`, `cmd`, `bash`). It removes Windows Terminal's `▎` wrap marker, then repairs wrapped literals before normalizing soft-wrap artifacts while preserving intentional structure:

1. **Terminal marker** (`▎`) → empty — removes the copied terminal UI marker before line handling.
2. **Double CRLF** (`\r\n\r\n[ \t]*`) → `\n\n` — preserves paragraph/section breaks.
3. **List items** (`\r\n[ \t]+` before `-`, `*`, `•`, or `N.`) → `\n` — preserves bullet and numbered list structure.
4. **Wrapped file paths** — a soft wrap inside a recognized file path is removed before generic line handling, so the path stays continuous and is protected from the model.
5. **Soft-wrap continuation** (` *\r\n[ \t]+`) → ` ` — collapses lines that wrapped purely due to terminal width.

Tabs are matched alongside spaces in the three line-handling passes. Bare `\r\n` without trailing whitespace is left untouched.

- Applied on the **hot path only** (`ExecuteHotPathAsync`). Headless/bench runs are unaffected.
- Bare `\r\n` without trailing whitespace is left untouched.
- Result is recorded through the common formatting result; the legacy `terminal_normalized=true terminal_norm_chars_removed=N` line fields and `terminal_normalization` JSON object remain unchanged.
- If not applied (non-terminal process or no artifacts found), `terminal_normalization.applied = false` and `chars_removed = 0`.

Full pipeline order: **resolve target** → **after-copy format** → **protect literals** → API call →
post-process (replacements + prompt-leak guard) → **restore literals** →
**before-paste format with protected literals** → **paste into the currently active app**.

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
| `paste_failed` | Clipboard setup or Ctrl+V failed |
| `spellcheck_detail` | JSON blob with full input/output/tokens/timings on every run |
| `replacements_reloaded` / `replacements_reload_failed` | Replacements file change detection |
| `update_check_start` / `update_download_done` / `update_apply_now` | UpdateService flow |
| `finalize_failed` | The post-run finalize step threw — the run's `run_completed`/`spellcheck_detail` lines were **lost**. Logs status, active process, and full stack. Treat as a missing-telemetry bug |
| `dashboard_open step=construct|show|activate|done` | Dashboard lifecycle |
| `loading_overlay_show` / `loading_overlay_hide` | Overlay visibility |
| `stopping` | Clean shutdown |

### `spellcheck_detail` fields

Each run emits a `spellcheck_detail` JSON blob containing: `status`, `error`, `model`, `active_app`, `active_exe`, `paste_target_app`, `paste_target_exe`, `paste_method`, `corrected_text_on_clipboard`, `original_clipboard_restored`, `captured_text_history_excluded`, `history_exclude_detail`, `text_changed`, `input_text`, `input_chars`, `output_text`, `output_chars`, `paste_text`, `paste_html` (see below), `raw_ai_output`, `clipboard_html` (see below), `rich_text` (source HTML presence, safe-rewrite attempt/result, reason, and paragraph count), `raw_response`, `request_payload`, `tokens` (input/output/total/cached/reasoning), `timings` (clipboard_ms, after_copy_format_ms, before_paste_format_ms, payload_ms, request_ms, api_ms, request_send_ms, request_wait_ms, response_download_ms, parse_ms, replacements_ms, prompt_guard_ms, paste_ms, total_ms), `replacements` (count/applied/protected_values plus per-kind protected counts), `prompt_leak` (triggered/occurrences/text_input_removed/removed_chars/before_length/after_length), the backward-compatible `terminal_normalization` object, `target_formatting` (rule/match identity plus per-hook application, character counts, stable operations, and failures), and `events[]`.

`target_formatting` may contain a parsed hostname for a site rule. It never contains a raw browser
path, query, fragment, page title, selected text, or extension message.

### `clipboard_html`, `clipboard_rtf`, `clipboard_formats`

Every capture reads all the flavors the source offered. `input_text` is `CF_UNICODETEXT`;
`clipboard_html` is `CF_HTML` verbatim including its header; `clipboard_rtf` is the RTF flavor. Both
rich fields are `""` when absent, carry `_chars` (true pre-truncation size) and `_truncated` siblings,
and are capped at 512K chars so one pathological selection cannot produce a multi-megabyte log line.

`clipboard_formats` is the comma-joined list of every format name present at capture. **This is the
field that makes an empty `clipboard_html` interpretable** — without it, "the app offered no markup"
and "the app offered markup we failed to read" are indistinguishable after the fact. Chrome offers
`HTML Format,UnicodeText,Chromium internal source RFH token,Chromium internal source URL,Locale,Text,OEMText`;
a plain-text source offers only the text formats.

Note on identifying the source page: `CF_HTML`'s header can carry a `SourceURL:` line, but it is
**not reliable** — observed present on a `file://` copy and absent on a real ChatGPT copy from the
same browser. The `Chromium internal source URL` format is also unusable: it reads back empty from
another process. Neither replaces the Chrome extension bridge in
`.planning/app-site-formatting-customizations.md`.

This is the input to the rich-text pipeline (`.planning/rich-text-clipboard-pipeline.md`). A narrow
safe path now handles ChatGPT's simple `data-pm-slice` paragraph fragments: if every non-empty source
paragraph exactly maps to the copied text and the corrected output has the same paragraph count, the
app replaces only text-node contents and writes both `CF_HTML` and Unicode text. Empty paragraphs stay
as markup, preventing their browser plain-text serialization from becoming extra pasted lines. One
ordered or unordered list item, including an indented nested item, also qualifies only when the
generated Unicode indentation and list marker are identical before and after correction. The generated prefix is
stripped and only the corrected item body is pasted as Unicode text because ChatGPT inserts any HTML
block as a nested item. A single inline `<span>` is
accepted around the item body. Mixed spans, links, and lists next use text-node alignment: each original
HTML text node must map in order to the copied Unicode text, and every model edit must belong to a
mapped node. The output retains the original tags and attributes. `rich_text.mode=aligned_html`
identifies this path, while `html` and `list_body_text` identify the narrower paths. If alignment
declines, corrected Unicode text still pastes with any HTML text nodes omitted from the source Unicode flavor,
and `rich_text.reason` records why. If reconstruction throws, the corrected Unicode text still pastes.
An exact
model echo followed by `---` and a corrected copy is removed before replacements and placeholder
restoration; the run records `echoed_original_removed`. The exact two-level fragment produced when
a parent item and its one nested child are selected is reconstructed as `nested_list_html`; both
generated prefixes and both source bodies must match before either HTML text node is replaced.
Larger simple list trees and bare `<li data-pm-slice>` subtrees use `structured_list_html`: every HTML
paragraph must map exactly to one generated Unicode line, and the corrected output must retain the
same ordered or unordered prefix at
each position. The app removes surplus blank lines inserted between prefixed items, reconstructs the
source newline sequence, and replaces only paragraph text in the original HTML tree. Unknown markup,
changed prefixes, missing lines, and nonblank extra lines still fall back instead of being guessed.

Dev rich-text runs also read the replacement clipboard back immediately before the paste. The
`rich_text` object records requested HTML size, whether the clipboard contained the exact generated
CF_HTML (`exact_match`, `missing_html`, or `mismatch`), its readback size, and its post-write formats.
Prod skips this diagnostic readback so it adds no production hot-path work.

`output_text` is the corrected text before rich-text replacement. `paste_text` and `paste_html` are
the final Unicode and CF_HTML strings requested in the clipboard write, including any text recovered
from source HTML. Each has `_chars` (full length) and `_truncated` siblings; the logged strings are
capped at 512K characters. An empty `paste_html` means the app requested a plain-text paste. These
fields describe the requested payload, even if the write or paste later fails. They do not prove how
the destination editor rendered the result. Older runs lack these fields.

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

## Optional developer evidence (schema 1)

Settings → Logs → Developer logging is off by default in both channels, including Production.
Changing it applies to the next correction without restarting. Disabled corrections keep their
existing clipboard and diagnostic behavior. Headless/benchmark requests do not capture the desktop.

Each detail record has `run_id` and `developer_logging_enabled`. Enabled runs add `developer_evidence` with `schema_version`,
`manifest_path` relative to the shared log directory, and `status`. Busy evidence admission is reported explicitly. The app writes evidence after
the correction returns, so a reader may need to retry briefly while finalization runs.

Evidence lives in `logs/developer-evidence/<run_id>/manifest.json`. `payloads` contains full
UTF-8 sidecars for `input_text`, `clipboard_html`, `clipboard_rtf`, `clipboard_formats`,
`raw_ai_output`, `output_text` (the rich mapper input), `paste_text`, `paste_html`, and
`readback_text/html/rtf/formats`. Entries expose relative `path`, SHA-256 `sha256`, UTF-8 `bytes`,
.NET UTF-16 `chars`, `complete` and `status`. Empty source markup is a complete empty payload;
null unavailable values are incomplete. Inline 512K markup caps remain, but the sidecars preserve
larger ordinary selections. A 16MiB per-payload cap and 256MiB total evidence store cap explicitly
return `size_limit` / `storage_limit` instead of deleting existing data. Evidence is local and can
contain selected text and surrounding editor content; API key/settings files are never included.

`contexts` links automatic `before-source` and `after-paste` captures with requested/captured/completed
timestamps and target process/PID/HWND/title. Images contain visible screen pixels within the
foreground window bounds (screen-clipped), and can include overlays; they are not DOM snapshots.
The after capture requests a 150ms settling delay, which does not prove editor rendering is finished.
Foreground changes and late source captures are explicitly labeled. UIA reports focused control
identity, up to 32768 characters of document text and 8 selected ranges of up to 8193 characters.
Changed paste destinations explicitly label unavailable destination-before context.
Text/range truncation, unsupported selection, password controls, provider errors, busy workers and
750ms timeouts are explicit. Selection offsets are not reconstructed.

UIA runs on a dedicated MTA background thread, with at most one outstanding provider call in this
process. A stuck provider causes later captures to be skipped, preventing accumulating orphan workers.
Image capture/encoding/storage also runs off the correction thread and accepts only one image job
at a time. The correction never waits for UIA or screenshot completion. Clipboard readback uses one
best-effort background STA worker with a 500ms deadline and clipboard sequence checks. A stuck read
blocks later readback jobs rather than accumulating workers. Absent formats, failures and races are
explicit; a successful read proves the clipboard state at its timestamp, not the editor rendering.
Evidence admission permits at most two in-flight runs. Shared cross-channel quota reservations
are serialized with a named mutex; a failed write may conservatively overcount space.

`--developer-evidence-smoke` runs the rebuilt native executable with a disposable RichTextBox,
real Ctrl+C/Ctrl+V, enabled readback and automatic images, disabled readback, live settings cache
updates using disposable settings, and a 600000-character sidecar check. It restores the prior
clipboard and exits 0/1. It does not call the model or modify a live user document. Evidence remains
in the shared logs for inspection, as it would for an ordinary enabled run.

`--developer-evidence-tests` runs deterministic payload integrity, UTF-16 character counts,
large sidecar, offered-but-unreadable source markup, per-payload limit, full-store and bounded-admission
checks in a disposable temporary directory, without interacting with the desktop.

### Verified on 2026-10-07

- Both Dev and Release compiled with the pinned .NET 10.0.401 SDK.
- Release `--developer-evidence-tests` passed UTF8/SHA256 full sidecar roundtrip (600000+
  characters and emoji), UTF16 counts, source-read incompleteness, 16MiB size limit,
  exhausted 256MiB store and two-run admission checks.
- Native Dev disposable RichTextBox smoke passed actual Ctrl+C/Ctrl+V, RTF source capture,
  before/after screenshots, readback and live enable/disable cache checks (PID 21332).
  Later fixture attempts correctly failed when desktop focus/selection was unavailable;
  the fixture reports these as errors rather than silently claiming a paste occurred.
- The actual Dev dashboard enabled the setting and the normal coordinator corrected a disposable
  Notepad selection using Ctrl+Alt+D (PID 24924, run `ea496bc4082448c2ba8a4a9fcd5da874`).
  The manifest contained timely before/after images, original/corrected UIA document text,
  original selection, correct readback and complete empty source HTML/RTF where absent.
  The visible Notepad result changed `teh` to `the`.

The final rebuilt Dev native smoke passed again (PID 13196, run
`7deef858b13a4cd3b05d77ba20abce96`, capture scheduling 3ms). The actual Dev dashboard then
disabled the setting and a second normal Notepad correction succeeded without developer evidence
(run `7ad4a5999ea54a93ab2bfee465f87bf6`). Dev settings were left disabled.
