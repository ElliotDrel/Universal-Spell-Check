# Architecture Patterns

**Domain:** AHK v2 monolithic script optimization for AI-powered spell checking
**Researched:** 2026-03-27

## Recommended Architecture

**Verdict: Stay monolithic. Optimize in-place.**

The single-file architecture is correct for this project. AHK v2 has no module system, no package manager, and `#Include` adds file I/O overhead at startup. The script is ~1100 lines -- well within manageable range for a single file. Extracting to multiple files would add complexity (include ordering, variable scoping across files) without meaningful benefit.

The optimization strategy is: improve the existing layers without changing the structural boundaries.

### Current Architecture (Keep As-Is)

```
Universal Spell Checker.ahk (monolithic, ~1100 lines)
|
|-- Configuration Layer        (lines 1-76)     -- KEEP
|-- Post-Processing Layer      (lines 79-200)   -- KEEP, minor tweaks
|-- Clipboard I/O Layer        (lines 202-265)  -- REFACTOR: add retry + restore
|-- Logging Layer              (lines 333-483)  -- KEEP, reduce debug noise
|-- Utility / JSON Layer       (lines 486-717)  -- KEEP
|-- Response Parsing Layer     (lines 738-791)  -- KEEP
|-- Hotkey Handler             (lines 800-1102) -- REFACTOR: extract WinHTTP reuse
```

### Component Boundaries

| Component | Responsibility | Keep/Refactor | Communicates With |
|-----------|---------------|---------------|-------------------|
| Configuration | Model params, paths, prompt text | KEEP as-is | All layers read it |
| Clipboard I/O | Capture selected text, paste corrected text | REFACTOR | Hotkey handler |
| Payload Builder | Construct model-specific JSON | KEEP as-is | Hotkey handler, API layer |
| API Communication | HTTP POST to OpenAI | REFACTOR | Hotkey handler |
| Response Parser | Extract text from JSON (regex + fallback) | KEEP as-is | Hotkey handler |
| Post-Processing | Replacements + prompt leak guard | KEEP, minor | Hotkey handler |
| Text Insertion | Clipboard paste or SendText | REFACTOR | Hotkey handler |
| Logging | JSONL structured logging | KEEP, reduce noise | Hotkey handler |

### Data Flow (Unchanged)

```
Ctrl+Alt+U -> Clipboard Capture -> Payload Build -> API Call -> Parse Response
    -> Post-Process (replacements, leak guard) -> Paste Back -> Log
```

The linear pipeline is correct. There are no data flow changes needed -- the optimizations target latency within each stage.

## What to Keep As-Is

### 1. Single-File Design
**Why:** AHK v2 scripts are loaded once and stay resident. No runtime cost to having all functions in one file. The `#Include` directive adds file I/O at script load. For a hotkey-triggered tool where startup time matters, single-file is optimal.

### 2. Regex-First JSON Parsing Strategy
**Why:** The dual-parser approach (regex primary, Map-based fallback) is well-designed. The regex path handles >99% of responses and runs in microseconds. The fallback exists for edge cases and provides debug data when something goes wrong. The performance gap (~10x) justifies keeping both paths.

### 3. Model Configuration Switch Block
**Why:** The switch statement on `modelModule` at lines 28-48 is clean, explicit, and prevents parameter mixing bugs (the critical gotcha documented in CLAUDE.md). Adding a config file would add startup I/O and parsing overhead for no functional gain -- model changes are infrequent.

### 4. Post-Processing Replacement System
**Why:** The replacement system runs in microseconds even at 80+ variants. The URL protection via placeholder extraction is a smart pattern. Reloading `replacements.json` every run is the right call -- it allows live editing. The O(n^2) insertion sort is fine because n < 100 and runs once per invocation.

### 5. Prompt Leak Safeguard
**Why:** Simple string match is the right approach for this rare edge case. Fuzzy matching would add latency to every request for a problem that occurs < 1% of the time.

### 6. JSONL Logging with SetTimer Async
**Why:** Using `SetTimer(() => LogDetailed(snapshot), -1)` to defer logging is correct. It prevents file I/O from blocking the critical path (text insertion). The JSONL format is easy to parse, append-only, and the 1MB rotation prevents unbounded growth.

## What to Refactor (Suggested Optimization Order)

The refactoring items below are ordered by impact and dependency. Each builds on the previous.

### Phase 1: Persistent WinHTTP Object (Highest Impact)

**What:** The current code creates a new `WinHttp.WinHttpRequest.5.1` COM object on every hotkey press (line 895). COM object creation involves Windows registry lookup, class factory instantiation, and interface negotiation -- all unnecessary after the first call.

