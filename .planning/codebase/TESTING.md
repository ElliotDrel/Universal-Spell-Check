# Testing Patterns

**Analysis Date:** 2026-03-27

## Test Framework

**Runner:**
- No dedicated test framework found
- AutoHotkey v2 testing is manual (no Jest, Vitest, pytest, etc. configured)
- Python component (`generate_log_viewer.py`) runs standalone, no pytest configured

**Assertion Library:**
- None configured
- Testing is implicit via manual runs and log inspection

**Run Commands:**
```bash
# Run the active script (hotkey activation)
# AutoHotkey Universal Spell Checker.ahk

# Generate and view logs
python generate_log_viewer.py              # reads logs/*.jsonl, writes logs/viewer.html, opens browser
python generate_log_viewer.py --no-open   # same, but skip opening in browser

# View logs in the generated HTML viewer
# Open logs/viewer.html in browser (auto-launched with --open flag)
```

## Test File Organization

**Location:**
- No dedicated test files
- Testing is implicit: run hotkey Ctrl+Alt+U and inspect logs

**Naming:**
- Not applicable (no test files)

**Structure:**
- Manual test workflow:
  1. Activate spell checker with Ctrl+Alt+U
  2. Script runs full flow: clipboard → API → response parsing → replacements → paste
  3. Results logged to `logs/spellcheck.jsonl`
  4. Inspect via `python generate_log_viewer.py` to view HTML dashboard

## Test Structure

**Suite Organization:**
- Tests are implicit in the main hotkey handler: `^!u::`
- All code paths instrumented with `logData.events.Push()` for tracing
- Debug events prefixed with "DEBUG:" to mark diagnostic output

**Patterns:**
No setup/teardown framework, but initialization pattern in hotkey:
```autohotkey
^!u:: {
    startTime := A_TickCount
    logData := {
        original: "",
        error: "",
        events: [],
        timings: {
            clipboardCaptured: 0,
            responseReceived: 0,
            ...
        }
    }
    ; ... main workflow ...
}
```

**Event Logging Pattern (serves as implicit test instrumentation):**
```autohotkey
logData.events.Push("Clipboard captured (" . (StrLen(originalText)) . " chars)")
logData.events.Push("Payload prepared for " . apiModel . " (verbosity: " . Verbosity . ")")
logData.events.Push("Request sent")
logData.events.Push("Response received")
logData.events.Push("DEBUG: Trying regex extraction")
logData.events.Push("DEBUG: Regex extraction SUCCESS, length=" . StrLen(correctedText))
logData.events.Push("DEBUG: Map-based parsing returned empty")
logData.events.Push("Post-processing: " . applied.Length . " replacement(s) applied")
logData.events.Push("Prompt-leak safeguard TRIGGERED")
logData.events.Push("Text pasted via clipboard - COMPLETE")
```

## Mocking

