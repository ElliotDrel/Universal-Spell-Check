# Technology Stack: Optimization Techniques

**Project:** Universal Spell Checker (AHK v2 + OpenAI API)
**Researched:** 2026-03-27
**Focus:** Performance optimization of existing AHK v2 spell checker

## Recommended Optimizations

### 1. Persistent WinHttp COM Object (Connection Reuse)

| Technique | Details | Why |
|-----------|---------|-----|
| Global/static WinHttp COM object | Create once at script startup, reuse across hotkey invocations | Eliminates COM object creation + TLS handshake overhead per request |
| Keep-alive (automatic) | WinHttp uses HTTP/1.1 keep-alive by default; connections pool for ~2 minutes idle | TCP connection reuse is automatic if the COM object is kept alive |

**Confidence:** HIGH (verified via Microsoft WinHttp documentation and AHK community)

**Current problem:** The script creates a new `ComObject("WinHttp.WinHttpRequest.5.1")` on every Ctrl+Alt+U press (line 895). This means every invocation pays:
1. COM object instantiation cost
2. DNS resolution (usually cached by OS, but still a lookup)
3. TCP connection establishment (~1 RTT)
4. TLS handshake (~2 RTTs for TLS 1.2, ~1 RTT for TLS 1.3)

**Implementation:** Move the COM object to a global variable initialized once:

```ahk
; At script startup (auto-execute section)
global httpObj := ComObject("WinHttp.WinHttpRequest.5.1")

; In the hotkey handler, reuse it:
^!u:: {
    ; httpObj is readable without `global` declaration since we only call methods
    httpObj.Open("POST", apiUrl, false)
    httpObj.SetRequestHeader("Content-Type", "application/json; charset=utf-8")
    httpObj.SetRequestHeader("Authorization", "Bearer " . apiKey)
    httpObj.Send(jsonPayload)
    ; ...
}
```

**Key AHK v2 scoping rule:** Calling methods on a global COM object from inside a hotkey does NOT require a `global` declaration -- only direct reassignment of the variable does. Since we only call `.Open()`, `.SetRequestHeader()`, `.Send()`, etc., no `global` keyword is needed inside the hotkey.

**Savings estimate:** 50-150ms on first call after idle (TLS handshake), 10-30ms on subsequent calls within the 2-minute keep-alive window. WinHttp's connection pool automatically reuses TCP connections to the same server.

**Caveat:** WinHttp considers idle sockets "expired" after ~2 minutes. If the user hasn't spell-checked in >2 minutes, the next call will re-establish the connection. This is handled transparently -- no error handling needed. The COM object itself persists; only the underlying TCP socket expires.

**What NOT to do:**
- Do NOT try to detect connection state or manually manage keepalive -- WinHttp handles this internally
- Do NOT use `http.SetRequestHeader("Connection", "Keep-Alive")` -- this is redundant since HTTP/1.1 defaults to keep-alive and WinHttp has no way to disable it
- Do NOT create the COM object inside a function with `static` if the hotkey handler needs it -- use a script-level global for simplicity in this architecture

---

### 2. Prompt Caching (Automatic, Free)

| Technique | Details | Why |
|-----------|---------|-----|
| Structure prompt with static prefix first | Place instruction text at the beginning, variable user text at the end | OpenAI's automatic prompt caching caches longest matching prefix |
| Minimum 1024 tokens for caching | Short prompts (under 1024 tokens) will never cache | Most spell-check prompts are short, so this has limited applicability |

**Confidence:** HIGH (verified via OpenAI official documentation)

**How it works:** OpenAI automatically caches prompt prefixes >= 1024 tokens. Cached tokens get 50% cost discount and up to 80% latency reduction. No code changes needed -- it just happens. The `store: true` parameter (already in the script) is unrelated; it controls 30-day data retention for evals/logging, NOT prompt caching.

**Current prompt structure is already correct:**
```
instructions: Fix the grammar and spelling...  (static prefix)
text input: [user's text]                      (variable suffix)
```

