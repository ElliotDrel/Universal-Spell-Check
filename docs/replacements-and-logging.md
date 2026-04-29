# Replacements, Prompt-Leak Guard, and Logging

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
- Before replacements, extracts `https?://\S+` URLs into `__URL_N__` placeholders; restores them afterward. Scheme-less links are not protected.
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
%LocalAppData%\UniversalSpellCheck\logs\spellcheck-{yyyy-MM-dd}.jsonl
```

`AppPaths.LogDirectory` always returns this path regardless of `BuildChannel.AppDataFolder`. Logs are **never split by channel** — the unified corpus is required for fine-tune dataset use.

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
| `app_version` | `BuildChannel.AppVersion` — semver for prod, `"0.0.0-dev"` for dev |
| `pid` | `Process.GetCurrentProcess().Id` — disambiguates two simultaneously running channels |

### Concurrent write safety

`DiagnosticsLogger.AppendWithRetry` opens the file with `FileMode.Append + FileShare.ReadWrite` and retries up to 5 times with linear backoff (10ms × attempt) on `IOException`. A per-instance `lock` prevents concurrent writes from the same process. The combination handles two channels (Prod + Dev) appending to the same file without data loss.

### Key log events

| Event | When |
|---|---|
| `started` | App boot; includes `channel`, `version`, `hotkey_vk` |
| `hotkey_pressed` | Every hotkey activation |
| `run_started` | Pipeline begins; includes `active_process`, `window_title` |
| `capture_succeeded` / `capture_failed` | After clipboard capture attempt |
| `guard_rejected reason=already_running` | Overlapping hotkey press |
| `request_failed` / `request_retrying` | API errors |
| `replace_succeeded` | Full pipeline success; includes all timing fields |
| `paste_failed` | Focus changed before paste, or Ctrl+V failed |
| `spellcheck_detail` | JSON blob with full input/output/tokens/timings on every run |
| `replacements_reloaded` / `replacements_reload_failed` | Replacements file change detection |
| `update_check_start` / `update_download_done` / `update_apply_now` | UpdateService flow |
| `dashboard_open step=construct|show|activate|done` | Dashboard lifecycle |
| `loading_overlay_show` / `loading_overlay_hide` | Overlay visibility |
| `stopping` | Clean shutdown |

### `spellcheck_detail` fields

Each run emits a `spellcheck_detail` JSON blob containing: `status`, `error`, `model`, `active_app`, `active_exe`, `paste_target_app`, `paste_target_exe`, `paste_method`, `text_changed`, `input_text`, `input_chars`, `output_text`, `output_chars`, `raw_ai_output`, `raw_response`, `request_payload`, `tokens` (input/output/total/cached/reasoning), `timings` (clipboard_ms, payload_ms, request_ms, api_ms, parse_ms, replacements_ms, prompt_guard_ms, paste_ms, total_ms), `replacements` (count/applied/urls_protected), `prompt_leak` (triggered/occurrences/text_input_removed/removed_chars/before_length/after_length), `events[]`.
