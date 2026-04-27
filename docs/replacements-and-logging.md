# Replacements, Prompt-Leak Guard, and Logging

## Replacements system (`replacements.json`)

Maps canonical -> variants:
```json
{
  "GitHub": ["Git Hub", "git hub", "GitHUB", "GITHUB", "Github", "github"],
  "OpenAI": ["Open AI", "Open Ai", "open ai", "openai"]
}
```

Functions:
- `LoadReplacements()` - parses JSON, strips BOM, flattens to `[variant, canonical]`, case-sensitive `StrCompare(..., true)` keeps case-only variants (e.g. `night shift` vs `Night Shift`), sorts longest-first. Caches modified-time + size.
- `RefreshReplacementsIfChanged(&status, &details)` - reparses only when mtime or size differs. Keeps last known-good cache on failure. Clears cache (no deferred retry) if file is missing.
- `RetryReplacementsReloadAfterPaste(events)` - retries a failed reload after paste and logs the outcome.
- `ApplyReplacements(text, &applied, &urlCount)` - extracts `https?://\S+` into `__URL_N__` placeholders (scheme-less links NOT protected), runs case-sensitive `InStr(..., true)` + `StrReplace(..., true, &count)`, restores URLs.
- `StripPromptLeak(text, promptText, &details)` - simple string check: removes `"instructions: " . promptText` when present, then strips a leading `text input:` label.

Timing captured in `timings.replacementsApplied` and `timings.promptGuardApplied`, logged as delta-ms.

## Native replacements and prompt guard

The native candidate ports the same core behavior in `native/UniversalSpellCheck/TextPostProcessor.cs`.

Native behavior:
- Reads `replacements.json` via `AppPaths.ReplacementsPath`; when run from the repo/publish folder, it walks parent directories until it finds the repo file.
- Parses canonical -> variants JSON, strips BOM, flattens to variant -> canonical pairs, and sorts longest-first.
- Uses case-sensitive ordinal comparisons/replacements.
- Protects `https?://\S+` URLs with `__URL_N__` placeholders before replacements and restores them afterward. Scheme-less links are not protected, matching the AHK behavior.
- Reloads replacements when modified-time or size changes.
- Clears cache when the file is missing; keeps last known-good cache on reload failure.
- Strips leaked `instructions: <PromptInstruction>` and then a leading `text input:` label before paste.

Native differences:
- Native logs are focused line logs, not the AHK weekly JSONL schema.
- Native currently logs replacement counts and prompt-leak status, but not the full applied replacement array.
- Native reload failure retry after paste is not implemented; it keeps last known-good cache and logs the failure.

Native log fields for post-processing:
- `postprocess_duration_ms`
- `replacements_count`
- `urls_protected`
- `prompt_leak_triggered`
- `prompt_leak_removed_chars`

Current verification gap:
- Startup proves replacements load, e.g. `replacements_reloaded count=103`.
- A targeted run such as `open ai and github` should still be used to confirm `replacements_count > 0`.

### Watchlist
- **Repeated reload failures** (`immediate reload failed` / `deferred reload failed` in logs) -> malformed JSON, file locking, or save timing.
- **Same-size fast edits edge case** - cache key is mtime + size; a very fast same-size edit can be missed until a later detectable save. Potential future fix: stronger cache key or transactional read/metadata pass.
- **Read-while-writing risk** - if file is edited mid-read, stale data can be cached under newer metadata. Potential fix: capture metadata before and after read, only accept reload when both match.

## Logging (JSONL)

- **Format:** one JSON object per line in weekly files `logs/spellcheck-YYYY-MM-DD-to-YYYY-MM-DD.jsonl` (Monday-based week).
- **Rotation:** if appending would push the current week file past 5 MiB, spills into `-2`, `-3`, etc. for the same week (does NOT rename old logs).
- **Fields:** timestamp, status, error, duration_ms, model, script_version, model_version, active_app, active_exe, paste_method, text_changed, input_text, input_chars, output_text, output_chars, raw_ai_output, tokens (input/output/total/cached/reasoning), timings (clipboard/payload/request/api/parse/replacements/prompt_guard/paste ms), replacements (count/applied/urls_protected), prompt_leak (triggered/occurrences/text_input_removed/removed_chars/before_length/after_length), events[], raw_response.
- **Viewer:** `python generate_log_viewer.py` -> `logs/viewer.html` (`--open` to auto-launch).

## Native logging

Native logs live outside the repo:

```text
%LOCALAPPDATA%\UniversalSpellCheck\native-spike-logs\
```

Current native events include:
- `started hotkey=Ctrl+Alt+Y model=gpt-4.1 phase=5`
- `replacements_reloaded`
- `hotkey_pressed`
- `run_started`
- `capture_succeeded`
- `capture_failed`
- `request_failed`
- `request_retrying`
- `replace_succeeded`
- `guard_rejected`
- `run_failed`
- `stopping`

Current native timing/log fields include:
- `active_process`
- `window_title`
- `input_len`
- `output_len`
- `duration_ms`
- `capture_duration_ms`
- `request_duration_ms`
- `postprocess_duration_ms`
- `paste_duration_ms`
- `copy_attempts`
- `request_attempts`
- `status_code`
- `error_code`

Native logs are intentionally not viewer-compatible yet. Do not claim AHK JSONL parity until that is explicitly implemented.