**Limitation for this project:** The instruction prefix is only ~40 tokens. The user's text varies. Total prompt is typically 50-250 tokens -- well below the 1024-token minimum for caching. Prompt caching will only help when the user spell-checks a very long document (1000+ words).

**Recommendation:** Keep the current structure (static prefix first, variable suffix last). This is already optimally structured for caching. No code change needed. The benefit will be marginal for typical use but significant for long-form text.

**What NOT to do:**
- Do NOT confuse `store: true` with prompt caching -- they serve different purposes
- Do NOT try to pad the prompt to reach 1024 tokens -- this wastes money and adds latency

---

### 3. Diff-Based Output (Reduce Output Tokens)

| Technique | Details | Why |
|-----------|---------|-----|
| Return only corrections, not full text | Ask the model to output JSON array of {original, corrected} pairs | Reduces output tokens from N (full text) to ~10-30 (just diffs) |
| Structured Outputs via `text.format` | Use JSON schema to enforce correction format | Guarantees parseable output; no regex fallback needed |

**Confidence:** MEDIUM (concept verified via OpenAI docs and GPT-4.1 prompting guide; not tested for spell-check quality)

**The biggest latency win available.** Output token generation is the primary driver of API latency (each token is generated sequentially). A 200-word text with 3 typos currently generates ~200 output tokens. With diff output, the same corrections would be ~20-30 tokens -- a 5-10x reduction in output tokens, translating to roughly 50-70% latency reduction on the API call itself.

**Structured Outputs format for Responses API:**

```json
{
  "model": "gpt-4.1",
  "input": [{"role": "user", "content": [{"type": "input_text", "text": "..."}]}],
  "store": true,
  "text": {
    "format": {
      "type": "json_schema",
      "name": "spell_corrections",
      "strict": true,
      "schema": {
        "type": "object",
        "properties": {
          "corrections": {
            "type": "array",
            "items": {
              "type": "object",
              "properties": {
                "original": { "type": "string" },
                "corrected": { "type": "string" }
              },
              "required": ["original", "corrected"],
              "additionalProperties": false
            }
          }
        },
        "required": ["corrections"],
        "additionalProperties": false
      }
    }
  }
}
```

**Application logic in AHK:** After receiving the corrections array, apply each `{original, corrected}` pair to the original text using `StrReplace()`. This is simpler and more reliable than regex parsing of free-form corrected text.

**Model compatibility notes:**
- **gpt-4.1**: Full structured output support via `text.format`. GPT-4.1 was specifically trained for diff-format output. Best candidate for this approach.
- **gpt-5.1**: Supports structured outputs via `text.format` in the Responses API. However, as a reasoning model, it may add latency from reasoning overhead even with `effort: "none"`.
- **gpt-5-mini**: Same as gpt-5.1 regarding structured output support.

**Trade-offs:**
- PRO: Massive latency reduction (fewer output tokens)
- PRO: Eliminates need for regex JSON parsing fallback (schema-guaranteed output)
- PRO: Eliminates need for prompt-leak safeguard (structured output cannot contain instruction text)
- CON: First request with a new schema incurs extra latency (~1-2s) for schema compilation; subsequent requests are normal
- CON: Model must correctly identify misspellings AND provide correct replacements as structured pairs -- more complex task than simple text rewrite
- CON: Position-based corrections (line/char offset) are unreliable from LLMs; simple find-replace is more robust
- CON: Edge cases: if the model hallucinates a correction that doesn't exist in the original text, `StrReplace()` becomes a no-op (safe failure mode)
- CON: Multi-word corrections spanning phrase boundaries may be harder for the model to express as simple pairs

**Recommendation:** Implement as an optional mode alongside the current full-text approach. Test quality before making it the default. Start with gpt-4.1 which has the best structured output training.

**What NOT to do:**
- Do NOT include line numbers or character offsets in the schema -- LLMs are unreliable at counting positions
- Do NOT use the V4A diff/patch format (designed for code files with context lines, overkill for spell check)
- Do NOT remove the full-text fallback until diff mode is proven reliable across diverse input types

---

### 4. Predicted Outputs (Chat Completions Only)

