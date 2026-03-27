# Codebase Concerns

**Analysis Date:** 2026-03-27

## Security Issues

**API Key Exposed in Source Code:**
- Issue: OpenAI API key is hardcoded in plain text in `Universal Spell Checker.ahk` at line 877
- Files: `Universal Spell Checker.ahk` (line 877)
- Impact: Critical - API key is visible in git history, source control, and any backups. Attacker can abuse API, incur costs, and intercept requests
- Current state: Key is active and currently exposed
- Fix approach:
  - Immediately revoke the exposed key in OpenAI dashboard
  - Move API key to environment variable `OPENAI_API_KEY` or `.env` file (ensure `.env` is in `.gitignore`)
  - Load key at runtime: `apiKey := EnvGet("OPENAI_API_KEY")`
  - Add pre-commit hook to catch future hardcoded keys (regex check for `sk-proj-`)
  - Note: `.gitignore` is present but the .env file was already created and needs protection

**Clipboard Data Security:**
- Issue: Original and corrected text are stored unencrypted in `logs/spellcheck.jsonl` file, accessible to any process with file permissions
- Files: `Universal Spell Checker.ahk` (line 872 `logData.original` and throughout logging)
- Impact: Medium - sensitive text may be exposed if logs directory is compromised
- Current mitigation: Text is only logged locally, not transmitted beyond OpenAI API
- Recommendations:
  - Consider option to disable full-text logging for sensitive content
  - Implement log retention policy to auto-delete logs older than N days
  - Document that logs contain complete user text and should be protected

**HTML Log Viewer XSS Risk:**
- Issue: The `generate_log_viewer.py` script generates HTML and may fail to properly escape user text from logs
- Files: `generate_log_viewer.py`, output file `logs/viewer.html`
- Risk: If malicious text is in the log (e.g., crafted JSON with script tags), it could execute when viewing logs
- Current mitigation: Script appears to use `html.escape()` but this should be verified for all fields
- Recommendations:
  - Verify all user text from logs is properly HTML-escaped before rendering
  - Use a templating engine (Jinja2) instead of string concatenation for HTML generation
  - Add Content-Security-Policy headers to viewer HTML

## Known Issues

**Prompt Leak Safeguard - Incomplete Coverage:**
- Issue: `StripPromptLeak()` at `Universal Spell Checker.ahk` lines 167-200 uses a hardcoded string match for exact instruction leak detection
- Files: `Universal Spell Checker.ahk` (lines 167-200)
- Trigger: Occurs when AI accidentally echoes the system prompt in its output (rare but documented in `Scratchpad.md`)
- Current approach: Simple string check for exact phrase "instructions: " + promptInstructionText
- Limitations:
  - Only catches exact matches of the instruction text
  - Will fail if AI reformats or paraphrases the prompt
  - Does not detect partial echoes or fragmented leaks
- Recommendations:
  - Add fuzzy matching or semantic detection for prompt leaks
  - Log all instances to identify patterns
  - Consider adding this to pre-processing to instruct model not to echo

**Model Configuration Switch is Manual:**
- Issue: Changing active model requires editing source code and restarting script
- Files: `Universal Spell Checker.ahk` (line 18, `modelModule := "gpt-4.1"`)
- Impact: Medium - users must recompile/restart to test different models
- Fix approach:
  - Move model selection to config file (e.g., `config.json`)
  - Load config at startup with hot-reload capability
  - Add command-line argument support: `Universal Spell Checker.ahk --model gpt-5.1`

## Performance Bottlenecks

**API Response Parsing Uses Two Methods Sequentially:**
- Issue: Script first tries regex extraction, then falls back to full JSON parsing if regex fails
- Files: `Universal Spell Checker.ahk` (lines 953-1022)
- Current approach: Regex is attempted first (fast), Map-based parsing as fallback (slow)
- Bottleneck: If regex fails, code must allocate JSON parser and traverse entire object tree
- Improvement path:
  - Profile both methods to confirm regex is consistently faster
  - Consider improving regex pattern to handle edge cases (escaped quotes, nested objects)
  - Cache compiled regex pattern instead of recompiling on every request
  - If fallback is needed, log metrics on when/why fallback triggers