**Why it matters:** This is likely the single biggest per-invocation overhead outside the API call itself. WinHTTP already supports connection pooling with keep-alive (enabled by default), but creating a new COM object on each request may force a new TCP connection or at minimum adds ~10-50ms of COM overhead.

**Pattern:**
```autohotkey
; At script level (global scope)
global persistentHttp := ComObject("WinHttp.WinHttpRequest.5.1")

; In hotkey handler, reuse the object:
persistentHttp.Open("POST", apiUrl, false)
persistentHttp.SetTimeouts(5000, 5000, 30000, 30000)
persistentHttp.SetRequestHeader("Content-Type", "application/json; charset=utf-8")
persistentHttp.SetRequestHeader("Authorization", "Bearer " . apiKey)
persistentHttp.Send(jsonPayload)
```

**Risk:** LOW. WinHTTP COM objects are designed for reuse across `Open`/`Send` cycles. The `Open` call resets state for the new request. Only risk is if the COM object gets into a bad state after an error -- add a `try/catch` that recreates the object on failure.

**Error recovery pattern:**
```autohotkey
try {
    persistentHttp.Open("POST", apiUrl, false)
    ; ... set headers, send ...
} catch {
    ; Recreate on failure
    persistentHttp := ComObject("WinHttp.WinHttpRequest.5.1")
    persistentHttp.Open("POST", apiUrl, false)
    ; ... retry ...
}
```

**Confidence:** HIGH -- WinHTTP connection reuse is well-documented by Microsoft and confirmed in AHK community threads.

### Phase 2: Clipboard Save/Restore + Retry

**What:** The current clipboard handling (lines 855-874) clears the user's clipboard and does not restore it. If the copy fails (ClipWait timeout), the user loses their clipboard content. Additionally, there is no retry on clipboard capture failure.

**Improvements:**
1. Save clipboard contents before clearing with `ClipboardAll()`
2. Add a single retry on `ClipWait` failure (some apps are slow to populate clipboard)
3. Restore original clipboard after paste is complete (or on error)
4. Add a small `Sleep` after `Send("^v")` before restoring clipboard (Windows needs time to complete paste)

**Pattern:**
```autohotkey
; Save user's clipboard
clipSaved := ClipboardAll()

; Clear and copy
A_Clipboard := ""
Send("^c")

if !ClipWait(1) {
    ; Retry once -- some apps need more time
    Sleep(100)
    Send("^c")
    if !ClipWait(1) {
        A_Clipboard := ClipboardAll(clipSaved)  ; Restore on failure
        ; ... log error, return ...
    }
}

; ... process text ...

; After paste:
Send("^v")
Sleep(150)  ; Give Windows time to complete paste
A_Clipboard := ClipboardAll(clipSaved)  ; Restore original clipboard
```

**Risk:** LOW-MEDIUM. The `ClipboardAll()` save/restore is a well-documented AHK pattern. The timing of clipboard restoration after paste requires a sleep -- too short and the paste sees the restored content instead of the corrected text; too long and the user notices a delay. 100-200ms is typical. The retry adds ~100ms only on failure (rare path).

**Dependency:** Independent of Phase 1.

**Confidence:** HIGH -- this is the officially recommended AHK clipboard pattern.

### Phase 3: Reduce Debug Event Noise

**What:** The hotkey handler pushes extensive `"DEBUG: ..."` events into `logData.events` on every single invocation (lines 955, 958, 961, 970, 972, 975, 977, 980, 984, 989, 991, 994, 999, 1003, 1014, 1017). This creates string allocations and array growth on the hot path.

**Improvements:**
1. Gate debug events behind a `debugLogging` flag (default `false`)
2. Keep non-DEBUG events (they are useful operational telemetry)
3. Add a constant at the top: `debugLogging := false`

**Pattern:**
```autohotkey
; Only push debug events when debug mode is on
if (debugLogging)
    logData.events.Push("DEBUG: Regex extraction SUCCESS, length=" . StrLen(correctedText))

; Always push operational events
logData.events.Push("Response parsed via regex (" . StrLen(correctedText) . " chars)")
```

**Risk:** VERY LOW. Removing string concatenation and array pushes from the hot path is pure upside. Debug logging can be re-enabled with a single flag toggle.

**Dependency:** Independent.

**Confidence:** HIGH.

### Phase 4: Predicted Outputs (Latency Reduction)

**What:** OpenAI's Predicted Outputs feature uses speculative decoding to validate predicted tokens in parallel, achieving 3-5x faster responses when most output is predictable. For spell checking, the corrected text is nearly identical to the input -- an ideal use case.

