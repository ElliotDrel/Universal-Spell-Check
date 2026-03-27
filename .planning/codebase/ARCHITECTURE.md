# Architecture

**Analysis Date:** 2026-03-27

## Pattern Overview

**Overall:** Single-file AutoHotkey script with modular functional design

**Key Characteristics:**
- Monolithic script (`Universal Spell Checker.ahk`) with clear functional layers
- No external code dependencies (self-contained, except `replacements.json` config)
- Event-driven hotkey activation (Ctrl+Alt+U) triggering a linear processing pipeline
- Synchronized API calls with timeout protection
- Emphasis on performance through regex extraction and microsecond-level timing instrumentation

## Layers

**Configuration Layer:**
- Purpose: Store static settings applied at runtime
- Location: `Universal Spell Checker.ahk` lines 1-76
- Contains: Model selector, API URL, prompt text, logging settings, app-specific paste behavior
- Depends on: Nothing
- Used by: All other layers

**Clipboard I/O Layer:**
- Purpose: Robust text capture and paste operations with multiple fallbacks
- Location: `Universal Spell Checker.ahk` lines 202-265
- Contains: HTML format parsing, Unicode/ANSI fallback, clipboard history policy
- Functions: `GetClipboardText()`, `__ExtractHtmlFragment()`, `__HtmlFragmentToPlainText()`, `__ReadClipboardString()`, `SetClipboardHistoryPolicy()`, `__SetClipboardDwordFormat()`
- Depends on: Windows DLL clipboard API, COM HTMLFile object
- Used by: Main hotkey handler

**Payload Building Layer:**
- Purpose: Construct model-specific OpenAI Responses API JSON payloads
- Location: `Universal Spell Checker.ahk` lines 880-893
- Contains: Dynamic payload construction based on model type (gpt-4.1, gpt-5.1, gpt-5-mini)
- Depends on: Model configuration (Temperature, Verbosity, reasoningEffort), JSON escaping
- Used by: Main hotkey handler

**API Communication Layer:**
- Purpose: Execute HTTP POST to OpenAI Responses API with error handling
- Location: `Universal Spell Checker.ahk` lines 895-929
- Contains: WinHttp COM object setup, request header injection, timeout configuration, error capture
- Depends on: WinHttp.WinHttpRequest.5.1 COM object
- Used by: Main hotkey handler

**Response Parsing Layer:**
- Purpose: Extract corrected text from API JSON response (two competing implementations)
- Location: `Universal Spell Checker.ahk` lines 738-791
- Contains:
  - `ExtractTextFromResponseRegex()`: Regex-based extraction (primary, ~10x faster)
  - `ExtractTextFromResponseObject()`: Full JSON object parsing (fallback)
  - `JsonLoad()`: Complete recursive JSON parser
  - Token count extraction via regex
- Depends on: `GetUtf8Response()` for proper UTF-8 decoding
- Used by: Main hotkey handler

**Post-Processing Layer:**
- Purpose: Apply brand/term casing corrections and safety cleanups
- Location: `Universal Spell Checker.ahk` lines 79-200
- Contains:
  - `LoadReplacements()`: Parse `replacements.json` on every run
  - `ApplyReplacements()`: Run case-sensitive variant matching with URL protection
  - `StripPromptLeak()`: Remove accidental instruction-text echoes
- Depends on: `replacements.json` file, JSON parsing
- Used by: Main hotkey handler

**Text Insertion Layer:**
- Purpose: Deliver corrected text back to application
- Location: `Universal Spell Checker.ahk` lines 1059-1076
- Contains: Clipboard-based paste (default) vs `SendText()` (app-specific)
- Depends on: Per-app configuration (`sendTextApps`), clipboard API
- Used by: Main hotkey handler

**Logging Layer:**
- Purpose: Structured JSONL recording of every spell-check operation with comprehensive timing/event tracking
- Location: `Universal Spell Checker.ahk` lines 333-483
- Contains:
  - `LogDetailed()`: Main logging function (JSONL format)
  - `RotateLogIfNeeded()`: 1MB log rotation
  - `BuildJsonStringArray()`: Helper for arrays
  - `JsonEscape()`: String sanitization
  - `JsonLoad()`: JSON parsing for `replacements.json`
