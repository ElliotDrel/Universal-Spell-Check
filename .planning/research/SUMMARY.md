# Project Research Summary

**Project:** Universal Spell Checker — AHK v2 + OpenAI API Optimization
**Domain:** Windows hotkey-driven AI spell checking / text correction tool
**Researched:** 2026-03-27
**Confidence:** HIGH

## Executive Summary

The Universal Spell Checker is a mature, self-contained AutoHotkey v2 script that works correctly. The opportunity is not to rebuild it but to systematically close known reliability gaps and then reduce latency. Research across all four domains converges on the same conclusion: the script has a well-designed pipeline (clipboard capture → API call → parse → paste), and improvements should be made in-place without changing structural boundaries. AHK v2 has no module system worth using here, and the script at ~1100 lines is comfortably within single-file range. The codebase already implements the right patterns (regex-first parsing, JSONL logging, URL-protected replacements, model-branched payloads) — the work is hardening and optimizing each stage.

Reliability problems must come before performance work. Three issues currently affect correctness on every invocation: the clipboard is permanently overwritten (users lose clipboard content), the paste can intermittently use stale clipboard content due to a race condition, and modifier keys can stick after the hotkey fires. These are table-stakes reliability issues for any clipboard-based tool. Alongside these, security-critical issues exist (hardcoded API key, full text logged to disk) that should be resolved in the same phase. Only after reliability is solid does the performance optimization work — persistent WinHTTP COM object, Predicted Outputs for gpt-4.1 via Chat Completions — become the right priority.

The single highest-risk decision in the roadmap is the diff-based structured output approach (returning only correction pairs instead of full rewritten text). It offers the largest theoretical latency reduction but has untested quality implications — the model must identify corrections as structured pairs rather than simply rewriting text. This should be deferred until the foundation is stable and can be tested in isolation. All other improvements are either well-documented patterns with verified implementation details or low-risk incremental changes.

---

## Key Findings

### Recommended Stack

The existing stack (WinHttp COM, ADODB.Stream, AHK v2 native string operations) is correct and should be kept. The only addition required is a Chat Completions API path for gpt-4.1 to enable Predicted Outputs — reasoning models (gpt-5.1, gpt-5-mini) stay on the Responses API. No external dependencies should be added.

**Core technologies:**
- **WinHttp.WinHttpRequest.5.1 COM object** — HTTP client — keep, but create once at script startup and reuse; eliminates 50-400ms TLS/COM overhead per invocation
- **OpenAI Responses API** — reasoning model endpoint — keep for gpt-5.1 and gpt-5-mini; `reasoning.effort` is unavailable on Chat Completions
- **OpenAI Chat Completions API** — standard model endpoint — add for gpt-4.1 path only; required to access Predicted Outputs (not available on Responses API as of early 2026)
- **ADODB.Stream** — UTF-8 response decoding — keep but harden cleanup; evaluate whether still necessary once WinHTTP object is persistent
- **Regex-first + Map-based fallback JSON parsing** — keep dual-parser approach; regex is ~10x faster and covers >99% of responses

**Key version constraint:** Predicted Outputs are confirmed Chat Completions-only; the community request to add them to the Responses API is open but unresolved as of Feb 2026. Do not assume this changes.

See `STACK.md` for full optimization priority table and implementation code snippets.

### Expected Features

The features research distinguishes between things users expect from a daily-driver clipboard tool (table stakes) versus differentiators. Several table-stakes items are currently missing.

**Must have (table stakes — currently missing):**
- **Clipboard save/restore** — every invocation permanently overwrites the user's clipboard; `ClipboardAll()` save/restore is a one-function AHK pattern
- **Paste race condition fix** — `A_Clipboard := correctedText` followed immediately by `Send("^v")` fails 5-15% of the time; add `ClipWait(0.5)` before the paste
- **Visual feedback during API call** — user has no indication the hotkey registered; add `ToolTip("Checking...")` immediately on invocation
- **Empty selection handling** — silent 1-second hang when nothing is selected; show `ToolTip("No text selected")` immediately
- **No-change skip paste** — when AI returns identical text, the script still pastes; `text_changed` is already tracked in logData, just gate the paste
- **API key from environment variable** — hardcoded key is a security issue; `EnvGet("OPENAI_API_KEY")` with fallback
- **Debug log level gating** — 15+ DEBUG events per invocation add noise and string allocation overhead; add `debugLogging := false` toggle
- **Retry with backoff on transient failures** — 429/500/502/503 errors cause silent failure; 1 retry with 500ms delay covers the common cases

