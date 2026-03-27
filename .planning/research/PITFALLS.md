# Pitfalls Research

**Domain:** AutoHotkey v2 AI spell checker optimization (clipboard, HTTP, JSON parsing, COM objects)
**Researched:** 2026-03-27
**Confidence:** HIGH (verified against AHK v2 docs, community forums, OpenAI API docs, and current codebase)

## Critical Pitfalls

### Pitfall 1: Clipboard Race Condition on Paste — Prior Content Gets Pasted Instead of Corrected Text

**What goes wrong:**
After the script sets `A_Clipboard := correctedText` and immediately fires `Send("^v")`, the target application pastes the **old** clipboard content instead of the corrected text. This happens intermittently — maybe 5-15% of the time — making it extremely frustrating to debug because it "works most of the time."

**Why it happens:**
The Windows clipboard is asynchronous. Setting `A_Clipboard` triggers a clipboard chain update that propagates to all clipboard viewers and managers (including Windows 10/11's built-in clipboard history). The `Send("^v")` fires before the target application's clipboard listener has finished processing the update. Third-party clipboard managers (Ditto, ClipboardFusion, Windows clipboard history via Win+V) add additional delay. The current code at line 1072 sets `A_Clipboard` and sends `^v` on the very next line with zero delay.

**How to avoid:**
- Add a `ClipWait(0.5)` after setting `A_Clipboard` and before `Send("^v")` to confirm the clipboard actually updated. The script already uses `ClipWait` after `Send("^c")` (line 859) but does **not** use it before the paste.
- Alternatively, add a `Sleep(50)` between the assignment and the paste — crude but effective.
- Consider using `A_Clipboard := ""` then `A_Clipboard := correctedText` then `ClipWait(0.5)` to force a clean transition that `ClipWait` can detect.

**Warning signs:**
- Users report "sometimes it pastes the old text" or "it pasted what I had before."
- Log entries show `text_changed: true` but the user sees unchanged text in the target app.
- The problem appears more frequently on slower machines, in Electron apps (VS Code, Slack, Discord), or when clipboard managers are running.

**Phase to address:**
Clipboard optimization phase. This is the single highest-impact reliability fix.

---

### Pitfall 2: Modifier Key Sticking — Ctrl Gets "Stuck" After Hotkey-Triggered Copy/Paste

**What goes wrong:**
After `^!u` fires (which requires holding Ctrl+Alt+U), the script sends `Send("^c")` and later `Send("^v")`. Because the user's physical Ctrl key may still be physically held when `Send("^c")` executes, the system can get confused about modifier state. After the spell check completes, the user's next keystrokes behave as if Ctrl is stuck down — typing "a" does Ctrl+A (select all), typing "s" does Ctrl+S (save), etc.

**Why it happens:**
AHK's `SendInput` mode synthesizes keystroke events at the driver level. When the hotkey fires with Ctrl already held, `Send("^c")` tries to press Ctrl again (it's already down), and on release it may send a premature `{Ctrl Up}` that conflicts with the physical key state. AHK v2 has had documented race conditions with modifier state tracking when other keyboard hooks are installed (e.g., other AHK scripts, Karabiner-like tools, gaming software).

**How to avoid:**
- Add `KeyWait("Control")` and `KeyWait("Alt")` at the start of the hotkey handler, before any `Send` commands. This ensures the user has released the hotkey modifiers before the script starts injecting its own keystrokes.
- Add `Send("{Ctrl Up}{Alt Up}")` at the **end** of the handler (in the `finally` block) as a safety net.
- Set `SetKeyDelay(-1, -1)` at script startup (v2 default for SendInput, but explicit is safer).

**Warning signs:**
- Users report "my keyboard went crazy after spell check."
- After spell check, the next typed character triggers a keyboard shortcut instead of inserting text.
- The issue is more common when users trigger the hotkey rapidly or hold the keys longer.

**Phase to address:**
Clipboard/input optimization phase. Add `KeyWait` calls and modifier cleanup in `finally` block.

---

### Pitfall 3: WinHTTP COM Object Recreation Destroys Connection Pooling — 200-400ms Penalty Per Request

