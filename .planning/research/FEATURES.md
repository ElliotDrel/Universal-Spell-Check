# Feature Landscape

**Domain:** AI-powered hotkey-driven spell checking / text correction tool (Windows, AutoHotkey v2)
**Researched:** 2026-03-27

## Table Stakes

Features users of a daily-driver spell checking tool expect. Missing any of these = the tool feels unreliable or incomplete for power-user workflows.

### Performance & Latency

| Feature | Why Expected | Complexity | Notes |
|---------|--------------|------------|-------|
| Clipboard save/restore | Users lose whatever was on their clipboard every time they spell check; every serious clipboard-using tool saves and restores `ClipboardAll` | Low | AHK v2 pattern: `ClipSaved := ClipboardAll()` before clearing, restore after paste with a DllCall busy-wait to avoid the async paste race condition. Currently NOT implemented -- the tool overwrites clipboard permanently. |
| Input size guard | Attempting to send 500KB+ of text to OpenAI wastes money and hangs the UI for 30s; user gets no feedback why | Low | Add a configurable max (e.g. 50KB) with a tooltip warning. Logs should record rejections. |
| Visual feedback during processing | User presses Ctrl+Alt+U and sees nothing until text appears (or an error tooltip 5s later); no way to know if it is working | Low | Show a tooltip/tray notification immediately: "Spell checking..." then clear on completion. Extremely low cost, high UX impact. |
| Retry with backoff on transient failures | API 429 (rate limit) and 5xx errors are transient; currently the tool shows a tooltip and gives up | Medium | Implement 1-2 automatic retries with exponential backoff (1s, 2s) for status codes 429, 500, 502, 503. Do NOT retry 400/401/404. Log each retry attempt. |
| API key from environment variable | Hardcoded API key in source is a security critical issue already flagged in CONCERNS.md; any user of this tool needs a non-hardcoded path | Low | `apiKey := EnvGet("OPENAI_API_KEY")` with fallback to hardcoded for migration period. One-line change. |
| Error classification and user messaging | Current error handling shows raw HTTP status codes; users need human-readable messages like "Rate limited, retrying..." or "Invalid API key" | Low | Map common status codes (401, 429, 500) to user-friendly tooltip messages. |
| Debug log level gating | 15+ DEBUG event log entries per invocation clutter logs and add noise; identified in CONCERNS.md | Low | Add a `debugLogging := false` toggle at top of script. Gate all `"DEBUG: ..."` events behind it. Default OFF. |

### Reliability & Correctness

| Feature | Why Expected | Complexity | Notes |
|---------|--------------|------------|-------|
| No-change detection (skip paste) | When AI returns identical text (nothing to fix), the tool still overwrites the selection via Ctrl+V; this can disrupt cursor position and undo history in some apps | Low | Already tracks `textChanged` in logData. Just gate the paste operation: `if (!logData.textChanged) { skip paste, show "No changes needed" tooltip }`. |
| Clipboard paste race condition fix | AHK's `Send("^v")` is async -- if something modifies the clipboard between setting it and the paste executing, wrong text gets pasted. Known AHK community issue. | Low-Medium | After `Send("^v")`, use `DllCall("user32\GetOpenClipboardWindow", "Ptr")` busy-wait loop (with timeout) before restoring clipboard. The pattern is well-documented in AHK v2 community. |
| Graceful handling of empty selection | If user presses Ctrl+Alt+U with nothing selected, script waits 1s (ClipWait timeout) then silently logs an error; no user feedback | Low | Show tooltip "No text selected" immediately after ClipWait fails. Already have the timeout logic, just add UX feedback. |
| Robust prompt leak detection | Current `StripPromptLeak` only catches exact string matches; documented as incomplete in CONCERNS.md | Medium | Add secondary check: if output starts with "instructions:" (case-insensitive prefix match), strip everything up to "text input:" label. This catches reformatted/paraphrased leaks without adding AI overhead. |

### Configuration & Maintenance

| Feature | Why Expected | Complexity | Notes |
|---------|--------------|------------|-------|
| Model selection without code editing | Changing model requires editing line 18 of the script and restarting; identified in CONCERNS.md | Medium | Load from `config.json` (hot-reloaded like replacements.json) or support command-line arg `--model gpt-5.1`. Config file is more consistent with existing patterns. |
| Log retention / cleanup | Archive files accumulate forever with no cleanup; identified in CONCERNS.md as scaling concern | Low | Add max archive count (e.g. keep last 10) or max age (30 days). Check during existing `RotateLogIfNeeded()` call. |
| Constants extracted from magic numbers | Timeouts (5000, 30000), max log size (1000000), ClipWait timeout (1) are all hardcoded throughout the script | Low | Move to a constants block at the top of the script. No behavior change, just maintainability. |

