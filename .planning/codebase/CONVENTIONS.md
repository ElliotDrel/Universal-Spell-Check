# Coding Conventions

**Analysis Date:** 2026-03-27

## Naming Patterns

**Files:**
- PascalCase with spaces: `Universal Spell Checker.ahk` (active script)
- Variant scripts with dashes: `Universal Spell Checker - SEND TEXT instead of ctr+v.ahk` (legacy)
- Configuration files lowercase with underscores: `replacements.json`, `generate_log_viewer.py`
- Log output in lowercase: `spellcheck.jsonl` (JSONL format)

**Functions:**
- camelCase for all functions: `GetClipboardText()`, `ApplyReplacements()`, `LogDetailed()`, `RotateLogIfNeeded()`
- Private/internal functions prefixed with double underscore: `__ReadClipboardString()`, `__ExtractHtmlFragment()`, `__JsonParseValue()`, `__JsonParseNumber()`
- Helper functions grouped logically near their public wrappers
- Single-purpose functions with clear responsibilities (e.g., `GetUtf8Response()` handles only UTF-8 stream conversion)

**Variables:**
- camelCase for local/global variables: `originalText`, `correctedText`, `logData`, `apiKey`, `apiUrl`
- Constants in uppercase with camelCase fallback: `enableLogging`, `maxLogSize`, `replacementsPath` (per-module constants defined at top)
- Descriptive names over abbreviations: `originalText` not `origText`, `urlCount` not `cnt`
- Object properties use camelCase: `logData.original`, `logData.pasteTime`, `logData.timings.clipboardCaptured`
- Array properties descriptive: `postReplacements`, `applied`, `events`
- Timing objects nested: `timings.clipboardCaptured`, `timings.requestSent` (millisecond timestamps)

**Types:**
- Implicit typing (AutoHotkey v2)
- Boolean flags explicit: `apiUsesReasoning`, `textChanged`, `pasteAttempted`
- JSON object keys use snake_case in output (for logging/API): `input_text`, `output_text`, `text_changed`, `model_version`

## Code Style

**Formatting:**
- No automatic formatter configured (raw AHK file)
- Indentation: 4 spaces (visible in function bodies and control structures)
- Line length: no hard limit enforced, but kept reasonable (~100 chars typical)
- Opening braces on same line: `function {` pattern
- Spacing: consistent single space around operators and after commas

**Linting:**
- No linter configured for AutoHotkey code
- Code review focuses on:
  - Parameter validation and error handling
  - Performance-critical sections (see CONCERNS.md for perf notes)
  - Security (API key handling, prompt injection safeguards)
  - Timing instrumentation completeness

**Python Code (generate_log_viewer.py):**
- PEP 8 style with 4-space indentation
- Type hints not used (older Python compatibility)
- Docstrings for public functions: `"""..."""` format
- Imports: stdlib only (json, glob, os, sys, html, pathlib)
- Line length: ~80-100 chars

## Import Organization

**AutoHotkey:**
- No explicit imports (AutoHotkey v2 has built-in functions)
- Library-style functions: all defined in single script for performance
- No external .ahk includes (intentionally self-contained)

**Python:**
```python
# Order observed in generate_log_viewer.py:
import json         # stdlib - data format
import glob         # stdlib - file patterns
import os           # stdlib - file operations
import sys          # stdlib - sys.argv
import html         # stdlib - HTML escaping
from pathlib import Path  # stdlib - modern path handling
```

**Configuration files:**
- `replacements.json`: JSON key-value map (canonical → [variants])
- No path aliases or special resolution mechanisms

## Error Handling

**Patterns:**
- Try-catch wrapping fallible operations: `try { ... } catch Error as e { ... }`
- Silent failures for optional operations (replacements, logging, clipboard history policy)
- Error capture in `logData.error` for API failures
- User-facing tooltips for critical failures: `ToolTip("API Error: " . status)`
- Detailed event logging for all error paths: `logData.events.Push("Exception thrown: " . message)`
- Comprehensive try-finally for resource cleanup (HTTP object, COM objects, clipboard operations)

**Specific patterns:**
- Clipboard operations: `try { ... } finally { DllCall("CloseClipboard") }`
- HTTP timeouts: 5s connection, 5s response, 30s total (defensive)
- JSON parse errors: caught separately, logged with position info
- Regex extraction: method 1 (fast), fallback to method 2 (compatible) with full debug logging
- Critical vs optional errors:
  - Critical: API response parsing, text insertion
  - Optional: log rotation, clipboard history tagging, post-processing replacements

## Logging

**Framework:** Console-free; all logging to JSONL format

**Patterns:**
- Structured logging: one JSON object per line in `logs/spellcheck.jsonl`
- Timestamp format: `"yyyy-MM-dd HH:mm:ss"` (human-readable, not Unix time)
- Event log: `logData.events` array captures sequence of operations and debug info
- Timing instrumentation: delta-millisecond values (`clipboard_ms`, `api_ms`, `parse_ms`, etc.)
- Token tracking: `tokens.input`, `tokens.output`, `tokens.total`, `tokens.cached`, `tokens.reasoning`
- Replacements tracking: count + list of applied variants
- Prompt-leak guard: triggered flag + occurrence count + character delta
- Raw capture: `raw_request` (JSON payload sent) and `raw_response` (full API response) for debugging
- Log rotation: automatic at 1MB with timestamp suffix (e.g., `spellcheck-2026-03-27-123456.jsonl`)

