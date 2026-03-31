# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Universal Spell Checker is a minimalist AutoHotkey script that provides instant AI-powered spell checking across all Windows applications. The focus is on maximum speed and seamless operation with minimal overhead.

## Primary Implementation

The project uses a single active script with a top-level model selector.

### Active Files
- **Universal Spell Checker.ahk**: Primary/default script with `modelModule` selector (`gpt-4.1`, `gpt-5.1`, `gpt-5-mini`)
- **replacements.json**: Post-processing replacement pairs - format: `{ "canonical": ["variant1", "variant2", ...] }`
- **generate_log_viewer.py**: Reads `logs/*.jsonl` and generates `logs/viewer.html` — run `python generate_log_viewer.py` to view

### Legacy / Simple Variant
- **Universal Spell Checker - SEND TEXT instead of ctr+v.ahk**: Minimal script that types output via `SendText()` instead of clipboard paste; no logging or post-processing - kept for reference/fallback

### Common Features
- AutoHotkey v2.0 scripts optimized for performance
- Direct OpenAI API integration via Responses API
- Instant text replacement via clipboard (or `SendText()` per app)
- Global hotkey: Ctrl+Alt+U
- Structured JSONL logging with full timing breakdown, token counts, active app tracking
- Dual JSON parsing (regex primary, Map fallback)
- Post-processing replacements via `replacements.json`
- Prompt-leak safeguard to strip echoed instruction headers when they appear in model output
- Enhanced clipboard reading (HTML -> Unicode -> ANSI fallback chain)
- Per-app paste behavior configurable via `sendTextApps` list

## Architecture & Performance

### Core Design Principles
- **Speed First**: Every operation optimized for minimal latency
- **Simplicity**: Self-contained .ahk files with minimal external dependencies (`replacements.json` only)
- **Seamless**: Direct clipboard manipulation for instant text replacement
- **Minimal**: Only essential functionality to avoid performance overhead
- **Model Flexibility**: One file supports all target models via a top-level selector

### Text Processing Flow (Optimized)
1. User selects text and presses Ctrl+Alt+U
2. Replacements are loaded once at startup, then `replacements.json` is reparsed only when its modified timestamp or file size changes
3. Script uses the original fast single-copy path for normal apps, but gives Notepad a short settle delay and up to 3 quick `Ctrl+C` attempts
4. `GetClipboardText()` reads content, preferring HTML format to strip formatting noise
5. Sends text directly to OpenAI API
6. Parses response (regex primary -> Map-based fallback)
7. `ApplyReplacements()` fixes known brand/term casing in AI output
8. `StripPromptLeak()` removes accidental echoed instruction blocks (simple string check against `promptInstructionText`)
9. Replaces selected text via clipboard paste (Ctrl+V) or `SendText()` depending on active app

### OpenAI API Integration
- **Endpoint**: `https://api.openai.com/v1/responses` (all models use Responses API)
- **Timeout**: 30 seconds for API response
- **Prompt**: Optimized for grammar/spelling fixes while preserving formatting

#### Model-Specific Configurations

**gpt-4.1 (Standard Model)**:
- Uses `temperature` parameter (e.g., `0.3`)
- Uses `verbosity: "medium"` (does NOT support "low")
- Does NOT support `reasoning` block
- Payload: `{"model":"gpt-4.1", "input":[...], "store":true, "text":{"verbosity":"medium"}, "temperature":0.3}`

**gpt-5.1 (Reasoning Model)**:
- Uses `reasoning` block with `effort: "none"` (fastest)
- Uses `verbosity: "low"`
- Does NOT support `temperature`
- Payload: `{"model":"gpt-5.1", "input":[...], "store":true, "text":{"verbosity":"low"}, "reasoning":{"effort":"none","summary":"auto"}}`

**gpt-5-mini (Reasoning Model)**:
- Uses `reasoning` block with `effort: "minimal"` (unique to this model)
- Uses `verbosity: "low"`
- Does NOT support `temperature`
- Payload: `{"model":"gpt-5-mini", "input":[...], "store":true, "text":{"verbosity":"low"}, "reasoning":{"effort":"minimal","summary":"auto"}}`

#### Common Payload Structure
- `input`: array with `{role:"user", content:[{type:"input_text", text:"..."}]}`
- `store`: `true` (required for all models)

### Performance Optimizations
- Direct WinHTTP COM object for API calls
- **Regex-based JSON text extraction** (primary method - fastest)
  - Single RegEx match extracts corrected text directly
  - ~10x faster than full JSON object parsing
  - No object allocation or recursive traversal overhead