## Differentiators

Features that set this tool apart from basic spell check utilities. Not expected, but create competitive advantage for a power-user daily-driver tool.

### Performance Optimizations

| Feature | Value Proposition | Complexity | Notes |
|---------|-------------------|------------|-------|
| Predicted Outputs (speculative decoding) | 3-5x latency reduction for spell check. Send original text as `prediction` param; since most output tokens match input, speculative decoding skips sequential generation. Ideal for spell check where 95%+ of text is unchanged. | Medium | **Caveat:** Only works with Chat Completions API (`/v1/chat/completions`), NOT the Responses API (`/v1/responses`) currently used. Would require a dual-endpoint approach: use Chat Completions + predicted outputs for gpt-4.1 (standard model), keep Responses API for reasoning models (gpt-5.1, gpt-5-mini) which don't support predicted outputs anyway. GPT-4.1 supports both APIs. Confidence: HIGH (verified via OpenAI docs -- Responses API community request from Feb 2026 confirms not yet ported). |
| Diff-based output format | Reduce output tokens by 80-90%. Instead of returning full corrected text, return only `[{"old":"teh","new":"the"}]` patches. Fewer output tokens = proportionally lower latency (output generation is the slowest step). | High | Requires prompt engineering change, structured output schema, and client-side patch application logic. Risk: model accuracy may decrease on patch format vs full-text rewrite. Would need extensive testing. The Scratchpad already identifies this as a potential optimization (250-350ms target). |
| Prompt caching optimization | OpenAI automatically caches prompt prefixes >= 1024 tokens. Structure prompt so system instructions come first (static) and user text comes last (variable). | Low | Currently the prompt is a single string `"instructions: ... text input: ..."`. Restructuring to use a separate system message would enable automatic prompt caching on the instruction prefix. However, the current prompt is likely under 1024 tokens total, so caching benefit may be minimal. Most impactful for longer texts. |
| WinHTTP connection reuse awareness | WinHTTP already pools persistent connections automatically (keep-alive is default, connections reused within ~2 min idle window). Currently creates a new COM object per invocation. | Low | Store the WinHTTP COM object as a persistent global instead of recreating it each invocation. WinHTTP handles connection pooling internally, but reusing the COM object avoids COM initialization overhead. Read all response data before next request to ensure pooling works. |

### Quality of Life

| Feature | Value Proposition | Complexity | Notes |
|---------|-------------------|------------|-------|
| Tray icon status indicator | System tray icon changes color/icon during processing (idle = green, working = yellow, error = red). Provides ambient awareness without tooltips. | Medium | AHK v2 supports `TraySetIcon()`. Would need 3 icon states. More polished than tooltip-only feedback. |
| Sound/haptic feedback on completion | Subtle system sound when spell check completes. Users working across monitors may not see tooltips. | Low | `SoundBeep(750, 100)` or play a short .wav. Make configurable (on/off). |
| Case-insensitive replacements matching | Currently requires explicit variants for every casing permutation (e.g., 7 entries for "Night Shift"). Case-insensitive matching with canonical casing output would reduce variant count by 60-70%. | Medium | Scratchpad already identifies this. Implementation: match case-insensitively, but always replace with the canonical form. Need to preserve original casing context (e.g., if variant is ALL CAPS at start of sentence). |
| Config file with hot-reload | Single `config.json` for model, API key path, debug level, max input size, timeouts, sound feedback, etc. Hot-reloaded each invocation (same pattern as replacements.json). | Medium | Unifies all the scattered configuration. Natural extension of the existing hot-reload pattern. Foundation for other configurability improvements. |
| Hotkey customization | Allow user to change the trigger hotkey without editing source code. | Low-Medium | Read from config file, use `Hotkey()` function to register dynamically. AHK v2 supports runtime hotkey registration. |
| Multi-hotkey support (different modes) | Second hotkey for "fix and simplify" or "fix grammar only, no style changes" or "make more formal." Different prompts for different contexts. | Medium | Add prompt variants to config. Each hotkey maps to a prompt template. Reuses the same API/parsing infrastructure. |
| Stale log viewer warning | The Scratchpad requests that the HTML log viewer detects when data may be outdated and prompts the user to regenerate. | Low | Add a timestamp to the generated HTML. On load, compare to current time. Show a banner if > 1 hour stale. JavaScript-only change in `generate_log_viewer.py`. |