**When logged:**
- On hotkey press (`Ctrl+Alt+U`): initialize `logData`
- After clipboard read: `clipboard_captured` timing
- After payload construction: `payload_prepared` timing
- After HTTP send: `request_sent` timing
- After HTTP receive: `response_received` timing, token counts extracted
- After parse: `text_parsed` timing, debug events for both regex and Map parsing
- After replacements: `replacements_applied` timing, count/list of changes
- After prompt-leak guard: `prompt_guard_applied` timing, trigger details
- After text insertion (paste or SendText): `text_pasted` timing, final state
- On any error: error message + relevant context captured

## Comments

**When to Comment:**
- Complex algorithms (e.g., HTML fragment extraction, surrogate pair handling in JSON parsing)
- Non-obvious performance decisions (e.g., "regex extraction ~10x faster than JSON parsing")
- Workarounds and known limitations (e.g., URL extraction placeholders)
- Critical state transitions (e.g., "Create the prompt from shared instruction text")
- Per-app behavior (e.g., `sendTextApps` configuration comment)

**JSDoc/TSDoc:**
- Not used (AutoHotkey doesn't have built-in TSDoc support)
- Function documentation via inline comments above definition
- Parameter documentation in comment block before function

**Example from code:**
```autohotkey
; Load post-processing replacements from replacements.json.
; JSON format: { "canonical": ["variant1", "variant2", ...], ... }
; Builds a flat list of [variant, canonical] sorted longest-first so longer
; phrases are replaced before any shorter substring could interfere.
LoadReplacements() {
    ...
}
```

## Function Design

**Size:**
- Most functions 10-50 lines (focused)
- Larger functions (100+ lines) reserved for parsing, logging, hotkey handler
- Hotkey handler at ~300 lines combines multiple stages (acceptable for main flow)

**Parameters:**
- Functions use `&paramName` for output parameters (AutoHotkey v2 pass-by-reference)
- Examples: `ApplyReplacements(text, &applied, &urlCount)`, `StripPromptLeak(text, promptText, &details)`
- Global state only for module config: `modelModule`, `apiKey`, `enableLogging`

**Return Values:**
- Strings for text extraction/transformation
- Objects/Maps for structured data (parsed JSON, timing info)
- Booleans for flags and checks
- Implicit return on error conditions (e.g., `return ""` when clipboard unavailable)

**Naming conventions for helper functions:**
- Parsing: `__JsonParseValue()`, `__JsonParseObject()`, `__JsonParseString()`
- Extraction: `__ExtractHtmlFragment()`, `__ReadClipboardString()`
- Escape/unescape: `JsonEscape()` (public for logging), escape sequences handled inside parsers
- Utility: `__SetClipboardDwordFormat()`, `__JsonSkipWhitespace()`

## Module Design

**Exports:**
- No explicit module exports (single-file design)
- Public API: hotkey handler `^!u::`, callable functions for testing

**Barrel Files:**
- Not used (self-contained script)

**Code Organization:**
1. Global configuration (lines 1-75): `#Requires`, logging, API, model selection
2. Utility functions (lines 76-350): replacements, clipboard, logging setup
3. JSON processing (lines 351-718): parsing, escaping, number conversion
4. Response extraction (lines 719-798): regex and object-based alternatives
5. Hotkey handler (lines 800-1102): main spell-check workflow

**Initialization:**
- `#Requires AutoHotkey v2.0`: version constraint at file top
- Directory creation: `DirCreate(A_ScriptDir . "\logs")` in global scope
- Model selector: `modelModule := "gpt-4.1"` (single source of truth)
- No global state initialization needed beyond config

**Python module structure (generate_log_viewer.py):**
- Constants: `SCRIPT_DIR`, `LOGS_DIR`, `OUTPUT_FILE`
- Functions: `load_entries()`, `compute_stats()`, main `main()` with `if __name__ == "__main__"`
- HTML template: inline multiline string `HTML_TEMPLATE`

## Performance Conventions

**Speed First:**
- Regex extraction preferred over JSON parsing for response text (single pattern match vs recursive traversal)
- Microsecond-level timing on replacements: `StrReplace(..., true)` with case-sensitive flag
- URL extraction uses placeholders to avoid regex re-running on protected URLs
- Post-replacement JSON rebuilding avoided (direct string append for logging)

**Timing instrumentation:**
- Every major operation timestamped: `A_TickCount` milliseconds
- Delta calculations post-operation: `tClip := (clipboardCaptured > 0) ? (clipboardCaptured - startTime) : 0`
- Timing deltas logged as integer milliseconds, no floating-point precision

**Fallback strategy:**
- Primary: regex extraction (fast, stateless)
- Secondary: Map-based JSON parsing (correct, verbose logging on failure)
- Both paths instrumented for debugging

---

*Convention analysis: 2026-03-27*