- **Map-based JSON parser** (`JsonLoad` - full recursive parser, fallback with debug logging)
  - Only used if regex extraction fails
  - Uses `Integer()`/`Float()` for AHK v2 compatibility
- **Post-processing replacements** (`replacements.json`)
  - Loaded once at startup and reparsed only when file metadata changes (modified time or size)
  - Failed reloads keep the last known-good cache instead of clearing replacements mid-run
  - If a metadata change or metadata read failure is detected, the script retries a full reload after paste and logs both attempts
  - If `replacements.json` is missing, the cache is cleared and no deferred retry is scheduled
  - Sorted longest-variant-first to prevent shorter substrings interfering
  - **URL protection**: `http://` and `https://` URLs (matched via `https?://\S+`) are extracted into placeholders before replacements run, then restored after — scheme-less links like bare `www.` or `example.com` are not protected
  - Runs in microseconds; logged with count and list of applied replacements
- **Prompt-leak safeguard** (`StripPromptLeak`)
  - Hardcoded detection for leaked `instructions: ... text input:` prefix echoed by the model
  - Uses a simple string check: if output contains the instruction prompt, remove it
  - Reuses the same `promptInstructionText` variable used to build the request prompt
  - Also removes a leading `text input:` label after stripping the leaked instruction block
  - Logs trigger status and before/after lengths via `promptLeakGuard` + `timings.promptGuardApplied`
- **Enhanced clipboard reading** (`GetClipboardText()`)
  - Prefers HTML clipboard format (strips empty paragraphs / formatting noise)
  - Falls back to Unicode (CF_UNICODETEXT), then ANSI (CF_TEXT)
- **Clipboard capture strategy** (`CaptureSelectedText()`)
  - Default apps keep the original single-attempt copy path for speed
  - `notepad.exe` gets a tiny first-attempt settle delay plus 2 short retries before failing
  - Logs the chosen strategy and which copy attempt succeeded or timed out so other app failures are clearly attributed
- **UTF-8 response reading** (`GetUtf8Response()` via ADODB.Stream)
  - Prevents mojibake on smart quotes and other non-ASCII characters
- **Per-app paste method** (`sendTextApps` / `UseSendText()`)
  - Default: clipboard + Ctrl+V (all apps)
  - Override: `SendText()` direct typing for apps listed in `sendTextApps`
- Log API error bodies on non-200 responses for quick root-cause checks
- Single clipboard operation for text replacement
- Hardcoded API key to avoid configuration delays

### Post-Processing Replacements System

`replacements.json` maps canonical forms to their common AI-output variants:

```json
{
  "GitHub": ["Git Hub", "git hub", "GitHUB", "GITHUB", "Github", "github"],
  "OpenAI": ["Open AI", "Open Ai", "open ai", "openai"]
}
```

- **`LoadReplacements()`**: Parses the JSON, strips UTF-8 BOM if present, flattens to `[variant, canonical]` pairs, and uses case-sensitive `StrCompare(..., true)` to keep case-only variants (for example `night shift` vs `Night Shift`) before sorting longest-first; updates cached modified-time and file-size metadata
- **`RefreshReplacementsIfChanged(&status, &details)`**: Checks `replacements.json` metadata against the cached modified-time and file-size values, reparses only when they differ, keeps the last known-good cache if a reload fails, and clears the cache without retry churn if the file is missing
- **`RetryReplacementsReloadAfterPaste(events)`**: Retries a full replacements reload after paste when the earlier metadata-driven reload attempt failed, and logs the outcome
- **`ApplyReplacements(text, &applied, &urlCount)`**: Extracts `http(s)://` URLs into `__URL_N__` placeholders first (scheme-less links not covered), then runs case-sensitive `InStr(..., true)` + `StrReplace(..., true, &count)` for exact variant matching, then restores URLs; reports URL count and replacement list for logging
- **`StripPromptLeak(text, promptText, &details)`**: Simple guard for rare instruction-echo outputs; removes `"instructions: " . promptText` when present, then strips a leading `text input:`
- **Timing**: Captured in `timings.replacementsApplied` and `timings.promptGuardApplied`; logged as delta-ms values in the weekly `spellcheck-YYYY-MM-DD-to-YYYY-MM-DD.jsonl` files