| Technique | Details | Why NOT |
|-----------|---------|---------|
| Supply original text as prediction | Model accelerates output generation for matching tokens | NOT available in Responses API -- only Chat Completions |

**Confidence:** HIGH (verified via OpenAI docs and community confirmation as of Feb 2026)

**This is the ideal technique for spell-checking** -- you supply the original text as the "prediction" and the model only needs to generate the differing tokens, dramatically reducing latency. However, **Predicted Outputs are currently only supported in the Chat Completions API (`/v1/chat/completions`)**, not in the Responses API (`/v1/responses`).

**Supported models:** GPT-4o, GPT-4o-mini, GPT-4.1, GPT-4.1-mini, GPT-4.1-nano.

**The dilemma:** The current script uses the Responses API because reasoning models (gpt-5.1, gpt-5-mini) require it. Predicted Outputs would require:
1. Switching back to Chat Completions for gpt-4.1 (the only model that benefits from predictions)
2. Maintaining dual API paths (Chat Completions for gpt-4.1 + Responses for reasoning models)

**If willing to add a Chat Completions path for gpt-4.1:**

```json
{
  "model": "gpt-4.1",
  "messages": [
    {"role": "system", "content": "Fix grammar and spelling. Preserve formatting. Return only corrected text."},
    {"role": "user", "content": "the user's original text here"}
  ],
  "prediction": {
    "type": "content",
    "content": "the user's original text here"
  },
  "temperature": 0.3
}
```

**Savings estimate:** For a 200-word text with 3 typos, ~95% of tokens are "accepted predictions" that the model confirms rather than generates from scratch. Latency reduction of 40-70% on the generation phase.

**Recommendation:** Strongly consider implementing a dual-path approach where gpt-4.1 uses Chat Completions + Predicted Outputs, while reasoning models continue using the Responses API. This is the single biggest latency optimization available for the gpt-4.1 path.

**What NOT to do:**
- Do NOT try to use `prediction` in the Responses API -- it will be ignored or error
- Do NOT use Predicted Outputs with reasoning models -- they are not supported

---

### 5. Streaming (Perceived Latency Only)

| Technique | Details | Why NOT (for now) |
|-----------|---------|-------------------|
| SSE streaming with `stream: true` | Receive tokens as they're generated | WinHttpRequest.5.1 COM object does not support incremental SSE reading |

**Confidence:** HIGH (verified limitation of WinHttpRequest COM object)

**The problem:** The `WinHttp.WinHttpRequest.5.1` COM object is fundamentally request/response oriented. It buffers the entire response before making it available via `ResponseText`. There is no way to incrementally read SSE events as they arrive using the COM object.

**Workarounds exist but add significant complexity:**
1. **DllCall the WinHTTP C API directly** (`WinHttpQueryDataAvailable` + `WinHttpReadData` in a loop) -- works but requires 100+ lines of DllCall boilerplate and careful memory management
2. **libcurl wrapper (LibQurl for AHK v2)** -- requires external DLL dependencies (libcurl, openssl), breaking the "self-contained script" constraint
3. **cURL via Run** -- shell out to curl.exe with streaming output piped to a file, then read the file -- hacky and adds process-spawn latency

**For spell-checking, streaming doesn't actually help** because you need the complete corrected text before pasting it back. You can't paste partial corrections. The benefit of streaming is purely perceived latency (showing a progress indicator), not actual task completion speed.

**Recommendation:** Do NOT implement streaming. The complexity cost is extremely high in AHK and the benefit is zero for the paste-back use case. If a progress indicator is desired, use a simple ToolTip("Checking...") before the request and clear it after.

**What NOT to do:**
- Do NOT attempt to implement SSE parsing in AHK via the COM object -- it fundamentally doesn't work
- Do NOT add libcurl as a dependency just for streaming -- it breaks the simplicity constraint

---

### 6. AHK v2 Script-Level Optimizations