### Reliability Enhancements

| Feature | Value Proposition | Complexity | Notes |
|---------|-------------------|------------|-------|
| Model fallback chain | If primary model (e.g., gpt-5.1) fails or times out, automatically retry with a faster/cheaper model (e.g., gpt-4.1-mini). Ensures correction always completes. | Medium | Configure fallback order in config. On timeout or 5xx after retries exhausted, try next model. Log which model actually served the request. |
| Output validation | Sanity-check AI output before pasting: reject if output is dramatically longer/shorter than input (e.g., >2x or <0.5x length), contains code/markdown when input was plaintext, or is empty. | Low-Medium | Simple heuristic checks. Prevents catastrophic model failures from pasting garbage into user's document. |
| Timeout tuning per model | Reasoning models (gpt-5.1) may need longer timeouts than standard models (gpt-4.1). Currently hardcoded to 30s for all. | Low | Add per-model timeout overrides in model configuration. gpt-4.1 could use 15s, reasoning models 45s. |

## Anti-Features

Features to explicitly NOT build. These would compromise the tool's core value of speed and simplicity.

| Anti-Feature | Why Avoid | What to Do Instead |
|--------------|-----------|-------------------|
| Real-time / as-you-type checking | Fundamentally changes the tool from "instant correction on demand" to a background monitoring service. Adds complexity (text change detection, cursor tracking, per-app integration), latency (continuous API calls), and cost. Grammarly does this with a massive engineering team; it is not viable for a single AHK script. | Keep the on-demand hotkey model. Speed is the product -- "select, hotkey, done." |
| GUI / settings window | Adding a GUI contradicts "zero UI overhead." Configuration should be file-based (config.json) and hot-reloaded. A settings window adds visual weight, maintenance burden, and startup time. | Use config.json for all settings. Users edit a text file. |
| Browser extension / per-app plugin | Would require maintaining separate codebases per platform. The AHK global hotkey already works in every Windows app universally. | Keep using the universal hotkey approach. Per-app paste method config handles edge cases. |
| Tone detection / style suggestions | Scope creep beyond spell/grammar checking. Adds output complexity, increases token usage, and slows response time. The prompt is intentionally minimal: "Fix grammar and spelling... Do not add or remove any content." | Keep the prompt minimal. Users who want style guidance should use Grammarly or a dedicated writing tool. |
| Plagiarism checking | Completely different domain. Requires comparing against massive text databases. Not related to the core spell check mission. | Out of scope. Use dedicated plagiarism tools. |
| Multi-language support | Adds prompt complexity, increases potential for incorrect corrections, and most AI models handle English best. The user's workflow is English-focused. | Keep English-only. The AI model handles incidental non-English text gracefully without special configuration. |
| Offline / local model fallback | Local models (Ollama, etc.) require significant setup, disk space, and introduce a second code path for model interaction. Quality is significantly lower than cloud models for grammar correction. The Scratchpad mentions investigating this, but the ROI is poor for a tool that requires internet anyway (the user is typically online when writing). | Defer indefinitely. Show a clear "No internet / API unavailable" error instead. Revisit only if API reliability becomes a real problem. |
| Undo history stack | Maintaining a clipboard undo stack adds memory usage and complexity. Windows apps already have Ctrl+Z which works after clipboard paste. | Rely on the target application's native undo (Ctrl+Z). Ensure clipboard save/restore does not interfere with undo behavior. |
| Diff visualization UI | The Scratchpad mentions a diff UI (similar to "Wiser Flow" bar). While appealing, this adds a GUI element, introduces latency (user must review before applying), and breaks the "instant" philosophy. | Log changes to the JSONL log. Users can review changes post-hoc in the log viewer. If a preview is ever needed, make it opt-in via a separate hotkey (e.g., Ctrl+Alt+Shift+U for "preview mode"). |
| Windows native app rewrite | The Scratchpad mentions replacing AHK with a native Windows app. This is a complete rewrite, not an improvement. The AHK script works well and is easy to modify. A native app would require build tooling, installers, and significantly more maintenance. | Continue iterating on the AHK script. The only advantage of a native app (persistent HTTP connections) can be partially achieved by keeping the WinHTTP COM object alive between invocations. |

