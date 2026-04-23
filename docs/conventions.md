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

## Error handling
- `try { ... } catch Error as e { ... }` wraps fallible ops.
- Silent failures for optional paths (logging, replacements, clipboard history policy).
- Resource cleanup via `try { ... } finally { ... }`.
- HTTP timeouts: 5s connect / 5s response / 30s total.
- On 4xx/5xx, capture raw error body for root-cause analysis.
- User-facing tooltip for critical failures: `ToolTip("API Error: " . status)`.

## Comments
Write only for: complex algorithms, non-obvious performance decisions, workarounds/known limits, critical state transitions, per-app behavior. Otherwise skip.

## Performance conventions
- Regex over JSON parsing when extracting a single field from predictable responses.
- Case-sensitive `StrReplace(..., true)` for brand replacements.
- URL placeholders extracted before replacements, restored after.
- Every major stage timestamped with `A_TickCount`; deltas logged as integer ms.
- Primary + fallback parsing paths, both instrumented.