| Technique | Confidence | Impact |
|-----------|------------|--------|
| `SetKeyDelay(-1)` and `SetControlDelay(-1)` | HIGH | Eliminates artificial delays in SendInput fallback |
| `ProcessSetPriority("A")` (Above Normal) | MEDIUM | Gives script priority over normal apps; marginal benefit |
| Avoid chaining expressions with commas | HIGH | AHK v2 is 50%+ slower with comma-chained variables vs. separate lines |
| Cache frequently accessed values in local variables | MEDIUM | Reduces memory lookups in hot loops |
| Use `SendMode("Input")` | HIGH | Already implied by default in v2 but worth making explicit |

**Confidence:** HIGH (verified via official AHK v2 performance docs and community benchmarks)

**AHK v2 defaults are already fast:** Unlike v1, AHK v2 defaults to `SetBatchLines -1` equivalent (no artificial sleeping between lines). The main script performance is dominated by the API call (1-3 seconds), so AHK-level optimizations yield microseconds -- negligible in practice.

**The one meaningful AHK optimization:** `SetKeyDelay(-1)` and `SetControlDelay(-1)` at script startup can slightly reduce paste latency by eliminating artificial delays in keyboard/control operations. Worth adding as a one-liner.

```ahk
; Add to script startup
SetKeyDelay(-1)
SetControlDelay(-1)
```

**What NOT to do:**
- Do NOT use `ProcessSetPriority("Realtime")` -- can hang the system if script misbehaves
- Do NOT chain variable assignments with commas in v2 -- this is SLOWER than separate lines (opposite of v1)
- Do NOT optimize the regex parser or JSON handling further -- they already run in microseconds; the API call is 99.9% of total latency

---

### 7. Timeout Tuning

| Technique | Details | Why |
|-----------|---------|-----|
| Reduce connect timeout | Change from 5000ms to 3000ms | Fail faster on network issues; OpenAI's servers respond to TCP connect within 100ms |
| Keep send/receive timeouts at 30s | API response time varies with input length | Reasoning models can take 2-5s; keep generous timeouts |

**Confidence:** MEDIUM

**Current timeouts:** `http.SetTimeouts(5000, 5000, 30000, 30000)` -- this is (resolve, connect, send, receive) in milliseconds.

**Recommendation:**
```ahk
; Tighter connect timeout, generous receive timeout
httpObj.SetTimeouts(3000, 3000, 30000, 30000)
```

OpenAI's API servers resolve and accept TCP connections quickly. A 3-second connect timeout is generous enough for any reasonable network condition. If it takes longer than 3 seconds to connect, the request was going to be slow anyway and it's better to fail fast and let the user retry.

---

## Optimization Priority Order

Ranked by expected latency improvement, implementation effort, and risk:

| Priority | Technique | Expected Savings | Effort | Risk |
|----------|-----------|-----------------|--------|------|
| 1 | Persistent COM object | 50-150ms (first call), 10-30ms (warm) | Low (move one line) | Very low |
| 2 | Diff-based output (structured) | 200-800ms (fewer output tokens) | Medium (new prompt + parser) | Medium (quality) |
| 3 | Predicted Outputs (gpt-4.1 via Chat Completions) | 300-1000ms | Medium (dual API path) | Low (well-documented) |
| 4 | AHK delay settings | 1-5ms | Very low (two lines) | None |
| 5 | Timeout tuning | 0ms normal, faster failure | Very low | None |
| 6 | Prompt caching | 0ms typical (prompt too short) | None | None |

## Alternatives Considered

| Category | Recommended | Alternative | Why Not |
|----------|-------------|-------------|---------|
| HTTP client | WinHttp.WinHttpRequest.5.1 (kept) | LibQurl (libcurl wrapper) | Adds DLL dependencies; breaks self-contained constraint; marginal speed gain |
| HTTP client | WinHttp COM (kept) | cURL via Run | Process spawn adds 50-100ms overhead; parsing stdout is fragile |
| JSON parsing | Regex extraction (kept) | jq via Run | External dependency; process spawn; current regex runs in microseconds |
| Response format | Full text (current default) | Streaming SSE | WinHttp COM cannot do incremental SSE; no benefit for paste-back workflow |
| API endpoint | Responses API (kept for reasoning models) | Chat Completions (add for gpt-4.1) | Only add if implementing Predicted Outputs; not a replacement |