**What goes wrong:**
Creating a new `ComObject("WinHttp.WinHttpRequest.5.1")` on every hotkey press (line 895) forces a fresh TCP+TLS handshake to `api.openai.com` every single time. This adds 200-400ms of network overhead that is completely unnecessary because WinHTTP natively supports connection pooling and keep-alive.

**Why it happens:**
The intuitive pattern is "create object, use it, let it be garbage collected." This works correctly but wastes the connection pool. WinHTTP reuses TCP connections **only within the same session** (same COM object instance or same session handle). When the COM object is destroyed and recreated, the connection pool is discarded. The current code creates a brand-new WinHTTP object inside the hotkey handler on every invocation.

**How to avoid:**
- Create the WinHTTP COM object **once** at script startup as a global/persistent variable.
- Reuse it across invocations. WinHTTP handles connection pooling automatically — subsequent `.Open()` / `.Send()` calls on the same object will reuse the existing TCP connection if it's still alive (idle connections expire after ~2 minutes).
- Always read the full response body (`ResponseBody` or `ResponseText`) before the next request — unread data prevents connection reuse.
- WinHTTP's pooled connections expire after ~2 minutes of idle time. For a spell checker used every few minutes, this is usually fine. For longer gaps, the first request after idle will be slower (unavoidable TLS handshake), but subsequent rapid corrections will be fast.

**Warning signs:**
- API request timing (`timings.responseReceived - timings.requestSent`) is consistently 400ms+ even for short texts.
- Network traces show a full TLS handshake on every request to api.openai.com.
- The `api_ms` timing never drops below ~350ms regardless of text length.

**Phase to address:**
HTTP optimization phase. This is likely the single largest latency reduction available (estimated 150-300ms savings on subsequent requests within a session).

---

### Pitfall 4: Regex JSON Extraction Breaks on OpenAI Response Format Changes

**What goes wrong:**
The regex at line 742 (`'s)"type"\s*:\s*"output_text"[^}]*"text"\s*:\s*"((?:[^"\\]|\\.)*)"'`) assumes a specific field ordering in the JSON response (`type` before `text` within the same object). If OpenAI adds new fields between `type` and `text`, reorders them, or nests the structure differently, the regex silently fails and the script falls back to the slow Map-based parser.

**Why it happens:**
JSON objects are unordered by specification. OpenAI's Responses API has already undergone breaking changes (removal of `response_format`, changes to structured output parameters). The API documentation does not guarantee field ordering. The regex uses `[^}]*` between `type` and `text` which will fail if a nested object (containing `}`) appears between them. Community reports from late 2025 show OpenAI intermittently returning malformed or restructured JSON.

**How to avoid:**
- Keep the regex as the fast path but make it more resilient. Consider two regex patterns: one for `type-before-text` ordering and one for `text-before-type` ordering.
- Ensure the Map-based fallback parser is robust and well-tested — it is the safety net.
- Add monitoring: log which parser succeeded. If the fallback starts triggering frequently, it means the regex needs updating.
- Consider using OpenAI's Structured Outputs (`text.format.type: "json_schema"` with `strict: true`) to guarantee a predictable response shape. This adds a few tokens of overhead but eliminates parsing fragility entirely.

**Warning signs:**
- Log events showing `"DEBUG: Regex extraction returned empty"` followed by `"DEBUG: Map-based parsing SUCCESS"` — this means the regex is failing silently.
- Sudden increase in `parse_ms` timings (regex is sub-millisecond; Map parser is 5-20ms).
- API response samples in logs show different field ordering than expected.

**Phase to address:**
JSON parsing optimization phase. Harden the regex and ensure fallback path performance is acceptable.

---

### Pitfall 5: OpenClipboard Deadlock — Script Hangs and Blocks All Other Applications

**What goes wrong:**
The `GetClipboardText()` function (line 206) calls `DllCall("OpenClipboard", "ptr", 0)` to open the Windows clipboard. If an exception occurs between `OpenClipboard` and `CloseClipboard`, or if the script is interrupted (e.g., by another hotkey firing, a timer, or an unhandled error), the clipboard stays locked. **Every other application on the system** that tries to access the clipboard will hang until the clipboard is released.