- Depends on: File I/O, timing counters
- Used by: Main hotkey handler

**Utility Functions:**
- Purpose: JSON handling, string escaping, timing helpers
- Location: `Universal Spell Checker.ahk` lines 486-717
- Contains: `JsonEscape()`, `JsonLoad()` (full recursive parser), `GetUtf8Response()`, number parsing
- Depends on: ADODB.Stream COM object for UTF-8 decoding
- Used by: All layers

## Data Flow

**Hotkey Trigger (Ctrl+Alt+U):**

1. **Initialization** (lines 800-847)
   - Capture active window title and exe name
   - Initialize `logData` object with timing counters and metadata
   - Set start timestamp

2. **Text Capture** (lines 856-874)
   - Reload `replacements.json` (allows live editing)
   - Clear clipboard and send Ctrl+C to selected text
   - Wait up to 1 second for clipboard content
   - Mark clipboard as transient (exclude from Win+V history)
   - Read clipboard preferring HTML format, fallback to Unicode/ANSI
   - Log captured text length

3. **Payload Construction** (lines 879-893)
   - Build prompt: `"instructions: " . promptInstructionText . "\ntext input: " . originalText`
   - Escape prompt for JSON
   - Branch based on model type:
     - **gpt-4.1** (standard): Include `temperature: 0.3`, `verbosity: "medium"`, NO reasoning
     - **gpt-5.1** (reasoning): Include `reasoning: {effort: "none"}`, `verbosity: "low"`, NO temperature
     - **gpt-5-mini** (reasoning): Include `reasoning: {effort: "minimal"}`, `verbosity: "low"`, NO temperature
   - Wrap in Responses API structure: `input: [{role: "user", content: [{type: "input_text", text: "..."}]}]`
   - Enable response caching via `store: true`
   - Log payload construction event

4. **API Request** (lines 895-902)
   - Create WinHttp POST request with 30-second timeout
   - Set Content-Type: application/json, Authorization: Bearer
   - Send payload
   - Record request sent timestamp

5. **Response Handling** (lines 905-949)
   - Check HTTP status (non-200 = error)
   - On error: capture error body, display tooltip, log error, return
   - On 200: Read response as UTF-8 (ADODB.Stream)
   - Extract token counts via regex (input_tokens, output_tokens, total_tokens, cached_tokens, reasoning_tokens)
   - Extract model version via regex

6. **Response Parsing** (lines 951-1023)
   - **PRIMARY METHOD**: Try regex extraction via `ExtractTextFromResponseRegex()`
     - Match pattern: `"type":"output_text"[^}]*"text":"(...)"`
     - Unescape JSON escapes in correct order (Unicode first, then standard escapes)
     - If successful, skip fallback
   - **FALLBACK METHOD**: If regex returns empty, try `JsonLoad()` + object traversal
     - Navigate: response → output[0] → content[0] → text
     - Includes comprehensive debug logging for both methods
   - Log whichever method succeeded

7. **Post-Processing** (lines 1025-1054)
   - **URL Protection**: Extract `http(s)://\S+` URLs into placeholders before replacements
   - **Replacements**: Run case-sensitive variant matching on protected text
   - **Replacements**: Restore original URLs
   - Log replacements count and which were applied
   - **Prompt Leak Guard**: Check if output contains instruction echo
   - If triggered: Remove instruction block and trailing "text input:" label
   - Log leak guard details (characters removed, before/after lengths)

8. **Text Insertion** (lines 1056-1089)
   - Determine insertion method:
     - If app in `sendTextApps` list: use `SendText()` (keystroke typing)
     - Otherwise: paste via clipboard + Ctrl+V
   - Log insertion method
   - Set corrected text back to clipboard
   - Record paste timing

9. **Logging** (lines 1091-1100)
   - Capture final error state or success status
   - Asynchronously call `LogDetailed()` via SetTimer to preserve timestamps
   - Include all metadata, events, timings, token counts, replacements, prompt leak details

**State Management:**