### Logging System (JSONL)
- **Format**: JSON Lines — one JSON object per line in weekly files like `logs/spellcheck-2026-03-23-to-2026-03-29.jsonl`
- **Rotation**: New entries write to the current week's file (Monday-based week start). If appending the next line would push that file past 5 MiB, the script spills into `-2`, `-3`, etc. files for that same week instead of renaming old logs
- **Fields per entry**: timestamp, status, error, duration_ms, model, model_version, active_app, active_exe, paste_method, text_changed, input_text, input_chars, output_text, output_chars, raw_ai_output, tokens (input/output/total/cached/reasoning), timings (clipboard/payload/request/api/parse/replacements/prompt_guard/paste in ms), replacements (count/applied/urls_protected), prompt_leak (triggered/occurrences/text_input_removed/removed_chars/before_length/after_length), events array, raw_response
- **Viewer**: Run `python generate_log_viewer.py` to generate `logs/viewer.html` (add `--open` to auto-launch in browser). The HTML has summary stats, sortable/filterable table, expandable row details, timing breakdown bars, and search

## Critical Debugging Principles (MUST FOLLOW)

### 1. Complete Verification Before Declaring Success
**NEVER declare work complete without verifying ALL aspects, not just structure.**

When verifying API integrations or model changes:
1. Model name/identifier correct?
2. Endpoint URL correct?
3. Request structure correct and matches docs?
4. **ALL parameters supported by this specific model/type?** (Critical - often missed!)
5. Response format compatible?
6. Error handling appropriate (capture raw error body for 4xx/5xx)?

**Real Example**: When migrating to GPT-5.1, initial verification checked model name and endpoint but MISSED that reasoning models don't support the `temperature` parameter. This would have caused API errors. Always verify parameter compatibility, especially when switching model TYPES (not just versions).

**Key Learning**: Standard GPT models vs Reasoning models (GPT-5/o1/o3) have fundamentally different parameter support. Model TYPE matters, not just model name.

### 2. Debug First, Fix Second
**NEVER attempt fixes without data when root cause is unclear.**

When facing bugs:
1. **FIRST**: Add comprehensive debug logging to identify exact failure point
2. **SECOND**: Analyze debug output to understand root cause
3. **THIRD**: Implement targeted fix based on data, not assumptions

**Real Example**: Spent multiple attempts on object model issues when actual problems were:
- Regex pattern too strict (revealed by debug log: "Regex extraction returned empty")
- Number parsing syntax error (revealed by debug log: "Expected a Number but got a String at line 359")

Debug logging would have revealed both issues immediately. Always add logging FIRST.

### 3. Simplest Solution First (Performance Priority)
When user emphasizes speed/performance:
1. Consider **regex-based parsing** before object model parsing
2. Regex is ~10x faster: no object allocation, no recursive parsing, no type checking
3. For JSON responses, regex extraction is often simpler AND faster than full parsing

**Real Example**: Regex solution (`ExtractTextFromResponseRegex`) is:
- Faster: single pattern match vs recursive object traversal
- Simpler: ~25 lines vs ~200 lines of JSON parser
- More reliable: no object model version dependencies

### 4. AutoHotkey v2 Compatibility Gotchas

**Number Conversion:**
- WRONG: `return numberText + 0` (throws "Expected a Number but got a String")
- RIGHT: `return Integer(numberText)` or `return Float(numberText)`

**Object Types:**
- `obj := {}` creates basic Object
- `obj := Map()` creates Map (better for dynamic keys)
- Different versions may handle these differently

**Property Access:**
- Dot notation: `obj.property` (for known properties)
- Dynamic: `obj.%varName%` (when property name is in variable)
- Bracket: `map[key]` (works on Map, may not work on Object in some versions)

**Method Names:**
- Object: `obj.HasOwnProp(key)`
- Map: `map.Has(key)`
- Array: `arr.Length` property

### 5. Regex vs JSON Parsing Decision Tree

**Use Regex When:**
- Performance is critical
- JSON structure is consistent/predictable
- Only need to extract specific fields
- Want to avoid object model complexity

**Use JSON Parsing When:**
- Need to access multiple nested fields
- JSON structure varies
- Need to modify/rebuild JSON
- Correctness more important than speed

**This project**: Regex is correct choice (only need one text field, speed critical)

### 6. Multiple Solutions Strategy

When debugging unclear issues, prepare multiple approaches:
1. Primary solution (fastest/simplest)
2. Fallback solutions (more compatible/verbose)
3. Comprehensive logging for all paths

**Real Example**: Current implementation has:
- Primary: Regex extraction (fastest)
- Fallback: Map-based parsing with full debug logs
- Both approaches ensure we get data about what works/fails

## OpenAI Model Type Differences (CRITICAL)