**Why it happens:**
The Windows clipboard is a system-wide exclusive lock. Only one process can have it open at a time. The current code uses a `try/finally` block (line 214/239) which is good, but there are subtleties:
- If AHK's thread is interrupted between `OpenClipboard` and the `try` statement, `CloseClipboard` will never be called.
- The `__HtmlFragmentToPlainText` function (line 322) creates a `ComObject("HTMLFile")` **while the clipboard is still open**. If the HTMLFile COM object initialization hangs or takes too long, the clipboard remains locked for that entire duration.
- The `SetClipboardHistoryPolicy` function (line 248) opens the clipboard **again** — if it's called while the clipboard is already open from `GetClipboardText`, it will fail silently (return false) but this is harmless. However, if the calling order changes, a double-open without a close in between would deadlock.

**How to avoid:**
- Move the `ComObject("HTMLFile")` creation **outside** the clipboard lock. Copy the raw HTML data, close the clipboard, **then** parse the HTML.
- Minimize the time between `OpenClipboard` and `CloseClipboard` to just the raw memory read operations.
- Add a timeout mechanism: if `OpenClipboard` fails (returns 0), retry 2-3 times with 10-50ms delays, then fall back to `A_Clipboard`.
- Consider using `A_ScriptHwnd` as the window handle parameter instead of `0` for better diagnostics.

**Warning signs:**
- The script hangs indefinitely on `OpenClipboard` because another app already has the clipboard locked.
- Other applications visibly freeze when trying to paste/copy while the script is processing.
- The HTMLFile COM object occasionally takes 50-200ms to initialize, during which the clipboard is locked.

**Phase to address:**
Clipboard optimization phase. Restructure `GetClipboardText` to minimize lock duration and move COM object parsing outside the lock.

---

### Pitfall 6: ADODB.Stream COM Object Leak Under Error Conditions

**What goes wrong:**
The `GetUtf8Response()` function (line 720) creates an `ADODB.Stream` COM object for UTF-8 decoding. If `stream.Write(http.ResponseBody)` throws (e.g., because the HTTP response body is empty or the connection was reset), the `finally` block checks `stream.State` and calls `stream.Close()`, but the stream variable itself is not explicitly released. Over hundreds of spell check invocations, leaked stream objects accumulate, slowly increasing the AHK process's memory footprint.

**Why it happens:**
AHK v2 uses reference-counting for COM objects. Local variables are released when the function exits, so in normal operation this is fine. But if an exception propagates from `GetUtf8Response` and the caller doesn't handle it cleanly, or if the stream enters an unexpected state, the reference may not be dropped correctly. The `ADODB.Stream` COM object holds internal buffers that are only released on `.Close()` — simply dropping the reference may not free the underlying native memory immediately.

**How to avoid:**
- Always call `stream.Close()` in the `finally` block regardless of `stream.State` (wrap the close itself in a try-catch).
- Set `stream := ""` after closing to explicitly release the COM reference.
- For the specific use case of reading a UTF-8 response body, consider whether `ADODB.Stream` is even needed. If the WinHTTP response is already UTF-8 and AHK v2 handles it correctly via `ResponseText`, the stream is unnecessary overhead.
- Profile memory usage over 100+ invocations to verify no growth.

**Warning signs:**
- AHK process memory slowly grows over a day of heavy use (e.g., from 15MB to 50MB+).
- `GetUtf8Response` errors in logs but the script continues running.
- Task Manager shows increasing "Private Working Set" for the AutoHotkey process.

**Phase to address:**
HTTP optimization phase. Evaluate whether ADODB.Stream is still necessary; if so, harden the cleanup.

---

### Pitfall 7: HTMLFile COM Object — Using a Deprecated IE Engine Inside a Clipboard Lock