**Critical limitation:** As of early 2026, Predicted Outputs are only available on the Chat Completions API (`/v1/chat/completions`), NOT the Responses API (`/v1/responses`). Community members have flagged this gap to OpenAI but it has not been resolved.

**Decision point:** This creates a trade-off:
- **Responses API** is required for reasoning models (gpt-5.1, gpt-5-mini) with `reasoning.effort` control
- **Chat Completions API** supports Predicted Outputs but not reasoning model parameters
- **gpt-4.1** (standard model) could use Chat Completions + Predicted Outputs for potentially 3-5x latency reduction

**Possible approach:** Dual-endpoint strategy where gpt-4.1 uses Chat Completions + Predicted Outputs while reasoning models continue using Responses API. This requires branching the API endpoint and payload construction based on model type -- the `apiUsesReasoning` flag already exists to support this.

**Supported models for Predicted Outputs:** gpt-4o, gpt-4o-mini, gpt-4.1, gpt-4.1-mini, gpt-4.1-nano.

**Risk:** MEDIUM. Adds complexity to the payload builder and requires maintaining two API endpoint paths. The cost trade-off is also notable -- rejected prediction tokens are still billed at completion token rates.

**Dependency:** Requires Phase 1 (persistent HTTP object) for best results.

**Confidence:** MEDIUM -- the feature is well-documented for Chat Completions but its absence from Responses API is confirmed only by community reports, not official docs.

### Phase 5: API Call Retry with Backoff

**What:** The current code makes exactly one API call attempt (lines 895-929). If the API returns a transient error (429 rate limit, 500/502/503 server error), the user gets an error tooltip and must retry manually.

**Pattern:**
```autohotkey
maxRetries := 2  ; 1 original + 1 retry (only for transient errors)
retryableStatuses := [429, 500, 502, 503]

loop maxRetries {
    try {
        persistentHttp.Open("POST", apiUrl, false)
        ; ... set headers ...
        persistentHttp.Send(jsonPayload)

        if (persistentHttp.Status = 200)
            break  ; Success

        ; Check if retryable
        isRetryable := false
        for status in retryableStatuses {
            if (persistentHttp.Status = status)
                isRetryable := true
        }

        if (!isRetryable || A_Index = maxRetries) {
            ; Non-retryable or exhausted retries
            ; ... handle error ...
            break
        }

        ; Brief backoff before retry
        Sleep(500 * A_Index)
        logData.events.Push("Retrying API call (attempt " . (A_Index + 1) . ")")
    } catch {
        ; COM/network error -- recreate HTTP object and retry
        persistentHttp := ComObject("WinHttp.WinHttpRequest.5.1")
        if (A_Index = maxRetries)
            throw
        Sleep(500)
    }
}
```

**Risk:** LOW. Limited to 1 retry with short backoff. Total worst-case added latency is ~500ms, only on error paths. The user already waits ~30s on timeout anyway.

**Dependency:** Best combined with Phase 1 (persistent HTTP object for recovery).

**Confidence:** HIGH -- standard pattern for HTTP clients.

### Phase 6: Extract Magic Numbers to Constants

**What:** Several hardcoded values are scattered throughout the hotkey handler:
- `ClipWait(1)` -- 1 second timeout
- `SetTimeouts(5000, 5000, 30000, 30000)` -- HTTP timeouts
- `SubStr(errPreview, 1, 1000)` -- error body truncation
- `SetTimer(() => ToolTip(), -5000)` / `-3000` -- tooltip display durations
- `maxLogSize := 1000000` -- already at top, good

**Pattern:**
```autohotkey
; Timeouts
clipboardWaitSec := 1
httpConnectTimeout := 5000
httpSendTimeout := 5000
httpReceiveTimeout := 30000
httpResponseTimeout := 30000

; Display
tooltipErrorDuration := -5000
tooltipWarnDuration := -3000

; Limits
errorBodyMaxChars := 1000
```

**Risk:** VERY LOW. Pure readability improvement with zero behavior change.

**Dependency:** Independent.

**Confidence:** HIGH.

## Patterns to Follow

### Pattern 1: Static COM Object with Recovery
**What:** Use a `static` or global COM object that persists between hotkey invocations, with automatic recreation on failure.
**When:** For any COM object used repeatedly (WinHTTP, ADODB.Stream).
**Why:** Eliminates COM class instantiation overhead on every call. WinHTTP manages its own connection pool internally -- reusing the COM wrapper lets the connection pool work as designed.
```autohotkey
GetHttpObject() {
    static http := ComObject("WinHttp.WinHttpRequest.5.1")
    return http
}
```