## Anti-Recommendations (What NOT to Build)

| Anti-Technique | Why Avoid |
|----------------|-----------|
| SSE streaming in AHK | COM object fundamentally incompatible; complexity explosion for zero benefit in paste-back workflow |
| Replace WinHttp with libcurl | Adds 3 DLL dependencies (libcurl, libcrypto, libssl); complexity for negligible speed gain on single requests |
| Prompt padding for caching | Wastes tokens and money; prompt caching only helps at 1024+ tokens which typical spell-check prompts never reach |
| Async/parallel requests | Single spell-check operation; nothing to parallelize |
| ADODB.Stream optimization | UTF-8 decoding via ADODB.Stream takes <1ms; not a bottleneck |
| Full JSON parser rewrite | JsonLoad fallback runs only on regex failure (extremely rare); optimizing it is wasted effort |

## Installation / Code Changes

No new dependencies. All optimizations use existing AHK v2 built-ins and OpenAI API features.

```ahk
; === Add to script startup (auto-execute section) ===

; Performance: reduce artificial delays
SetKeyDelay(-1)
SetControlDelay(-1)

; Performance: create WinHttp COM object once and reuse
global httpObj := ComObject("WinHttp.WinHttpRequest.5.1")

; === In hotkey handler, replace lines 895-901 with: ===

; Reuse persistent HTTP object (no `global` needed for method calls)
httpObj.Open("POST", apiUrl, false)
httpObj.SetTimeouts(3000, 3000, 30000, 30000)
httpObj.SetRequestHeader("Content-Type", "application/json; charset=utf-8")
httpObj.SetRequestHeader("Authorization", "Bearer " . apiKey)
logData.timings.requestSent := A_TickCount
httpObj.Send(jsonPayload)
```

For diff-based output and Predicted Outputs, see the detailed implementation notes in sections 3 and 4 above.

## Sources

- [WinHttp connection pooling and keep-alive behavior](https://microsoft.public.winhttp.narkive.com/QcDt7dZL/persistent-connection-of-http-1-1-with-winhttprequest-5-0) -- HIGH confidence
- [WinHttpRequest COM object reference (Microsoft)](https://learn.microsoft.com/en-us/windows/win32/winhttp/winhttprequest) -- HIGH confidence
- [AHK v2 Performance docs](https://www.autohotkey.com/docs/v2/misc/Performance.htm) -- HIGH confidence
- [AHK v2 global variable scoping rules](https://www.autohotkey.com/docs/v2/Functions.htm) -- HIGH confidence
- [AHK community: Faster Multiple WinHttp requests](https://www.autohotkey.com/boards/viewtopic.php?t=91213) -- MEDIUM confidence
- [AHK community: Optimizing AHKv2 Code for Speed](https://www.autohotkey.com/boards/viewtopic.php?style=19&t=124617) -- HIGH confidence
- [OpenAI Prompt Caching guide](https://developers.openai.com/api/docs/guides/prompt-caching) -- HIGH confidence
- [OpenAI Latency Optimization guide](https://platform.openai.com/docs/guides/latency-optimization) -- HIGH confidence
- [OpenAI Predicted Outputs guide](https://developers.openai.com/api/docs/guides/predicted-outputs) -- HIGH confidence
- [OpenAI Structured Outputs guide](https://developers.openai.com/api/docs/guides/structured-outputs) -- HIGH confidence
- [GPT-4.1 Prompting Guide (diff format)](https://developers.openai.com/cookbook/examples/gpt4-1_prompting_guide) -- HIGH confidence
- [OpenAI community: Predicted Outputs not in Responses API (Feb 2026)](https://community.openai.com/t/predicted-outputs-in-response-api/1373125) -- HIGH confidence
- [OpenAI community: Responses API streaming events guide](https://community.openai.com/t/responses-api-streaming-the-simple-guide-to-events/1363122) -- MEDIUM confidence
- [AHK community: WinHttpRequest ResponseStream limitations](https://www.autohotkey.com/board/topic/71528-com-winhttprequest-responsestream/) -- MEDIUM confidence

---

*Stack research: 2026-03-27*
