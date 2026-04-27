# Code Conventions

## Files
- PascalCase with spaces for active scripts: `Universal Spell Checker.ahk`.
- lowercase+underscores for config/tools: `replacements.json`, `generate_log_viewer.py`.
- JSONL logs: `spellcheck-YYYY-MM-DD-to-YYYY-MM-DD.jsonl`.

## AutoHotkey
- camelCase for functions and variables: `GetClipboardText()`, `originalText`.
- Private helpers prefixed `__`: `__ReadClipboardString()`, `__JsonParseValue()`.
- Output parameters via `&paramName` (AHK v2 by-ref).
- 4-space indent, opening braces on same line, no hard line limit (~100 char typical).
- Global state only for module config (`modelModule`, `apiKey`, `enableLogging`).

## JSON keys in output
- snake_case: `input_text`, `output_text`, `text_changed`, `model_version`.

## Python (log viewer)
- PEP 8, 4-space indent. No type hints. Stdlib only. Docstrings for public funcs.

## C# / Native WinForms
- Project lives under `native/UniversalSpellCheck`.
- Use file-scoped namespace `namespace UniversalSpellCheck;`.
- Use PascalCase for types and public members; use `_camelCase` for private readonly fields.
- Keep the app single-process and resident; do not introduce helper processes without measured need.
- UI should stay minimal: tray menu, small settings form, and the bottom-center loading overlay.
- Keep capture/request/paste serialized through the coordinator; do not queue hotkey presses.
- Use `try/finally` around busy state so the loading overlay always hides.
- Keep `HttpClient` app-lifetime, not per request.
- Store API keys only through DPAPI current-user storage; never write them to `settings.json`.

## Error handling
- `try { ... } catch Error as e { ... }` wraps fallible ops.
- Silent failures for optional paths (logging, replacements, clipboard history policy).
- Resource cleanup via `try { ... } finally { ... }`.
- HTTP timeouts: 5s connect / 5s response / 30s total.
- On 4xx/5xx, capture raw error body for root-cause analysis.
- User-facing tooltip for critical failures: `ToolTip("API Error: " . status)`.
- Native user-facing failures use `NotifyIcon.ShowBalloonTip`; request failures must not paste over the selection.

## Comments
Write only for: complex algorithms, non-obvious performance decisions, workarounds/known limits, critical state transitions, per-app behavior. Otherwise skip.

## Performance conventions
- Regex over JSON parsing when extracting a single field from predictable responses.
- Case-sensitive `StrReplace(..., true)` for brand replacements.
- URL placeholders extracted before replacements, restored after.
- Every major stage timestamped with `A_TickCount`; deltas logged as integer ms.
- Primary + fallback parsing paths, both instrumented.
- Native logs should include enough timing to distinguish capture, request, post-processing, and paste costs.