### Standard GPT Models (gpt-4, gpt-4-turbo, etc.)
- Support: temperature, top_p, presence_penalty, frequency_penalty, max_tokens
- Use Chat Completions API: `/v1/chat/completions`
- Endpoint: `messages` array with role/content

### Reasoning Models (gpt-5.1, gpt-5-mini, gpt-5, o1, o3 series)
- **DO NOT support**: temperature, top_p, presence_penalty, frequency_penalty, logprobs, logit_bias
- **Only default values** or model-managed reasoning parameters
- Use Responses API: `/v1/responses`
- Endpoint: `input` array with role/content/type structure
- Use internal adaptive reasoning instead of temperature control

**Migration Checklist** (when changing models):
1. Verify model name/identifier
2. Verify correct API endpoint for that model family
3. **Verify ALL request parameters are supported** (don't assume!)
4. Check response structure differences
5. Test with sample request before declaring complete

**Why This Matters**: Reasoning models will return API errors if you send unsupported parameters like temperature or `reasoning_effort` (the correct shape is `reasoning: { effort: ... }`). Always verify parameter compatibility when switching model TYPES, not just versions; note `gpt-5-mini` uniquely allows `effort:"minimal"`.

## Important Notes for Claude

### Proactive Behavior (CRITICAL)
- **PROACTIVELY VERIFY CODE**: After making changes, ALWAYS review code for bugs without waiting for user to ask
- **PROACTIVELY UPDATE DOCUMENTATION**: When file structure or configurations change, update CLAUDE.md immediately
- **ASK CLARIFYING QUESTIONS FIRST**: When tasks have ambiguity (which version? which approach?), ask BEFORE doing work

### Model Configuration Rules
- **gpt-4.1** is a STANDARD model: uses `temperature`, `verbosity:"medium"`, NO reasoning block
- **gpt-5.1** is a REASONING model: uses `reasoning.effort:"none"`, `verbosity:"low"`, NO temperature
- **gpt-5-mini** is a REASONING model: uses `reasoning.effort:"minimal"` (unique), `verbosity:"low"`, NO temperature
- **NEVER mix parameters**: temperature and reasoning are mutually exclusive based on model type

### File Structure Awareness
- Active script: `Universal Spell Checker.ahk` (primary, with `modelModule` selector)
- `replacements.json` lives alongside the script; edit it freely - script reloads it on every run
- When modifying core logic, ensure all model branches inside the single script remain consistent
- `Universal Spell Checker - SEND TEXT instead of ctr+v.ahk` is a minimal legacy variant - do not use it as a template for new features

### Verification Standards
- **VERIFY EVERYTHING**: Don't declare work complete without checking ALL parameters, not just structure
- **Model type awareness**: Standard GPT vs Reasoning models have different parameter support - ALWAYS CHECK
- **SPEED IS PARAMOUNT**: Always prioritize performance - user has emphasized this repeatedly
- **Debug first**: If you can't test the code yourself, add comprehensive logging before attempting fixes (include raw error body on failures)
- **Simplest wins**: Regex > Object parsing for simple extraction tasks
- **Version awareness**: AHK v2 syntax differs from v1; use Integer()/Float() for number conversion
- **Official docs only**: When user emphasizes official documentation, be strategic in searches when direct access fails
- The scripts are intentionally minimal - but temporary debug logging is acceptable for troubleshooting
- Focus on the .ahk files, not the abandoned C# application in the App folder

## Legacy Components (Deprecated)

- **Universal Spell Checker App/**: Abandoned C# .NET implementation
- **Universal Spell Check - V1 - OG.ahk**: Original slower version
- **Universal Spell Checker - V2.ahk**: Early iteration
- **Universal Spell Checker - V3.ahk / V3.5.ahk**: Intermediate versions
- **spellcheck.js / spellcheck-old.js**: Old JavaScript approach
- **Universal Spell Checker - SEND TEXT instead of ctr+v.ahk**: Minimal script that types output via `SendText()` instead of clipboard paste; no logging or post-processing - kept for reference/fallback

These exist for reference but are not actively developed. The focus is on `Universal Spell Checker.ahk`.

<!-- GSD:project-start source:PROJECT.md -->
## Project

**Universal Spell Checker**

A minimalist AutoHotkey script that provides instant AI-powered spell checking across all Windows applications. Select text, press Ctrl+Alt+U, and the corrected text replaces the selection in-place. Built for maximum speed and seamless operation with zero UI overhead.

**Core Value:** Spell checking must feel instant and invisible — select, hotkey, done. Speed is the product.

### Constraints

- **Platform**: Windows only — relies on AHK v2, WinHTTP COM, Windows clipboard API
- **Runtime**: AutoHotkey v2.0 interpreter must be installed
- **API**: OpenAI API key required with Responses API access
- **Performance**: Every operation must be optimized for minimal latency — speed is the core value
- **Simplicity**: Self-contained .ahk files with minimal external dependencies
- **No build step**: Scripts run directly, no compilation or bundling
<!-- GSD:project-end -->

<!-- GSD:stack-start source:codebase/STACK.md -->
## Technology Stack

## Languages
- AutoHotkey v2.0 - Main implementation language for spell-checking automation
- Python 3.x - Log viewer HTML generation tool
## Runtime
- Windows (script runs as AutoHotkey v2.0 interpreter)
- Python 3.x runtime for log viewer tool
- Python pip (implicit) - Python standard library only, no external dependencies
## Frameworks
- AutoHotkey v2.0 - Hotkey scripting framework with COM integration
- Custom JSON Lines (JSONL) logging system - Structured log format stored in `logs/spellcheck.jsonl`
- Python built-in libraries (json, glob, pathlib, html, os, sys) for log viewer generation
- Python built-in web templating (no external template library) - `generate_log_viewer.py` generates static HTML
## Key Dependencies
- Windows COM Objects - ADODB.Stream (UTF-8 response reading), HTMLFile (HTML to plaintext conversion), WinHttp.WinHttpRequest.5.1 (HTTP requests)
- OpenAI API client - Direct HTTP/1.1 via WinHttp.WinHttpRequest (no SDK)
- Clipboard API - Windows native clipboard format handling (CF_HTML, CF_UNICODETEXT, CF_TEXT)
- Process/Window API - WinGetTitle, WinGetProcessName for active application tracking
## Configuration
- API Key: Hardcoded in script (`.ahk` file line 877) - "sk-proj-..." format
- Model selection: Top-level `modelModule` variable (line 18) - supports "gpt-4.1", "gpt-5.1", "gpt-5-mini"
- Per-app paste behavior: `sendTextApps` array (line 64) configurable for keystroke typing override
- No build process - AutoHotkey scripts run directly
- Python viewer: Run `python generate_log_viewer.py` to generate `logs/viewer.html`
- Log format: JSONL (UTF-8, BOM-aware reading)
## Platform Requirements
- Windows 11 Pro (tested) - works on Windows 8+
- AutoHotkey v2.0+ (interpreter installed)
- Python 3.x for log viewer generation
- Windows 8+ (script uses WinHTTP which available on all modern Windows)
- OpenAI API key with access to Responses API
- Network connectivity for API calls to `https://api.openai.com/v1/responses`
## Special Notes
- Direct HTTP via Windows native COM (no SDK dependency)
- Responses API endpoint (not Chat Completions)
- Request timeout: 30 seconds
- gpt-4.1 (standard, uses temperature parameter)
- gpt-5.1 (reasoning, uses reasoning.effort="none")
- gpt-5-mini (reasoning, uses reasoning.effort="minimal")
- Max single log file: 1MB before rotation
- Archive format: `spellcheck-YYYY-MM-dd-HHmmss.jsonl`
- Fields: 30+ metrics including timings breakdown, token counts, clipboard formats, active app tracking
<!-- GSD:stack-end -->

<!-- GSD:conventions-start source:CONVENTIONS.md -->
## Conventions

## Naming Patterns
- PascalCase with spaces: `Universal Spell Checker.ahk` (active script)
- Variant scripts with dashes: `Universal Spell Checker - SEND TEXT instead of ctr+v.ahk` (legacy)
- Configuration files lowercase with underscores: `replacements.json`, `generate_log_viewer.py`
- Log output in lowercase: `spellcheck.jsonl` (JSONL format)
- camelCase for all functions: `GetClipboardText()`, `ApplyReplacements()`, `LogDetailed()`, `RotateLogIfNeeded()`
- Private/internal functions prefixed with double underscore: `__ReadClipboardString()`, `__ExtractHtmlFragment()`, `__JsonParseValue()`, `__JsonParseNumber()`
- Helper functions grouped logically near their public wrappers
- Single-purpose functions with clear responsibilities (e.g., `GetUtf8Response()` handles only UTF-8 stream conversion)
- camelCase for local/global variables: `originalText`, `correctedText`, `logData`, `apiKey`, `apiUrl`
- Constants in uppercase with camelCase fallback: `enableLogging`, `maxLogSize`, `replacementsPath` (per-module constants defined at top)
- Descriptive names over abbreviations: `originalText` not `origText`, `urlCount` not `cnt`
- Object properties use camelCase: `logData.original`, `logData.pasteTime`, `logData.timings.clipboardCaptured`
- Array properties descriptive: `postReplacements`, `applied`, `events`
- Timing objects nested: `timings.clipboardCaptured`, `timings.requestSent` (millisecond timestamps)
- Implicit typing (AutoHotkey v2)
- Boolean flags explicit: `apiUsesReasoning`, `textChanged`, `pasteAttempted`
- JSON object keys use snake_case in output (for logging/API): `input_text`, `output_text`, `text_changed`, `model_version`
## Code Style
- No automatic formatter configured (raw AHK file)
- Indentation: 4 spaces (visible in function bodies and control structures)
- Line length: no hard limit enforced, but kept reasonable (~100 chars typical)
- Opening braces on same line: `function {` pattern
- Spacing: consistent single space around operators and after commas
- No linter configured for AutoHotkey code
- Code review focuses on:
- PEP 8 style with 4-space indentation
- Type hints not used (older Python compatibility)
- Docstrings for public functions: `"""..."""` format
- Imports: stdlib only (json, glob, os, sys, html, pathlib)
- Line length: ~80-100 chars
## Import Organization
- No explicit imports (AutoHotkey v2 has built-in functions)
- Library-style functions: all defined in single script for performance
- No external .ahk includes (intentionally self-contained)
- `replacements.json`: JSON key-value map (canonical → [variants])
- No path aliases or special resolution mechanisms
## Error Handling
- Try-catch wrapping fallible operations: `try { ... } catch Error as e { ... }`
- Silent failures for optional operations (replacements, logging, clipboard history policy)
- Error capture in `logData.error` for API failures
- User-facing tooltips for critical failures: `ToolTip("API Error: " . status)`
- Detailed event logging for all error paths: `logData.events.Push("Exception thrown: " . message)`
- Comprehensive try-finally for resource cleanup (HTTP object, COM objects, clipboard operations)
- Clipboard operations: `try { ... } finally { DllCall("CloseClipboard") }`
- HTTP timeouts: 5s connection, 5s response, 30s total (defensive)
- JSON parse errors: caught separately, logged with position info
- Regex extraction: method 1 (fast), fallback to method 2 (compatible) with full debug logging
- Critical vs optional errors:
## Logging
- Structured logging: one JSON object per line in `logs/spellcheck.jsonl`
- Timestamp format: `"yyyy-MM-dd HH:mm:ss"` (human-readable, not Unix time)
- Event log: `logData.events` array captures sequence of operations and debug info
- Timing instrumentation: delta-millisecond values (`clipboard_ms`, `api_ms`, `parse_ms`, etc.)
- Token tracking: `tokens.input`, `tokens.output`, `tokens.total`, `tokens.cached`, `tokens.reasoning`
- Replacements tracking: count + list of applied variants
- Prompt-leak guard: triggered flag + occurrence count + character delta
- Raw capture: `raw_request` (JSON payload sent) and `raw_response` (full API response) for debugging
- Log rotation: automatic at 1MB with timestamp suffix (e.g., `spellcheck-2026-03-27-123456.jsonl`)
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
- Complex algorithms (e.g., HTML fragment extraction, surrogate pair handling in JSON parsing)
- Non-obvious performance decisions (e.g., "regex extraction ~10x faster than JSON parsing")
- Workarounds and known limitations (e.g., URL extraction placeholders)
- Critical state transitions (e.g., "Create the prompt from shared instruction text")
- Per-app behavior (e.g., `sendTextApps` configuration comment)
- Not used (AutoHotkey doesn't have built-in TSDoc support)
- Function documentation via inline comments above definition
- Parameter documentation in comment block before function
## Function Design
- Most functions 10-50 lines (focused)
- Larger functions (100+ lines) reserved for parsing, logging, hotkey handler
- Hotkey handler at ~300 lines combines multiple stages (acceptable for main flow)
- Functions use `&paramName` for output parameters (AutoHotkey v2 pass-by-reference)
- Examples: `ApplyReplacements(text, &applied, &urlCount)`, `StripPromptLeak(text, promptText, &details)`
- Global state only for module config: `modelModule`, `apiKey`, `enableLogging`
- Strings for text extraction/transformation
- Objects/Maps for structured data (parsed JSON, timing info)
- Booleans for flags and checks
- Implicit return on error conditions (e.g., `return ""` when clipboard unavailable)
- Parsing: `__JsonParseValue()`, `__JsonParseObject()`, `__JsonParseString()`
- Extraction: `__ExtractHtmlFragment()`, `__ReadClipboardString()`
- Escape/unescape: `JsonEscape()` (public for logging), escape sequences handled inside parsers
- Utility: `__SetClipboardDwordFormat()`, `__JsonSkipWhitespace()`
## Module Design
- No explicit module exports (single-file design)
- Public API: hotkey handler `^!u::`, callable functions for testing
- Not used (self-contained script)
- `#Requires AutoHotkey v2.0`: version constraint at file top
- Directory creation: `DirCreate(A_ScriptDir . "\logs")` in global scope
- Model selector: `modelModule := "gpt-4.1"` (single source of truth)
- No global state initialization needed beyond config
- Constants: `SCRIPT_DIR`, `LOGS_DIR`, `OUTPUT_FILE`
- Functions: `load_entries()`, `compute_stats()`, main `main()` with `if __name__ == "__main__"`
- HTML template: inline multiline string `HTML_TEMPLATE`
## Performance Conventions
- Regex extraction preferred over JSON parsing for response text (single pattern match vs recursive traversal)
- Microsecond-level timing on replacements: `StrReplace(..., true)` with case-sensitive flag
- URL extraction uses placeholders to avoid regex re-running on protected URLs
- Post-replacement JSON rebuilding avoided (direct string append for logging)
- Every major operation timestamped: `A_TickCount` milliseconds
- Delta calculations post-operation: `tClip := (clipboardCaptured > 0) ? (clipboardCaptured - startTime) : 0`
- Timing deltas logged as integer milliseconds, no floating-point precision
- Primary: regex extraction (fast, stateless)
- Secondary: Map-based JSON parsing (correct, verbose logging on failure)
- Both paths instrumented for debugging
<!-- GSD:conventions-end -->

<!-- GSD:architecture-start source:ARCHITECTURE.md -->
## Architecture

## Pattern Overview
- Monolithic script (`Universal Spell Checker.ahk`) with clear functional layers
- No external code dependencies (self-contained, except `replacements.json` config)
- Event-driven hotkey activation (Ctrl+Alt+U) triggering a linear processing pipeline
- Synchronized API calls with timeout protection
- Emphasis on performance through regex extraction and microsecond-level timing instrumentation
## Layers
- Purpose: Store static settings applied at runtime
- Location: `Universal Spell Checker.ahk` lines 1-76
- Contains: Model selector, API URL, prompt text, logging settings, app-specific paste behavior
- Depends on: Nothing
- Used by: All other layers
- Purpose: Robust text capture and paste operations with multiple fallbacks
- Location: `Universal Spell Checker.ahk` lines 202-265
- Contains: HTML format parsing, Unicode/ANSI fallback, clipboard history policy
- Functions: `GetClipboardText()`, `__ExtractHtmlFragment()`, `__HtmlFragmentToPlainText()`, `__ReadClipboardString()`, `SetClipboardHistoryPolicy()`, `__SetClipboardDwordFormat()`
- Depends on: Windows DLL clipboard API, COM HTMLFile object
- Used by: Main hotkey handler
- Purpose: Construct model-specific OpenAI Responses API JSON payloads
- Location: `Universal Spell Checker.ahk` lines 880-893
- Contains: Dynamic payload construction based on model type (gpt-4.1, gpt-5.1, gpt-5-mini)
- Depends on: Model configuration (Temperature, Verbosity, reasoningEffort), JSON escaping
- Used by: Main hotkey handler
- Purpose: Execute HTTP POST to OpenAI Responses API with error handling
- Location: `Universal Spell Checker.ahk` lines 895-929
- Contains: WinHttp COM object setup, request header injection, timeout configuration, error capture
- Depends on: WinHttp.WinHttpRequest.5.1 COM object
- Used by: Main hotkey handler
- Purpose: Extract corrected text from API JSON response (two competing implementations)
- Location: `Universal Spell Checker.ahk` lines 738-791
- Contains:
- Depends on: `GetUtf8Response()` for proper UTF-8 decoding
- Used by: Main hotkey handler
- Purpose: Apply brand/term casing corrections and safety cleanups
- Location: `Universal Spell Checker.ahk` lines 79-200
- Contains:
- Depends on: `replacements.json` file, JSON parsing
- Used by: Main hotkey handler
- Purpose: Deliver corrected text back to application
- Location: `Universal Spell Checker.ahk` lines 1059-1076
- Contains: Clipboard-based paste (default) vs `SendText()` (app-specific)
- Depends on: Per-app configuration (`sendTextApps`), clipboard API
- Used by: Main hotkey handler
- Purpose: Structured JSONL recording of every spell-check operation with comprehensive timing/event tracking
- Location: `Universal Spell Checker.ahk` lines 333-483
- Contains:
- Depends on: File I/O, timing counters
- Used by: Main hotkey handler
- Purpose: JSON handling, string escaping, timing helpers
- Location: `Universal Spell Checker.ahk` lines 486-717
- Contains: `JsonEscape()`, `JsonLoad()` (full recursive parser), `GetUtf8Response()`, number parsing
- Depends on: ADODB.Stream COM object for UTF-8 decoding
- Used by: All layers
## Data Flow
- **logData object**: Single mutable state object passed through entire pipeline, accumulated with events/timings
- **Global variables**: Model config (`modelModule`, `apiModel`, `Verbosity`, etc.), paths, flags
- **No shared state between invocations**: Each Ctrl+Alt+U press creates fresh logData object
## Key Abstractions
- Purpose: Single source of truth for model selection and parameter mapping
- Location: `Universal Spell Checker.ahk` lines 18-48
- Pattern: Switch statement mapping model name → parameter values
- Ensures model type compatibility (e.g., reasoning models exclude temperature)
- Purpose: Fast path (regex) + safe fallback (full JSON parsing)
- Primary: `ExtractTextFromResponseRegex()` — no object allocation, ~10x faster
- Fallback: `JsonLoad()` + object traversal — full compatibility, comprehensive debug logging
- Purpose: Prevent substring interference during replacements
- Pattern: Load from JSON, sort longest-first, apply in order
- Example: Replace "Build Purdue" before "Build" to avoid partial matches
- Purpose: Handle apps that don't work well with clipboard paste
- Pattern: Check `sendTextApps` list, choose `SendText()` or `Ctrl+V` accordingly
## Entry Points
- Location: `Universal Spell Checker.ahk` line 800 (`^!u::`)
- Triggers: User presses Ctrl+Alt+U with text selected
- Responsibilities: Orchestrates entire spell-check pipeline, error handling, logging
- `generate_log_viewer.py`: Reads JSONL logs, generates HTML viewer
- Command: `python generate_log_viewer.py [--no-open]`
## Error Handling
## Cross-Cutting Concerns
- Approach: Structured JSONL (one object per line) with millisecond-level timing breakdown
- Timing fields: clipboardCaptured, payloadPrepared, requestSent, responseReceived, textParsed, replacementsApplied, promptGuardApplied, textPasted
- Delta timing: Each stage computed from previous checkpoint, logged as elapsed ms
- Rotation: 1MB per file, archived with timestamp
- Async logging: SetTimer ensures timestamps reflect actual completion, not logging time
- Model name validation: Switch statement rejects invalid `modelModule` values
- Prompt text: Single hardcoded `promptInstructionText` used for both request and leak detection
- Token count extraction: Regex patterns with Integer() conversion for AHK v2 compatibility
- Regex-based JSON extraction: Avoids object allocation overhead
- Sorted replacement pairs: Longest-first prevents substring interference
- Clipboard format preference: HTML reduces formatting noise
- URL protection: Extract/restore around replacements rather than conditional logic during replacements
- Metadata-checked `replacements.json` cache: Allows live edits without per-run reparsing
- API key hardcoded (intentional design choice for fast startup, offline-first)
- Clipboard history exclusion: Mark source text as transient
- Prompt leak detection: Safeguard against instruction echo in output
- HTML escaping in log viewer: Prevents script injection in viewer UI
<!-- GSD:architecture-end -->

<!-- GSD:workflow-start source:GSD defaults -->
## GSD Workflow Enforcement

Before using Edit, Write, or other file-changing tools, start work through a GSD command so planning artifacts and execution context stay in sync.

Use these entry points:
- `/gsd:quick` for small fixes, doc updates, and ad-hoc tasks
- `/gsd:debug` for investigation and bug fixing
- `/gsd:execute-phase` for planned phase work

Do not make direct repo edits outside a GSD workflow unless the user explicitly asks to bypass it.
<!-- GSD:workflow-end -->

<!-- GSD:profile-start -->
## Developer Profile

> Profile not yet configured. Run `/gsd:profile-user` to generate your developer profile.
> This section is managed by `generate-claude-profile` -- do not edit manually.
<!-- GSD:profile-end -->