**What goes wrong:**
The `__HtmlFragmentToPlainText()` function (line 320) creates a `ComObject("HTMLFile")` which internally loads `mshtml.dll` (Internet Explorer's rendering engine). This COM object is:
1. Created inside the clipboard lock (called from within `GetClipboardText`'s `try` block while `OpenClipboard` is held).
2. Based on a deprecated engine (IE/Trident) that Microsoft has retired.
3. Variable in initialization time — sometimes instant, sometimes 50-200ms, especially on first invocation.

**Why it happens:**
The HTMLFile approach was the standard way to convert HTML to plain text in AHK for years. It works by actually instantiating an embedded IE rendering engine, writing HTML to it, and reading `body.innerText`. It's heavyweight for what amounts to a tag-stripping operation. The deprecation of IE means this COM class could stop working in a future Windows update (though Microsoft has committed to mshtml.dll security patches through at least 2029).

**How to avoid:**
- Replace `ComObject("HTMLFile")` with a regex-based HTML-to-text converter for the simple fragments this script handles (clipboard HTML is typically simple: `<p>`, `<br>`, `<span>` tags with inline styles).
- Move the HTML parsing **outside** the clipboard lock entirely — read the raw bytes from the clipboard, close it, then convert.
- A regex strip like `RegExReplace(html, "<[^>]+>", "")` followed by `RegExReplace(result, "&amp;|&lt;|&gt;|&nbsp;|&quot;", ...)` for entity decoding handles 99% of clipboard HTML fragments.
- If full DOM parsing is ever needed, consider the AhkSoup library (pure AHK v2, no COM dependency).

**Warning signs:**
- Clipboard operations occasionally take 100-300ms (check `timings.clipboardCaptured` in logs).
- The very first spell check after script launch is noticeably slower than subsequent ones (HTMLFile COM object cold-start).
- Future Windows updates break `ComObject("HTMLFile")` entirely — the script would silently fall through to Unicode/ANSI clipboard text.

**Phase to address:**
Clipboard optimization phase. Replace HTMLFile with regex-based stripping and move parsing outside the lock.

---

### Pitfall 8: Backslash Unescape Order in Regex JSON Extraction Creates Corrupted Output

**What goes wrong:**
The `ExtractTextFromResponseRegex` function (lines 744-758) unescapes JSON string escapes in a specific order. The current order handles Unicode escapes first, then standard escapes, with backslash (`\\` -> `\`) done last. This is correct. But if someone "optimizes" by reordering these replacements (e.g., doing backslash first for "simplicity"), the output becomes corrupted: `\\n` (literal backslash + n) would become `\n`, then the `\n` -> newline replacement would incorrectly convert it to a newline character.

**Why it happens:**
This is a classic JSON unescape ordering bug. The correct order is:
1. Unicode escapes (`\uXXXX`) first
2. Standard escapes (`\"`, `\n`, `\r`, `\t`, `\/`) second
3. Backslash (`\\` -> `\`) **last**

The current code does this correctly. The pitfall is that during optimization, someone refactors the unescape logic and inadvertently reorders the operations. This is especially dangerous because it passes basic tests (most text doesn't contain literal backslashes) but fails on edge cases (file paths, code snippets, regex patterns in user text).

**How to avoid:**
- Add a comment block explicitly explaining **why** the order matters, with an example of what breaks if changed.
- Add test cases that include literal backslashes in the input text (`C:\\Users\\...` in spell-checked text should round-trip correctly).
- Consider replacing the sequential `StrReplace` calls with a single-pass unescape function that handles all escapes in one traversal (eliminates the ordering concern entirely).

**Warning signs:**
- User text containing file paths, code, or regex patterns comes back corrupted after spell check.
- Literal `\n` in user text gets converted to actual newlines.
- Log entries show `text_changed: true` when the AI made no spelling corrections — the "change" was caused by unescape corruption.

**Phase to address:**
JSON parsing optimization phase. Document the ordering constraint; optionally refactor to single-pass.

---

### Pitfall 9: Synchronous HTTP Blocking the UI Thread — Script Appears Frozen During API Call

**What goes wrong:**
The `http.Open("POST", apiUrl, false)` call at line 897 uses `false` for the async parameter, making the HTTP request **synchronous**. During the 0.5-3 second API call, the entire AHK thread is blocked. The script cannot respond to other hotkeys, timers, or window messages. If the API takes longer (network issues, rate limiting), the user sees a completely unresponsive script. If they press Ctrl+Alt+U again during the wait, the second invocation is queued and fires immediately after the first completes, potentially causing a double-paste.

**Why it happens:**
Synchronous HTTP is simpler to code — no callbacks, no state machines, no race conditions. It works fine when the API responds quickly. But it creates a blocking operation on AHK's single thread, which means no other hotkeys or timers can fire during the wait. With a 30-second timeout configured (line 896), the worst case is a 30-second freeze.

**How to avoid:**
- For most use cases, synchronous is actually acceptable here because the user is waiting for the result anyway. The key mitigation is:
  - Show a visual indicator (ToolTip "Checking..." or tray icon change) before the HTTP call so the user knows it's working.
  - Reduce the timeout from 30s to something reasonable like 10-15s — a 30-second spell check is useless.
  - Add hotkey suppression: use a `static isRunning` flag to prevent re-entry while a request is in flight.
- If async is desired, use `http.Open("POST", apiUrl, true)` with `http.WaitForResponse(timeout)`, which allows AHK's message pump to continue. But this adds complexity and the benefits are marginal for a single-shot request.

**Warning signs:**
- Users report "the script froze" or "nothing happened" when the API is slow.
- Double-pastes occur because the user retriggers the hotkey during the wait.
- The tooltip from a previous error never clears because the timer couldn't fire during the blocking call.

**Phase to address:**
HTTP optimization phase. Add re-entry guard and visual feedback; consider reducing timeout.

---

### Pitfall 10: Unicode Surrogate Pair Handling in Both JSON Parsers

**What goes wrong:**
Characters outside the Basic Multilingual Plane (emoji like an emoji rocket, mathematical symbols, CJK Extension B characters) are encoded as surrogate pairs in JSON (`\uD83D\uDE80` for the rocket emoji). The regex-based parser (line 747) handles `\uXXXX` escapes one at a time via `Chr(codepoint)`, which produces an isolated high surrogate followed by an isolated low surrogate instead of combining them into a single character. The Map-based fallback parser (lines 663-675) has surrogate pair handling but it's documented as "complex and not well-tested" in CONCERNS.md.

**Why it happens:**
AHK v2 uses UTF-16 internally, so surrogate pairs should work naturally if handled correctly. But the regex parser converts each `\uXXXX` independently without checking if the current value is a high surrogate (0xD800-0xDBFF) that should be combined with a following low surrogate (0xDC00-0xDFFF). For typical spell-checked English text this rarely matters. But if users spell-check text containing emoji or special Unicode characters, the output can be corrupted.

**How to avoid:**
- In the regex parser's Unicode unescape loop, after converting a `\uXXXX` to a codepoint, check if it's in the high surrogate range. If so, look ahead for a `\uXXXX` low surrogate and combine them: `Chr(0x10000 + ((high - 0xD800) << 10) + (low - 0xDC00))`.
- Add test cases with emoji-containing text to verify round-trip correctness.
- Since the AI model rarely introduces emoji in spell-checked output, this is low-frequency but high-impact when it hits.

**Warning signs:**
- Spell-checked text containing emoji shows garbled characters or replacement characters (the Unicode replacement character).
- Log entries show `raw_ai_output` containing `\uD83D\uDE00` (surrogate pair) but `output_text` shows two isolated characters.

**Phase to address:**
JSON parsing optimization phase. Low priority but should be fixed during any parser refactoring.

---

## Technical Debt Patterns

Shortcuts that seem reasonable but create long-term problems.

| Shortcut | Immediate Benefit | Long-term Cost | When Acceptable |
|----------|-------------------|----------------|-----------------|
| Hardcoded API key (line 877) | Zero startup latency, no config parsing | Key exposed in source/git history; can't rotate without code change; security risk | Never in shared/public code; acceptable only in single-user local scripts with .gitignore protection |
| New WinHTTP COM object per invocation | Simpler code, no global state management | 200-400ms TLS handshake penalty per request; connection pool wasted | Only acceptable if requests are very infrequent (>2 min apart) |
| Synchronous HTTP (`async: false`) | No callback complexity, linear code flow | Blocks entire AHK thread during API call; no other hotkeys or timers fire | Acceptable for single-purpose scripts where the user expects to wait |
| HTMLFile COM for HTML-to-text | Full DOM parsing, handles complex HTML | Depends on deprecated IE engine; slow cold start; heavyweight for tag stripping | Only if dealing with truly complex HTML; regex is better for clipboard fragments |
| Debug logging via `events.Push("DEBUG: ...")` | Easy to add, captures all state transitions | Clutters logs; adds string allocation overhead per invocation; no level filtering | During active development only; should be gated behind a debug flag for production |
| O(n^2) insertion sort for replacements | Simple code, works for small lists | Degrades noticeably above ~200 entries | Acceptable at current size (~80 entries); swap to proper sort if list grows |

## Integration Gotchas

Common mistakes when connecting to external services.

| Integration | Common Mistake | Correct Approach |
|-------------|----------------|------------------|
| OpenAI Responses API | Sending `temperature` to reasoning models (gpt-5.x) or `reasoning` to standard models (gpt-4.1) | Branch on model type; the current `apiUsesReasoning` flag pattern is correct and must be preserved during refactoring |
| OpenAI Responses API | Assuming JSON field order in response | Use both regex (fast, ordering-dependent) and full parser (correct, ordering-independent); log which one succeeds |
| OpenAI Responses API | Not reading the full response body before reusing the connection | Always call `GetUtf8Response()` or `ResponseText` completely; partial reads prevent WinHTTP connection reuse |
| Windows Clipboard API | Holding `OpenClipboard` lock while doing slow operations (COM object creation, HTML parsing) | Read raw data under the lock, close immediately, then process the data |
| Windows Clipboard API | Not handling `OpenClipboard` failure (another app has the lock) | Implement retry with short delays (10-50ms, 3 attempts), then fall back to `A_Clipboard` |
| ADODB.Stream | Not closing the stream before releasing the variable | Always call `.Close()` in a `finally` block, then set variable to `""` |

## Performance Traps

Patterns that work at small scale but fail as usage grows.

| Trap | Symptoms | Prevention | When It Breaks |
|------|----------|------------|----------------|
| Recreating WinHTTP COM object per request | Consistent 400ms+ request times | Persist the COM object globally | Every single request — this is not a scaling issue but a constant penalty |
| Sequential `StrReplace` calls in a loop over many replacements | Noticeable pause after API response returns | Use `InStr` to short-circuit (already done); ensure `Limit: 1` where possible; keep list sorted longest-first (already done) | Replacements list exceeds ~500 entries |
| Regex `while` loop for Unicode unescaping (line 747) | Slow parsing for responses with many Unicode characters | Pre-check with `InStr(text, "\u")` before entering loop; process all matches in one pass | Responses with 50+ Unicode escape sequences (rare for English text) |
| Full JSON parser as fallback | 5-20ms parse time vs sub-ms for regex | Ensure regex handles all known response shapes; the fallback should be a safety net, not a frequent path | Already at scale — the penalty is per-invocation |
| Logging raw request/response to JSONL | Log files grow 10-50KB per entry; frequent rotation | Make raw capture optional via a config flag; log only summary fields by default | After ~100 entries per MB limit, rotation happens every few hours of heavy use |
| Loading `replacements.json` from disk on every invocation | ~1ms file I/O per press (invisible now) | Cache the parsed result; only reload if file modification time changed | Only matters if invocation rate is very high (>10/sec) — unlikely for a spell checker |

## Security Mistakes

Domain-specific security issues beyond general web security.

| Mistake | Risk | Prevention |
|---------|------|------------|
| API key hardcoded in source at line 877 | Key exposed via git history, file sharing, backups; anyone with access can incur API costs | Move to `EnvGet("OPENAI_API_KEY")` or a `.env` file (already in .gitignore); rotate the exposed key |
| Full input/output text logged unencrypted to `spellcheck.jsonl` | Sensitive text (passwords, medical info, legal docs) persists on disk | Add option to disable full-text logging; implement log retention/purge policy |
| Prompt injection via user text | Malicious text could override the system prompt: `"Ignore previous instructions and..."` | The instruction is prepended as `"instructions: ..."` not as a system message; consider using the API's `instructions` field instead of embedding in user content |
| Raw API error body logged (line 922) | API error responses could contain sensitive diagnostic info | Already limited to 1000 chars (good); ensure no auth tokens leak in error bodies |

## UX Pitfalls

Common user experience mistakes in this domain.

| Pitfall | User Impact | Better Approach |
|---------|-------------|-----------------|
| No visual feedback during API call | User doesn't know if the hotkey registered; presses it again causing double-paste | Show a brief ToolTip("Checking...") immediately on hotkey press; clear it when done |
| No undo path after paste | If AI makes a bad correction, user must manually undo in the target app (Ctrl+Z may not work perfectly in all apps) | Store original text; consider preserving it on clipboard so Ctrl+Z in the target app can restore it |
| Silent failure when nothing is selected | User presses Ctrl+Alt+U with no selection; gets a clipboard timeout after 1 second with no explanation | Show an immediate ToolTip("No text selected") if clipboard is empty after copy |
| Error tooltip disappears after 3-5 seconds | User might not see the error if they looked away | Use a longer timeout for errors (5-8 seconds); consider a sound/notification for failures |
| Spell check on already-correct text feels wasteful | 500ms+ round-trip for text that didn't change; user wonders "did it work?" | The `text_changed` flag exists in logging; surface it to the user via a brief "No changes needed" tooltip |

## "Looks Done But Isn't" Checklist

Things that appear complete but are missing critical pieces.

- [ ] **Connection reuse:** WinHTTP COM object is created per-invocation — looks like it "works" but wastes 200-400ms per request on TLS handshake. Verify by checking if the COM object persists between invocations.
- [ ] **Clipboard lock safety:** `GetClipboardText` has a try/finally for CloseClipboard — looks safe but the HTMLFile COM object creation happens inside the lock. Verify by timing `clipboardCaptured` in logs (should be <10ms; if >50ms, the lock is held too long).
- [ ] **Regex parser coverage:** The regex extracts text successfully — looks complete but silently fails on reordered JSON fields. Verify by checking logs for fallback parser activation frequency.
- [ ] **Modifier key safety:** The hotkey handler copies and pastes — looks like it works but doesn't ensure modifiers are released. Verify by holding Ctrl+Alt+U for >500ms, then immediately typing — check if typed characters are correct.
- [ ] **Error handling completeness:** The outer try/catch/finally looks comprehensive — but errors in `FinalizeRun` or `LogDetailed` (called from finally) would be silently swallowed. Verify by temporarily introducing a logging error and checking if it surfaces.
- [ ] **UTF-8 correctness:** ADODB.Stream handles UTF-8 decoding — looks correct but smart quotes and em-dashes are the most common edge cases. Verify by spell-checking text with curly quotes and checking for mojibake in the output.
- [ ] **Surrogate pair handling:** JSON parser processes `\uXXXX` escapes — looks complete but the regex parser doesn't combine surrogate pairs. Verify by spell-checking text containing emoji and checking the output.

## Recovery Strategies

When pitfalls occur despite prevention, how to recover.

| Pitfall | Recovery Cost | Recovery Steps |
|---------|---------------|----------------|
| Clipboard race (old text pasted) | LOW | User presses Ctrl+Z in target app to undo, then re-triggers spell check. No data loss — just annoyance. |
| Stuck modifier key | LOW | User presses and releases Ctrl/Alt/Shift physically. If persistent, press the "reset" hotkey (if one is added) or restart the script. |
| WinHTTP connection not reused | NONE | No recovery needed — it's a performance issue, not a failure. Fix is purely optimization. |
| Regex parser failure (falls to Map parser) | LOW | Transparent to user — the Map parser produces the same result, just slower. Log monitoring detects it. |
| OpenClipboard deadlock | HIGH | Other applications freeze. User must end the AHK process via Task Manager. Data in the current clipboard is lost. This is why minimizing lock duration is critical. |
| ADODB.Stream memory leak | LOW | Restart the AHK script periodically. Memory reclaimed on process exit. |
| HTMLFile COM deprecation break | MEDIUM | Script falls through to Unicode/ANSI text (no HTML stripping). Formatting noise (empty paragraphs, spans) may appear in spell-checked output until the parser is replaced. |
| Surrogate pair corruption | MEDIUM | Manually fix the garbled characters. Not auto-recoverable — requires parser fix. |

## Pitfall-to-Phase Mapping

How roadmap phases should address these pitfalls.

| Pitfall | Prevention Phase | Verification |
|---------|------------------|--------------|
| Clipboard race on paste | Clipboard optimization | Test 50+ rapid spell checks; verify zero instances of stale-text paste |
| Stuck modifier keys | Clipboard optimization | Hold hotkey for varying durations; verify no stuck keys after release |
| WinHTTP connection waste | HTTP optimization | Compare `api_ms` before/after; expect 150-300ms improvement on subsequent requests |
| Regex JSON fragility | JSON parsing optimization | Test with reordered response fields; verify regex handles both orderings or fallback triggers cleanly |
| OpenClipboard deadlock | Clipboard optimization | Open clipboard from another app; verify script retries and falls back gracefully |
| ADODB.Stream leak | HTTP optimization | Run 200+ invocations; monitor process memory for growth |
| HTMLFile COM deprecation | Clipboard optimization | Replace with regex HTML stripping; verify clipboard timing drops to <5ms |
| JSON unescape ordering | JSON parsing optimization | Test with backslash-heavy input text (file paths, code); verify round-trip correctness |
| Synchronous HTTP blocking | HTTP optimization | Add re-entry guard and visual feedback; test rapid double-press behavior |
| Surrogate pair corruption | JSON parsing optimization | Test with emoji-containing text; verify correct output |

## Sources

- [AHK v2 Clipboard Issues (community)](https://www.autohotkey.com/boards/viewtopic.php?t=135810) — clipboard race conditions and timing
- [StealthPaste: Clipboard-Free Paste (community)](https://www.autohotkey.com/boards/viewtopic.php?style=19&t=138697) — clipboard reliability issues that motivated a clipboard-free approach
- [AHK v2 Changelog](https://www.autohotkey.com/docs/v2/ChangeLog.htm) — documented race condition fixes in Send/modifier handling
- [Stop modifier keys from getting stuck (community)](https://www.autohotkey.com/boards/viewtopic.php?t=81667) — modifier key sticking patterns and fixes
- [AHK v2 Performance Optimization (community)](https://www.autohotkey.com/boards/viewtopic.php?style=19&t=124617) — v2-specific performance tips
- [AHK v2 Official Performance Page](https://www.autohotkey.com/docs/v2/misc/Performance.htm) — official optimization guidance
- [WinHTTP Connection Pooling (Microsoft)](https://groups.google.com/g/microsoft.public.winhttp/c/p5ozI0j34Lw) — keep-alive and connection reuse behavior
- [Persistent HTTPS Connections (Medium)](https://medium.com/@techmsy/persistent-https-connections-reduce-api-call-time-by-50-3ca23723b336) — measured 50% latency reduction from connection reuse
- [AHK v2 COM Memory Leak (v2.0.5 fix)](https://github.com/AutoHotkey/AutoHotkey/releases/tag/v2.0.5) — COM enumeration reference counting fix
- [AHK v2 COM Object Memory Management (community)](https://www.autohotkey.com/boards/viewtopic.php?t=69569) — cleanup patterns for COM objects
- [AHK v2 HTMLFile COM Alternatives (community)](https://www.autohotkey.com/boards/viewtopic.php?t=126938) — HTMLFile deprecation and AhkSoup alternative
- [OpenAI Responses API Migration Guide](https://platform.openai.com/docs/guides/migrate-to-responses) — response_format breaking changes
- [OpenAI Malformed JSON Responses (community)](https://community.openai.com/t/chat-completion-responses-suddenly-returning-malformed-or-inconsistent-json/1368077) — intermittent JSON format issues in 2025
- [AHK v2 RegExMatch Performance (official docs)](https://www.autohotkey.com/docs/v2/lib/RegExMatch.htm) — regex caching and study option
- [AHK v2 Binary Compatibility / Surrogate Pairs](https://www.ahkscript.org/docs/v2/Compat.htm) — UTF-16 and surrogate pair handling
- [What ClipWait Does and Does Not Accomplish (community)](https://www.autohotkey.com/boards/viewtopic.php?style=19&t=134737) — ClipWait behavior nuances
- Current codebase analysis: `Universal Spell Checker.ahk`, `CONCERNS.md`, `CONVENTIONS.md`

---
*Pitfalls research for: AutoHotkey v2 AI spell checker optimization*
*Researched: 2026-03-27*