**Insertion Sort in LoadReplacements():**
- Issue: Uses O(n²) insertion sort to order replacements by length
- Files: `Universal Spell Checker.ahk` (lines 106-116)
- Current performance: Acceptable since replacement list is "typically tiny"
- Scaling concern: If replacements.json grows beyond 100 entries, performance degrades
- Fix approach: Switch to O(n log n) sort once list grows; currently fine given current size

**Full JSON Parsing Fallback - Recursive Overhead:**
- Issue: `JsonLoad()` parser at `Universal Spell Checker.ahk` (lines 541-677) uses recursive descent
- Files: `Universal Spell Checker.ahk` (lines 541-677)
- Impact: For deeply nested objects, recursion depth could be problematic (AutoHotkey stack limits)
- Current state: Works fine for OpenAI response structure (shallow nesting)
- Risk: If API response structure changes to deeper nesting, could hit recursion limits
- Recommendation: Switch to iterative parser or limit recursion depth with error handling

## Fragile Areas

**Regex Pattern for Output Text Extraction:**
- Files: `Universal Spell Checker.ahk` (line 742)
- Pattern: `'s)"type"\s*:\s*"output_text"[^}]*"text"\s*:\s*"((?:[^"\\]|\\.)*)"'`
- Why fragile:
  - Assumes specific field ordering in JSON response (`type` before `text` within object)
  - Uses `[^}]*` which could match across multiple objects if JSON structure changes
  - Doesn't account for escaped content that includes `"}` sequences
  - Single-line regex mode (`'s'` flag) matches newlines, could over-match
- Safe modification:
  - Validate field ordering with OpenAI API docs before changing
  - Add unit tests with sample responses including edge cases (escaped chars, nested objects)
  - Consider using a proper JSON parser as primary method instead of regex
  - Test coverage: Current implementation lacks dedicated tests for edge cases

**Clipboard Reading Fallback Chain:**
- Files: `Universal Spell Checker.ahk` (lines 203-244)
- Pattern: Tries HTML → Unicode → ANSI formats in sequence
- Why fragile:
  - Each format may have different encoding/decoding behavior
  - HTML extraction uses substring matching for markers (lines 303-317) - fragile if markers absent
  - ANSI fallback may garble non-ASCII characters
  - No validation that fallback format actually contains valid text
- Safe modification:
  - Add explicit length/content validation after each fallback
  - Test with various Windows clipboard formats (Office, Rich Text, Images)
  - Log which format was actually used so users can debug clipboard issues
  - Add tests for each format branch

**JSON Parser Error Handling:**
- Files: `Universal Spell Checker.ahk` (lines 541-677)
- Issues:
  - Parser throws exceptions for malformed JSON but doesn't provide line/column info
  - Unicode escape sequences validated with regex but may fail on some sequences
  - Surrogate pair handling (lines 663-675) is complex and not well-tested
- Test coverage gaps:
  - No tests for invalid escape sequences like `\u000G`
  - No tests for orphaned surrogate pairs
  - No tests for numbers at extreme bounds (very large/small)

## Test Coverage Gaps

**No Unit Tests for Core Functions:**
- Missing coverage:
  - `ApplyReplacements()` - replacement logic with URL protection
  - `StripPromptLeak()` - prompt leak detection and removal
  - `ExtractTextFromResponseRegex()` - JSON extraction edge cases
  - `JsonLoad()` - JSON parser with various inputs
- Files: `Universal Spell Checker.ahk` (entire codebase lacks test files)
- Risk: High - these functions have complex logic with edge cases; bugs go undetected
- Priority: High
- Recommendation:
  - Create test file `Universal Spell Checker.test.ahk` with automated test suite
  - Test replacements with various inputs: URLs, special chars, overlapping variants
  - Test prompt leak with variations of instruction text
  - Test JSON extraction with real OpenAI responses and edge cases

**No Integration Tests:**
- Missing: End-to-end tests with mock API server
- Risk: API integration logic not validated before deployment
- Recommendation:
  - Create mock HTTP server that returns sample responses
  - Test full flow: clipboard read → API call → parsing → replacement → paste