### Pattern 2: Flag-Gated Debug Logging
**What:** Wrap verbose debug events behind a boolean flag checked at the top of the script.
**When:** Any time you have debug output that runs on every invocation but is only needed during development.
**Why:** String concatenation and array push operations have non-zero cost. On the hot path (between clipboard capture and paste), every millisecond matters.
```autohotkey
if (debugLogging)
    logData.events.Push("DEBUG: detail=" . someValue)
```

### Pattern 3: Clipboard Save/Restore Sandwich
**What:** Wrap the entire hotkey handler in a clipboard save at the start and restore at the end (including error paths via `finally`).
**When:** Any script that temporarily modifies the clipboard for its own use.
**Why:** Users do not expect a spell checker to destroy their clipboard contents. This is table-stakes UX for clipboard-based tools.

### Pattern 4: Retry with Scope-Limited Backoff
**What:** Retry transient failures (429, 5xx) exactly once with a short delay. Do not retry on 400/401/403 (client errors).
**When:** Network-dependent operations where transient failures are expected.
**Why:** Adds resilience with minimal latency impact. The retry only fires on error paths, not on the happy path.

## Anti-Patterns to Avoid

### Anti-Pattern 1: Extracting to Multiple Files
**What:** Splitting the script into separate `.ahk` files with `#Include`.
**Why bad:** Adds file I/O at script startup, introduces include-ordering bugs, complicates variable scoping (AHK v2 globals must be explicitly declared in each file), and provides no runtime benefit since all code is loaded into memory either way. The script is ~1100 lines -- this is not large enough to warrant splitting.
**Instead:** Keep monolithic. Use the existing functional layering (configuration at top, utilities in middle, handler at bottom) for organization.

### Anti-Pattern 2: Async/Streaming API Calls
**What:** Making the WinHTTP request asynchronous (`http.Open("POST", url, true)`) or switching to streaming responses.
**Why bad:** AHK v2's event model is single-threaded. Async WinHTTP requires COM event sinks (`ComObjConnect`) which are fragile and add significant complexity. The user is already waiting for the result (they pressed the hotkey and expect immediate replacement). Streaming would require incremental text construction and partial clipboard updates -- far more complex than the current synchronous call. The API response time (typically 200-800ms for short text) is dominated by model inference, not transfer time.
**Instead:** Keep synchronous. Reduce latency through Predicted Outputs (Phase 4) and persistent connections (Phase 1).

### Anti-Pattern 3: Full JSON Parsing as Primary Method
**What:** Replacing regex extraction with the full `JsonLoad()` parser as the primary response parsing method.
**Why bad:** The regex path extracts the single needed text field in one pattern match. The full parser allocates a Map hierarchy, traverses arrays, and parses every field including those never used (token counts, IDs, metadata). For a ~2KB API response, the difference is ~10x in execution time.
**Instead:** Keep regex as primary. The fallback parser exists for resilience, not performance.

### Anti-Pattern 4: Over-Engineering Error Recovery
**What:** Adding exponential backoff, circuit breakers, or multi-retry loops with configurable retry counts.
**Why bad:** This is a personal tool triggered by a hotkey press. The user is sitting there waiting. More than 1 retry on a transient error adds unacceptable wait time. If the API is down, the user will just try again manually.
**Instead:** One fast retry for transient errors. Fail visibly and quickly for everything else.

### Anti-Pattern 5: Configuration File for Model Selection
**What:** Moving model selection to a config file (`config.json`) loaded at runtime.
**Why bad:** The user changes models infrequently (maybe weekly). Loading and parsing a JSON config file on every hotkey press adds I/O latency to every single invocation for something that could be a source-code constant. The current approach (edit line 18, reload script) takes seconds and is perfectly adequate for a personal tool.
**Instead:** Keep the `modelModule` variable at line 18. If runtime model switching becomes a real need, add a second hotkey (`Ctrl+Alt+M`) to cycle through models -- no file I/O required.

## Scalability Considerations

| Concern | Current (single user) | If replacements.json grows 10x | If clipboard text is very large |
|---------|----------------------|-------------------------------|-------------------------------|
| Replacement processing | Microseconds at ~80 variants | Still sub-millisecond at 800 variants (linear scan + string replace) | O(n*m) where n=text length, m=variants. Could become noticeable above 50KB text + 500 variants |
| JSON payload construction | String concatenation, microseconds | N/A | Large text means large prompt string. JsonEscape is O(n). For 100KB text, this is still fast (<10ms) |
| API response parsing | Regex single-pass, microseconds | N/A | Response size scales with input. Regex stays fast. |
| Clipboard capture | ClipWait 1s max | N/A | Windows clipboard handles large text natively. AHK string ops slow above ~1MB |
| Log file size | 1MB rotation works | N/A | Large text in logs increases file size. Could add text truncation in logs for inputs >10KB |