**Should have (differentiators, not yet implemented):**
- **Predicted Outputs for gpt-4.1** — 3-5x latency reduction via speculative decoding on Chat Completions API
- **Persistent WinHTTP COM object** — eliminates 50-400ms connection overhead; move one line to script startup
- **Error classification with user-friendly messages** — map 401/429/500 to readable tooltips
- **Input size guard** — configurable max (e.g., 50KB) prevents accidental 30-second hangs on huge selections
- **Log retention/cleanup** — archived logs accumulate forever; purge archives older than 30 days
- **Case-insensitive replacements matching** — reduces `replacements.json` variant burden by 60-70%
- **Output validation** — reject AI output that is dramatically longer/shorter than input before pasting

**Defer to v2+:**
- **Diff-based output format** — high complexity, quality untested for grammar (not just spelling) corrections
- **Config file (config.json)** — ARCHITECTURE.md argues against this; model changes are infrequent, file I/O per invocation adds latency; if runtime model switching is ever needed, a hotkey cycle is faster than file parsing
- **Multi-hotkey support** — nice-to-have, not on the critical path
- **Model fallback chain** — retry logic covers most cases; fallback adds complexity
- **Tray icon status indicator** — medium complexity for marginal UX gain over tooltips

**Explicit anti-features (do not build):**
- Real-time / as-you-type checking (fundamentally changes the tool's model)
- GUI / settings window (file-based config is correct for this tool)
- SSE streaming (WinHttp COM object cannot do incremental reads; zero benefit for paste-back workflow)
- Local model fallback (quality gap is large; tool requires internet anyway)

See `FEATURES.md` for full dependency graph and MVP priority ordering.

### Architecture Approach

The monolithic single-file design is correct and should be kept. The optimization strategy is to improve the existing layers without changing structural boundaries. The linear pipeline (clipboard capture → payload build → API call → parse → post-process → paste back → log) is correct. The `#Include` multi-file approach would add startup I/O overhead and variable scoping complexity for no runtime benefit.

**Major components (all in `Universal Spell Checker.ahk`):**

1. **Configuration Layer (lines 1-76)** — model params, API key, prompt text — keep as-is; model switching via `modelModule` variable at line 18 is cleaner than a runtime config file
2. **Clipboard I/O Layer (lines 202-265)** — capture selected text, paste corrected text — refactor: add save/restore, retry on capture failure, `ClipWait` before paste; move HTMLFile COM creation outside the clipboard lock
3. **API Communication (lines 895-929 in hotkey handler)** — HTTP POST to OpenAI — refactor: extract WinHTTP object to global, add retry loop, add re-entry guard
4. **Response Parser (lines 738-791)** — regex primary, Map-based fallback — keep as-is; harden regex to handle both `type-before-text` and `text-before-type` field orderings
5. **Post-Processing Layer (lines 79-200)** — replacements + prompt leak guard — keep; minor enhancement for case-insensitive matching
6. **Logging Layer (lines 333-483)** — JSONL structured logging — keep; add `debugLogging` flag to gate verbose events
7. **Hotkey Handler (lines 800-1102)** — orchestrates the pipeline — refactor: add `KeyWait` for modifier release, re-entry guard, visual feedback, constants extraction

**Key patterns to follow:**
- Static COM object with `try/catch` recreation on failure (Pattern 1 from ARCHITECTURE.md)
- Flag-gated debug logging, not always-on event pushes (Pattern 2)
- Clipboard save/restore sandwich wrapping the entire handler (Pattern 3)
- One fast retry for transient errors; fail visibly for everything else (Pattern 4)

**High-risk modification areas:**
- Hotkey handler timing checkpoints — reordering stages silently breaks log accuracy
- JSON unescape order in `ExtractTextFromResponseRegex` — backslash must be unescaped last; document this constraint with a comment
- Clipboard restoration timing — `Sleep(100-200)` between paste and clipboard restore is required

See `ARCHITECTURE.md` for the full optimization dependency graph and risk area details.

### Critical Pitfalls

1. **Clipboard race condition on paste** — setting `A_Clipboard` and immediately sending `^v` fails 5-15% of the time because the clipboard chain propagation hasn't completed; prevent with `ClipWait(0.5)` before the paste. Electron apps (VS Code, Slack) and clipboard managers make this worse.

2. **OpenClipboard deadlock** — the `GetClipboardText()` function holds the Windows clipboard lock while creating a `ComObject("HTMLFile")`; if HTMLFile initialization takes 50-200ms (common on cold start), every other app on the system freezes during that window. Fix: read raw clipboard data, release the lock, then parse the HTML outside the lock.

3. **WinHTTP COM object recreation** — creating a new COM object per hotkey press discards the TCP connection pool, forcing a full TLS handshake (200-400ms) on every request. This is a constant tax on every invocation, not a scaling issue.

4. **Modifier key sticking** — when `^!u` fires with Ctrl/Alt still physically held, subsequent `Send("^c")` and `Send("^v")` can leave modifier keys stuck; add `KeyWait("Control")` and `KeyWait("Alt")` at handler start; add `Send("{Ctrl Up}{Alt Up}")` in the `finally` block.

5. **JSON unescape ordering in regex parser** — the backslash unescape (`\\` → `\`) must come last; if someone reorders the `StrReplace` calls during "optimization," file paths and code snippets in user text become corrupted. Add a comment block explaining the ordering constraint.

6. **HTMLFile COM using deprecated IE engine** — `ComObject("HTMLFile")` loads `mshtml.dll` (Internet Explorer's rendering engine). Microsoft has committed to patches through ~2029 but the cold-start latency (50-200ms on first invocation) and lock-holding behavior make this worth replacing with a regex-based HTML tag stripper.

7. **Synchronous HTTP re-entry** — the 30-second synchronous HTTP call blocks AHK's entire thread; rapid double-press queues a second invocation that fires immediately after the first, causing double-paste. Add a `static isRunning` re-entry guard.

See `PITFALLS.md` for full pitfall-to-phase mapping, recovery strategies, and the "Looks Done But Isn't" checklist.

---

## Implications for Roadmap

Based on combined research, four phases are suggested. The ordering is driven by the dependency structure in FEATURES.md and the risk areas identified in PITFALLS.md: reliability before performance, and high-risk optimizations last.

### Phase 1: Foundation Fixes (Reliability and Security)

**Rationale:** Several currently-missing table-stakes features affect correctness on every invocation and must come first. These are also prerequisite for meaningful latency benchmarking — you cannot accurately measure API latency savings if paste failures are adding noise.

**Delivers:** A script where clipboard content is never lost, pastes are reliable, users have feedback, the API key is not hardcoded, and logs are not flooded with debug noise.

**Addresses (from FEATURES.md):**
- Clipboard save/restore with paste race condition fix (`ClipWait(0.5)` before paste)
- Visual feedback tooltip during processing
- Empty selection handling
- No-change skip paste
- API key from environment variable
- Debug log level gating
- Constants extraction from magic numbers

**Avoids (from PITFALLS.md):**
- Clipboard race condition (Pitfall 1)
- OpenClipboard deadlock — move HTMLFile creation outside the lock (Pitfall 5)
- Modifier key sticking — add `KeyWait` and modifier cleanup in `finally` (Pitfall 2)
- Synchronous HTTP re-entry — add `static isRunning` guard (Pitfall 9)

**Research flag:** Standard patterns, no phase research needed. All implementation details are verified with code snippets in ARCHITECTURE.md and PITFALLS.md.

---

### Phase 2: Resilience and Error Handling

**Rationale:** Once reliability is solid, the script should handle transient API failures gracefully instead of silently giving up. This phase also addresses the security concern around full-text logging and adds the input size guard that prevents accidental 30-second hangs.

**Delivers:** A script that automatically handles transient API failures, gives users readable error messages, and does not log sensitive text by default.

**Addresses (from FEATURES.md):**
- Retry with backoff (1 retry, 500ms delay for 429/500/502/503 only)
- Error classification with user-friendly tooltip messages
- Input size guard (configurable max, tooltip warning on reject)
- Log retention/cleanup (archive purge policy)
- Output validation (sanity check AI output length before pasting)

**Uses (from STACK.md):**
- Persistent WinHTTP COM object (prerequisite for retry recovery — recreate on COM failure)
- `try/catch` with COM object recreation pattern

**Avoids (from PITFALLS.md):**
- ADODB.Stream COM object leak — harden `finally` cleanup (Pitfall 6)

**Research flag:** Standard patterns. Retry backoff is a well-documented pattern; ARCHITECTURE.md Phase 5 has the full implementation code.

---

### Phase 3: HTTP and Connection Optimization

**Rationale:** With reliability and resilience in place, latency benchmarks are now trustworthy. This phase implements the single highest-impact low-risk optimization (persistent WinHTTP COM object) and the most impactful client-side latency improvement (Predicted Outputs for gpt-4.1).

**Delivers:** Measurable latency reduction — estimated 150-300ms from connection reuse on subsequent requests within a session; estimated 40-70% generation-time reduction for gpt-4.1 via Predicted Outputs.

**Addresses (from FEATURES.md):**
- Persistent WinHTTP COM object (Priority 3 in FEATURES.md)
- Predicted Outputs for gpt-4.1 via Chat Completions API
- Timeout tuning (reduce connect timeout from 5000ms to 3000ms)
- AHK delay settings (`SetKeyDelay(-1)`, `SetControlDelay(-1)`)

**Uses (from STACK.md):**
- Chat Completions API path for gpt-4.1 (`/v1/chat/completions` with `prediction` parameter)
- Responses API path kept unchanged for gpt-5.1 and gpt-5-mini
- `apiUsesReasoning` flag extended to also gate the API endpoint choice

**Avoids (from PITFALLS.md):**
- WinHTTP COM object recreation waste (Pitfall 3)
- Partial response reads preventing connection reuse — always consume full `ResponseText` (Pitfall 3 integration note)

**Research flag:** Needs verification that gpt-4.1 behaves identically on Chat Completions vs Responses API (quality and pricing). The absence of Predicted Outputs from the Responses API is confirmed via community reports (Feb 2026) but is not in official docs — check official docs before implementing.

---

### Phase 4: JSON Parsing and Output Hardening

**Rationale:** The parsing layer has known fragility (regex assumes field ordering, regex parser does not handle surrogate pairs) and the HTMLFile COM replacement belongs here. These are lower-urgency than phases 1-3 but should be addressed before any diff-based output work.

**Delivers:** A parsing layer that handles both JSON field orderings, correctly processes emoji and Unicode outside the BMP, and uses a lightweight regex-based HTML stripper instead of the deprecated IE engine.

**Addresses (from FEATURES.md):**
- Case-insensitive replacements matching (reduces `replacements.json` variant burden)

**Addresses (from PITFALLS.md):**
- Regex JSON fragility — add secondary regex for `text-before-type` ordering (Pitfall 4)
- JSON unescape ordering — add protective comment; optionally refactor to single-pass (Pitfall 8)
- Surrogate pair handling in regex parser — check for high surrogate and combine with following low surrogate (Pitfall 10)
- HTMLFile COM deprecation — replace with regex-based tag stripper outside the clipboard lock (Pitfall 7)

**Research flag:** Standard patterns. The surrogate pair combining formula is in PITFALLS.md. The regex HTML stripper pattern is well-documented in the AHK community.

---

### Phase 5: Diff-Based Output (Deferred, Needs Validation)

**Rationale:** This is the highest theoretical latency win (80-90% output token reduction) but has the highest uncertainty around real-world correction quality. It must be implemented alongside the existing full-text path and validated before becoming the default.

**Delivers:** Optional diff mode that returns only `{original, corrected}` pairs; eliminates need for prompt-leak safeguard and regex parsing fallback when active.

**Addresses (from FEATURES.md):**
- Diff-based output format (deferred in MVP recommendations)

**Uses (from STACK.md):**
- Structured Outputs via `text.format.json_schema` on Responses API
- gpt-4.1 as the primary candidate (best structured output training)
- `StrReplace()` for applying correction pairs to original text

**Avoids (from PITFALLS.md):**
- Prompt injection risk — structured output cannot contain instruction text; eliminates `StripPromptLeak` concern

**Research flag:** Needs phase research before implementation. Quality of structured correction output for grammar (not just spelling) is untested. Multi-word corrections and punctuation changes across word boundaries may not express cleanly as {original, corrected} pairs. Do not remove full-text fallback until diff mode is validated on diverse input.

---

### Phase Ordering Rationale

- **Phases 1 before 2** — clipboard correctness and feedback must exist before resilience adds retry delays; a retrying script with no user feedback looks frozen
- **Phase 2 before 3** — retry logic requires the persistent WinHTTP object for COM recovery; establishing the retry pattern first makes Phase 3 cleaner
- **Phase 3 before 4** — latency benchmarks in Phase 3 depend on clean parsing in Phase 4, but the HTTP-level gains are independent enough to implement first; the HTMLFile replacement in Phase 4 will improve `clipboardCaptured` timing which can be verified against Phase 3's benchmarks
- **Phase 5 last** — diff mode builds on a stable, hardened pipeline; testing quality requires the logging improvements from Phase 1 to be in place

### Research Flags

Phases likely needing deeper research during planning:
- **Phase 3 (Predicted Outputs):** Confirm gpt-4.1 Chat Completions and Responses API parity; confirm Predicted Outputs are still Chat Completions-only at time of implementation
- **Phase 5 (Diff-based output):** Run quality validation testing before implementing as non-default; test multi-word corrections, punctuation changes, and grammar corrections (not just spelling)

Phases with standard patterns (can skip research-phase):
- **Phase 1:** All patterns are well-documented AHK v2 official patterns; implementation code is in ARCHITECTURE.md
- **Phase 2:** Standard HTTP retry patterns; code examples in ARCHITECTURE.md Phase 5
- **Phase 4:** AHK regex and string operations; well-documented Unicode patterns

---

## Confidence Assessment

| Area | Confidence | Notes |
|------|------------|-------|
| Stack | HIGH | All core techniques verified via Microsoft WinHTTP docs, OpenAI official docs, and AHK v2 official docs; Predicted Outputs Chat Completions-only confirmed via community (Feb 2026) |
| Features | HIGH | Table-stakes features identified from current codebase analysis; differentiators cross-referenced with OpenAI official docs |
| Architecture | HIGH | Single-file recommendation backed by AHK v2 module system limitations; refactoring phases supported by official AHK patterns and community-verified COM patterns |
| Pitfalls | HIGH | 10 pitfalls verified against AHK v2 docs, Microsoft WinHTTP docs, codebase analysis, and community incident reports; all have code-level prevention strategies |

**Overall confidence:** HIGH

### Gaps to Address

- **Predicted Outputs Chat Completions-only status:** Confirmed via community report (Feb 2026) but not in official OpenAI docs as a stated limitation. Verify at implementation time before building the dual-endpoint path.
- **gpt-4.1 Chat Completions vs Responses API parity:** Research does not include a direct comparison of output quality or pricing when gpt-4.1 is called on both endpoints. Verify before switching the gpt-4.1 path.
- **Diff-based output quality for grammar corrections:** No real-world testing of whether the model reliably produces correct `{original, corrected}` pairs for grammatical rewrites (as opposed to single-word spelling corrections). Validate with a test suite before Phase 5.
- **WinHTTP COM object recovery under network failure:** What happens if the persistent COM object encounters a fatal state (e.g., network adapter toggled)? The `try/catch` recreation pattern is specified but not tested end-to-end.
- **ADODB.Stream necessity:** Research notes this may no longer be needed if WinHTTP UTF-8 handling is correct; verify during Phase 3 implementation whether `ResponseText` can replace `GetUtf8Response()` for smart quotes and em-dashes.

---

## Sources

### Primary (HIGH confidence)
- [WinHttpRequest COM object reference (Microsoft)](https://learn.microsoft.com/en-us/windows/win32/winhttp/winhttprequest) — COM object reuse, connection pooling behavior
- [AHK v2 Performance docs (official)](https://www.autohotkey.com/docs/v2/misc/Performance.htm) — SetKeyDelay, SetControlDelay, v2-specific optimization guidance
- [AHK v2 ClipboardAll documentation (official)](https://www.autohotkey.com/docs/v2/lib/ClipboardAll.htm) — clipboard save/restore pattern
- [AHK v2 ClipWait documentation (official)](https://www.autohotkey.com/docs/v2/lib/ClipWait.htm) — clipboard timing behavior
- [OpenAI Predicted Outputs guide (official)](https://developers.openai.com/api/docs/guides/predicted-outputs) — supported models, Chat Completions requirement
- [OpenAI Latency Optimization guide (official)](https://platform.openai.com/docs/guides/latency-optimization) — prompt caching, diff output, streaming
- [OpenAI Structured Outputs guide (official)](https://developers.openai.com/api/docs/guides/structured-outputs) — `text.format.json_schema` for Responses API
- [GPT-4.1 Prompting Guide (official cookbook)](https://developers.openai.com/cookbook/examples/gpt4-1_prompting_guide) — diff-format output recommendations
- [OpenAI Prompt Caching guide (official)](https://developers.openai.com/api/docs/guides/prompt-caching) — 1024-token minimum, 50% cost discount
- [AHK v2 ChangeLog (official)](https://www.autohotkey.com/docs/v2/ChangeLog.htm) — modifier key race condition fixes

### Secondary (MEDIUM confidence)
- [OpenAI community: Predicted Outputs not in Responses API (Feb 2026)](https://community.openai.com/t/predicted-outputs-in-response-api/1373125) — Chat Completions-only confirmation
- [OpenAI community: Malformed JSON responses (2025)](https://community.openai.com/t/chat-completion-responses-suddenly-returning-malformed-or-inconsistent-json/1368077) — JSON field ordering fragility
- [AHK community: Faster Multiple WinHttp requests](https://www.autohotkey.com/boards/viewtopic.php?t=91213) — COM object reuse pattern
- [AHK community: Optimizing AHKv2 Code for Speed](https://www.autohotkey.com/boards/viewtopic.php?style=19&t=124617) — v2-specific performance tips
- [AHK community: Clipboard v2 Issues](https://www.autohotkey.com/boards/viewtopic.php?t=135810) — clipboard race conditions
- [AHK community: Stop modifier keys from getting stuck](https://www.autohotkey.com/boards/viewtopic.php?t=81667) — KeyWait patterns
- [AHK community: HTMLFile COM alternatives](https://www.autohotkey.com/boards/viewtopic.php?t=126938) — IE engine deprecation concerns
- [AHK community: WinHttp COM Object Reuse](https://www.autohotkey.com/boards/viewtopic.php?t=128029) — static variable pattern
- [Persistent HTTPS Connections latency reduction (Medium)](https://medium.com/@techmsy/persistent-https-connections-reduce-api-call-time-by-50-3ca23723b336) — 50% latency reduction measured
- [WinHTTP keep-alive and connection pooling (Microsoft public group)](https://microsoft.public.winhttp.narkive.com/9NM27MTE/keep-alive) — keep-alive behavior details

### Tertiary (LOW confidence)
- [Best AI Grammar Checkers 2026](https://max-productive.ai/ai-tools/grammar-checkers/) — market context only; no technical findings used

---

*Research completed: 2026-03-27*
*Ready for roadmap: yes*