**No Error Recovery Tests:**
- Missing: Tests for error scenarios (API timeout, malformed response, clipboard failure)
- Risk: Error paths not exercised; hidden bugs in error handling
- Files affected: Lines 904-928 (API error handling), 859-864 (clipboard timeout)

## Scaling Limits

**Clipboard Size Limit:**
- Current capacity: No explicit limit, but Windows clipboard API has practical limits (~2GB theoretical)
- Practical limit: AutoHotkey string operations become slow above 1MB
- Current behavior: Script will attempt to process any size
- Scaling path: Add input size check, reject text > 100KB with user message

**Log File Rotation:**
- Current capacity: Rotates at 1MB per file
- Mechanism: Creates dated archive files (lines 334-351)
- Potential issue: Unlimited number of archive files accumulates over time
- Scaling path:
  - Implement retention policy (keep last N archives, delete older)
  - Add optional compression to reduce disk usage
  - Document expected disk usage over time

**Replacements JSON Size:**
- Current size: ~2.5KB with 80+ variant entries
- Scaling: O(n) per text processed; currently negligible at n=80
- Limit before degradation: ~10,000 entries would add noticeable latency
- Current: Not a concern; current approach scales fine

## Dependencies at Risk

**AutoHotkey v2.0 Language Dependency:**
- Risk: AutoHotkey is a niche language with small community; if project abandons v2, support ends
- Impact: Script becomes unmaintainable if AHK v2 breaks
- Current: AutoHotkey is actively maintained, no immediate risk
- Migration plan:
  - Consider C# rewrite for Windows native integration (mentioned in Scratchpad.md as future feature)
  - Document how to migrate to alternative scripting language

**OpenAI API Endpoint `/v1/responses`:**
- Risk: Responses API is newer; unclear if it will be maintained long-term vs being deprecated
- Impact: If OpenAI deprecates Responses API, script breaks
- Current: API is actively used for reasoning models (gpt-5.1, gpt-5-mini)
- Mitigation:
  - Monitor OpenAI API changelog
  - Keep fallback to Chat Completions API if needed
  - Test migration path to `/v1/chat/completions` if Responses API deprecated

## Missing Critical Features

**No Offline Mode:**
- Problem: Script entirely depends on OpenAI API; no local spell checking fallback
- Blocks: Can't spell check without internet connection
- Recommendation: Explore local model options (mentioned in Scratchpad.md) like Ollama for offline fallback

**No Undo/Rollback:**
- Problem: Once text is pasted into application, it's permanent; no way to revert
- Blocks: Mistakes can't be corrected without manual undo in target app
- Recommendation:
  - Keep original text in clipboard after paste so Ctrl+Z can restore
  - Add "Show Diff" feature to preview changes before pasting

**No Diff Visualization:**
- Problem: Users can't see what changed before the text is pasted
- Blocks: Quality assurance; can't verify AI output before replacing
- Recommendation: Show side-by-side diff UI (mentioned in Scratchpad.md)

**No Per-App Configuration:**
- Problem: Some apps need special paste behavior; currently only `SendText()` toggle per app
- Blocks: Can't customize behavior per application
- Recommendation:
  - Expand `sendTextApps` config to full app-specific config object
  - Allow per-app settings for verbosity, model, replacements

## Code Quality Issues

**Extensive Conditional Debug Logging:**
- Issue: Script contains many `logData.events.Push("DEBUG: ...")` statements throughout parsing
- Files: `Universal Spell Checker.ahk` (lines 955, 958, 961, etc.)
- Impact: Log files become cluttered with debug noise; adds code noise
- Fix: Implement proper debug level system (DEBUG, INFO, WARN, ERROR) and only log DEBUG when explicitly enabled

**Raw Request/Response Stored in Logs:**
- Issue: Entire API payload and response stored as strings in logs
- Files: `Universal Spell Checker.ahk` (lines 893, 933)
- Impact: Logs become very large; sensitive data exposed
- Fix: Store only summary info by default; add flag to include full payloads for debugging

**Magic Numbers Throughout Code:**
- Issue: Timeouts, retry counts, array indices are hardcoded
- Files: `Universal Spell Checker.ahk` (line 896: timeouts, line 378: 30000ms)
- Fix: Extract to constants at top of script

---

*Concerns audit: 2026-03-27*