None of these are immediate concerns. The script processes short text selections (typically <1KB) and will never hit these limits in normal use.

## Risk Areas When Modifying Existing Architecture

### HIGH RISK: Modifying the Hotkey Handler Flow
The hotkey handler (lines 800-1102) is a 300-line sequential pipeline with carefully ordered timing checkpoints. Every `A_TickCount` capture feeds into the logging system. Reordering stages, adding stages between timing checkpoints, or changing the error handling flow can silently break timing accuracy without any visible error. **Always run the full handler and check log output after any modification.**

### HIGH RISK: Changing JSON Escape Ordering
The `ExtractTextFromResponseRegex` function (lines 738-763) unescapes JSON in a specific order: Unicode escapes first, then standard escapes, with backslash last. Changing this order causes double-unescaping bugs that produce corrupted text. **Never reorder the unescape sequence.**

### MEDIUM RISK: Clipboard Timing
The time between `Send("^v")` and clipboard restoration is critical. If the clipboard is restored before the target application reads it for the paste operation, the user gets their old clipboard content pasted instead of the corrected text. **Always add a Sleep(100-200) between paste and clipboard restore.**

### MEDIUM RISK: Persistent HTTP Object State
If the COM object enters a bad state (e.g., after a network timeout that leaves the connection half-open), subsequent requests may fail until the object is recreated. **Always wrap persistent HTTP usage in try/catch with object recreation on failure.**

### LOW RISK: Model Parameter Mixing
The `apiUsesReasoning` flag gates temperature vs reasoning parameters. If Predicted Outputs (Phase 4) is implemented with a dual-endpoint strategy, this branching becomes more complex. **Keep model-type branching in one clear switch block, not scattered conditionally.**

### LOW RISK: Debug Event Accumulation
The `logData.events` array grows unbounded during execution. For normal runs this is ~10-20 entries. But if debug logging is enabled and both parsing methods execute with verbose output, it can reach 30+ entries. This is fine for logging but watch for the performance impact of string concatenation in debug messages on the hot path.

## Optimization Dependency Graph

```
Phase 1: Persistent WinHTTP -----> Phase 4: Predicted Outputs
       |                                  (requires endpoint branching)
       |
       +-----> Phase 5: API Retry with Backoff
                         (uses persistent object for recovery)

Phase 2: Clipboard Save/Restore   (independent)
Phase 3: Debug Event Noise         (independent)
Phase 6: Magic Number Constants    (independent)
```

**Recommended order:** Phase 3 (trivial, risk-free) -> Phase 6 (trivial, risk-free) -> Phase 1 (highest impact) -> Phase 2 (important UX) -> Phase 5 (resilience) -> Phase 4 (highest complexity, conditional on API support)

## Sources

- [AutoHotkey v2 Script Performance](https://www.autohotkey.com/docs/v2/misc/Performance.htm) -- official performance documentation
- [AHK Community: Optimization / Speed / Performance](https://www.autohotkey.com/boards/viewtopic.php?style=2&t=100472)
- [AHK Community: Faster Multiple WinHttp Requests](https://www.autohotkey.com/boards/viewtopic.php?t=91213)
- [Microsoft WinHTTP Keep-Alive / Connection Pooling](https://microsoft.public.winhttp.narkive.com/9NM27MTE/keep-alive)
- [WinHTTP Persistent Connections](https://groups.google.com/g/microsoft.public.winhttp/c/csNL1ItfoXg) -- confirms connection reuse across request handles
- [AHK v2 ClipWait Documentation](https://www.autohotkey.com/docs/v2/lib/ClipWait.htm) -- official clipboard handling
- [OpenAI Latency Optimization Guide](https://developers.openai.com/api/docs/guides/latency-optimization) -- predicted outputs, streaming, token reduction
- [OpenAI Predicted Outputs Guide](https://developers.openai.com/api/docs/guides/predicted-outputs) -- supported models, limitations
- [Predicted Outputs in Response API (Community)](https://community.openai.com/t/predicted-outputs-in-response-api/1373125) -- confirms Responses API does not yet support prediction parameter
- [AHK Community: Clipboard v2 Issues](https://www.autohotkey.com/boards/viewtopic.php?t=135810) -- clipboard error handling patterns
- [AHK Community: WinHttp COM Object Reuse](https://www.autohotkey.com/boards/viewtopic.php?t=128029) -- static variable pattern

---

*Architecture analysis: 2026-03-27*