- **logData object**: Single mutable state object passed through entire pipeline, accumulated with events/timings
- **Global variables**: Model config (`modelModule`, `apiModel`, `Verbosity`, etc.), paths, flags
- **No shared state between invocations**: Each Ctrl+Alt+U press creates fresh logData object

## Key Abstractions

**Model Configuration Block:**
- Purpose: Single source of truth for model selection and parameter mapping
- Location: `Universal Spell Checker.ahk` lines 18-48
- Pattern: Switch statement mapping model name → parameter values
- Ensures model type compatibility (e.g., reasoning models exclude temperature)

**Dual Parser Strategy:**
- Purpose: Fast path (regex) + safe fallback (full JSON parsing)
- Primary: `ExtractTextFromResponseRegex()` — no object allocation, ~10x faster
- Fallback: `JsonLoad()` + object traversal — full compatibility, comprehensive debug logging

**Replacement Variant Sorting:**
- Purpose: Prevent substring interference during replacements
- Pattern: Load from JSON, sort longest-first, apply in order
- Example: Replace "Build Purdue" before "Build" to avoid partial matches

**Per-App Paste Method:**
- Purpose: Handle apps that don't work well with clipboard paste
- Pattern: Check `sendTextApps` list, choose `SendText()` or `Ctrl+V` accordingly

## Entry Points

**Hotkey Handler:**
- Location: `Universal Spell Checker.ahk` line 800 (`^!u::`)
- Triggers: User presses Ctrl+Alt+U with text selected
- Responsibilities: Orchestrates entire spell-check pipeline, error handling, logging

**Optional: Manual Invocation Points:**
- `generate_log_viewer.py`: Reads JSONL logs, generates HTML viewer
- Command: `python generate_log_viewer.py [--no-open]`

## Error Handling

**Strategy:** Try-catch with fallback paths and detailed logging

**Patterns:**

1. **Clipboard Timeout:**
   - Scenario: Ctrl+C doesn't work (e.g., read-only app)
   - Action: Check ClipWait(1) result, log error, display tooltip
   - No retry — fail gracefully

2. **API Errors:**
   - Non-200 HTTP status
   - Action: Capture error body (first 1000 chars), display tooltip with status, log raw response
   - Falls through to logging without paste

3. **Parsing Failures:**
   - Regex returns empty, JsonLoad throws or returns invalid structure
   - Action: Try both methods sequentially, log both attempts with debug events
   - If both fail: Show "No text returned" error, don't paste

4. **JSON Parse Errors:**
   - Malformed JSON from replacements.json
   - Action: Silent catch in `LoadReplacements()`, continue without replacements
   - Logging only if detailed events enabled

5. **Clipboard Format Fallback Chain:**
   - HTML → Unicode (CF_UNICODETEXT) → ANSI (CF_TEXT)
   - Each format attempted independently; first non-empty result used

## Cross-Cutting Concerns

**Logging:**
- Approach: Structured JSONL (one object per line) with millisecond-level timing breakdown
- Timing fields: clipboardCaptured, payloadPrepared, requestSent, responseReceived, textParsed, replacementsApplied, promptGuardApplied, textPasted
- Delta timing: Each stage computed from previous checkpoint, logged as elapsed ms
- Rotation: 1MB per file, archived with timestamp
- Async logging: SetTimer ensures timestamps reflect actual completion, not logging time

**Validation:**
- Model name validation: Switch statement rejects invalid `modelModule` values
- Prompt text: Single hardcoded `promptInstructionText` used for both request and leak detection
- Token count extraction: Regex patterns with Integer() conversion for AHK v2 compatibility

**Performance Optimization:**
- Regex-based JSON extraction: Avoids object allocation overhead
- Sorted replacement pairs: Longest-first prevents substring interference
- Clipboard format preference: HTML reduces formatting noise
- URL protection: Extract/restore around replacements rather than conditional logic during replacements
- Per-run `replacements.json` reload: Allows live edits without script restart

**Security Considerations:**
- API key hardcoded (intentional design choice for fast startup, offline-first)
- Clipboard history exclusion: Mark source text as transient
- Prompt leak detection: Safeguard against instruction echo in output
- HTML escaping in log viewer: Prevents script injection in viewer UI

---

*Architecture analysis: 2026-03-27*