## Feature Dependencies

```
API key from env var         (independent, do first)
    |
Config file (config.json)    (enables model selection, hotkey config, timeouts)
    |
    +-- Model selection without code editing
    +-- Hotkey customization
    +-- Multi-hotkey support (different prompts)
    +-- Timeout tuning per model
    +-- Model fallback chain
    |
Debug log level gating       (independent)
    |
Constants extraction         (independent, improves all other work)
    |
Visual feedback (tooltip)    (independent, do early)
    |
Clipboard save/restore  -->  Paste race condition fix  -->  No-change skip paste
    |
Input size guard             (independent)
    |
Retry with backoff      -->  Error classification  -->  Model fallback chain
    |
Predicted Outputs            (requires Chat Completions API path for gpt-4.1)
    |
Diff-based output            (independent but HIGH complexity, defer)
    |
Case-insensitive replacements (independent)
    |
Log retention/cleanup        (independent)
    |
Output validation            (independent, but most valuable after retry logic)
```

## MVP Improvement Recommendations

**Priority 1 -- Immediate reliability and UX wins (Low complexity, high impact):**
1. Visual feedback tooltip during processing
2. Clipboard save/restore with paste race condition handling
3. No-change detection (skip paste when text unchanged)
4. API key from environment variable
5. Graceful handling of empty selection
6. Debug log level gating
7. Constants extraction

**Priority 2 -- Resilience and configurability:**
8. Retry with backoff on transient API failures
9. Error classification with user-friendly messages
10. Config file with hot-reload (model, timeouts, debug level)
11. Input size guard
12. Log retention/cleanup

**Priority 3 -- Performance differentiation:**
13. Predicted Outputs via Chat Completions API for gpt-4.1 (3-5x latency reduction)
14. WinHTTP COM object persistence
15. Case-insensitive replacements
16. Output validation

**Defer:**
- Diff-based output format (high complexity, needs extensive testing)
- Multi-hotkey support (nice-to-have, not critical)
- Model fallback chain (useful but adds complexity; retry logic covers most failures)
- Stale log viewer warning (trivial but not on the critical path)

## Sources

- [OpenAI Predicted Outputs documentation](https://developers.openai.com/api/docs/guides/predicted-outputs) -- HIGH confidence, official docs
- [OpenAI Latency Optimization guide](https://developers.openai.com/api/docs/guides/latency-optimization) -- HIGH confidence, official docs
- [OpenAI Prompt Caching documentation](https://platform.openai.com/docs/guides/prompt-caching) -- HIGH confidence, official docs
- [Predicted Outputs in Responses API community request](https://community.openai.com/t/predicted-outputs-in-response-api/1373125) -- MEDIUM confidence, confirms feature gap as of Feb 2026
- [AutoHotkey v2 ClipboardAll documentation](https://www.autohotkey.com/docs/v2/lib/ClipboardAll.htm) -- HIGH confidence, official docs
- [AHK paste-restore clipboard race condition](https://tdalon.blogspot.com/2021/04/ahk-paste-restore-clipboard-pitfall.html) -- MEDIUM confidence, community-verified pattern
- [AHK v2 clipboard save/restore community thread](https://www.autohotkey.com/boards/viewtopic.php?t=132558) -- MEDIUM confidence
- [WinHTTP keep-alive and connection pooling](https://microsoft.public.winhttp.narkive.com/9NM27MTE/keep-alive) -- MEDIUM confidence, Microsoft public group
- [AI agent retry strategies and graceful degradation](https://getathenic.com/blog/ai-agent-retry-strategies-exponential-backoff) -- MEDIUM confidence
- [LLM token optimization strategies](https://redis.io/blog/llm-token-optimization-speed-up-apps/) -- MEDIUM confidence
- [OpenAI Apply Patch tool](https://developers.openai.com/api/docs/guides/tools-apply-patch) -- HIGH confidence, official docs
- [Grammarly features overview](https://www.grammarly.com/features) -- MEDIUM confidence, competitor reference
- [Best AI Grammar Checkers 2026](https://max-productive.ai/ai-tools/grammar-checkers/) -- LOW confidence, market overview