**Framework:**
- No mocking framework (AutoHotkey doesn't have built-in mocks)
- Testing strategy: real API calls to OpenAI (logging captures actual behavior)

**Patterns:**
- Real HTTP operations via `WinHttp.WinHttpRequest.5.1` COM object
- Real filesystem: replacements loaded from actual `replacements.json`, logs written to `logs/spellcheck.jsonl`
- Real clipboard operations: `Send("^c")` and `Send("^v")` interact with actual system clipboard

**What to Mock (if test framework added):**
- OpenAI API endpoint (expensive; currently does real calls)
- Clipboard operations (replace `Send("^c")` with mock)
- File I/O (replace `FileRead()`, `FileAppend()` with test doubles)
- Timing (replace `A_TickCount` with injectable clock)
- Window focus (replace `WinActive()`, `WinGetTitle()` with fixtures)

**What NOT to Mock (test in isolation):**
- JSON parsing logic (`JsonLoad`, regex extraction) - test with actual JSON payloads
- Post-processing replacements - test with real variant matching
- Prompt-leak safeguard - test with real instruction strings
- Timing calculations - verify delta math with actual durations

## Fixtures and Factories

**Test Data:**
- None formalized, but replacements.json serves as fixture:
  ```json
  {
    "buildpurdue": ["buildPurdue", "BuildPurdue", ...],
    "GitHub": ["Git Hub", "git hub", ...]
  }
  ```
- Example model configurations hardcoded in model selector:
  - `gpt-4.1`: temperature=0.3, verbosity="medium"
  - `gpt-5.1`: reasoning effort="none", verbosity="low"
  - `gpt-5-mini`: reasoning effort="minimal", verbosity="low"

**Location:**
- `replacements.json` alongside script (configuration, not test-specific)
- Model configurations in `^!u::` hotkey handler
- Test inputs are live text copied to clipboard

## Coverage

**Requirements:**
- No coverage target enforced
- No coverage tool configured

**View Coverage:**
- Not applicable (no test runner)

## Test Types

**Unit Tests:**
- Not formalized
- Implicit unit testing via function calls:
  - `JsonLoad(rawJson)` tested by parsing actual API responses
  - `ApplyReplacements(text, &applied, &urlCount)` tested by real replacements.json
  - `StripPromptLeak(text, promptText, &details)` tested by response parsing

**Integration Tests:**
- Main spell-check workflow is integration test:
  1. Capture clipboard text
  2. Call OpenAI API (real endpoint)
  3. Parse response
  4. Apply replacements
  5. Paste result
  6. Log everything

**E2E Tests:**
- Running Ctrl+Alt+U with various text inputs is the E2E test
- Observation points:
  - Text successfully replaced in active window
  - Log entry created in `spellcheck.jsonl`
  - Timing data captured
  - Error handling and fallbacks work

**Manual Test Workflow (primary):**
1. Select text with grammar/spelling issues
2. Press Ctrl+Alt+U
3. Verify output correct in window
4. Run `python generate_log_viewer.py` to inspect detailed log
5. Check timing breakdown to identify slow steps
6. Verify token counts match API response
7. Validate post-processing replacements applied correctly

## Common Patterns

**Async Testing:**
- No async/await in AutoHotkey (synchronous COM calls)
- `ClipWait(1)` waits for clipboard with 1-second timeout
- `SetTimer(() => ..., -N)` used for deferred operations (logging)

**Timing verification pattern (in logging):**
```autohotkey
logData.timings.clipboardCaptured := A_TickCount
; ... do work ...
logData.timings.responseReceived := A_TickCount
; Delta calculated at log time:
tApi := (responseReceived > requestSent) ? (responseReceived - requestSent) : 0
```

**Error Testing:**
```autohotkey
; API error handling:
if (http.Status != 200) {
    logData.error := "API Error: " . http.Status . " - " . http.StatusText
    errPreview := GetUtf8Response(http)
    logData.rawResponse := SubStr(errPreview, 1, 1000)
    ToolTip("API Error: " . http.Status)
    return  ; exit handler, FinalizeRun still called
}

; Parse error handling:
try {
    correctedText := ExtractTextFromResponseRegex(response)
} catch Error as regexErr {
    logData.events.Push("Regex extraction failed: " . regexErr.Message)
}

; Silent fallback:
if (correctedText = "") {
    try {
        responseObj := JsonLoad(response)
        ; ... traverse responseObj ...
    } catch Error as parseErr {
        logData.events.Push("JSON parse error: " . parseErr.Message)
    }
}
```

**Logging assertion pattern (post-run verification):**
```
# In generate_log_viewer.py viewer:
1. Check "status" field = "SUCCESS"
2. Verify "duration_ms" < 5000 (acceptable)
3. Inspect "events" array for any "DEBUG:" entries indicating fallbacks
4. Validate "tokens" match API response
5. Check "replacements.count" > 0 if variants expected to match
6. Verify "text_changed" = true when input != output
7. Look for "Prompt-leak safeguard TRIGGERED" if instruction leak suspected
```

## Test Coverage Gaps

**Untested areas (identified):**
- Surrogate pair handling in JSON unicode escape (`__JsonParseUnicodeEscape`) - complex edge case
  - Files: `Universal Spell Checker.ahk` lines 663-676
  - Risk: malformed JSON with surrogate pairs could crash
  - Priority: Medium (rare in typical spell-check payloads)

- HTML fragment extraction fallback (`__ExtractHtmlFragment`) - two parsing strategies with different position calculations
  - Files: `Universal Spell Checker.ahk` lines 302-318
  - Risk: If first strategy fails, second might use wrong character positions
  - Priority: Medium (clipboard HTML format varies by source)

- Model selection logic for gpt-5-mini - unique "minimal" effort value untested
  - Files: `Universal Spell Checker.ahk` lines 39-43
  - Risk: Typo in effort string would fail silently (API would reject)
  - Priority: Low (caught by API error logging)

- Clipboard history policy tagging on systems without support
  - Files: `Universal Spell Checker.ahk` lines 248-265
  - Risk: `RegisterClipboardFormat` might fail on older Windows
  - Priority: Low (graceful fallback with event logging)

- Log rotation when filesystem full
  - Files: `Universal Spell Checker.ahk` lines 334-351
  - Risk: `FileMove` failure silently caught, logs stop appending
  - Priority: Low (user would notice missing logs)

- Response streaming/chunking - current code assumes single text output block
  - Files: `Universal Spell Checker.ahk` lines 974-1007
  - Risk: If API returns multiple output blocks, only first is used
  - Priority: Medium (Responses API may chunk large responses)

## Testing Best Practices in Use

**What's working well:**
- Comprehensive event logging enables post-mortem debugging
- Two independent parsing strategies (regex + Map) provide fallback safety
- Timing instrumentation makes performance problems visible
- Silent failures with logging means core functionality never breaks
- Token tracking validates API integration end-to-end

**What could improve:**
- Add unit test harness for JSON parsing (`JsonLoad`, escape handling)
- Add benchmark for post-processing replacements (microsecond-level perf tracking)
- Add regression test file with known problematic inputs (smart quotes, unicode, control chars)
- Formalize E2E test scenarios (various models, large/small inputs, error conditions)

---

*Testing analysis: 2026-03-27*
