# Model Configuration & API Integration

**Endpoint (all models):** `https://api.openai.com/v1/responses`
**Timeouts (Rust):** Each attempt shares one 30s deadline across send, receive, and body reads (`Client::once` in `rust/src/http.rs`). WinHTTP additionally sets resolve/connect limits of 5s each and send/receive limits of 30s each. A retryable failure or response can trigger one second attempt after a 500ms pause, so the overall call can exceed 30s.

## Current caller

Desktop T3 Code selections containing assistant citations append an instruction to
copy each protected placeholder exactly once, including standalone tokens. Other
T3 selections and other applications retain the existing prompt. Standard/reasoning
parameter selection is unchanged. `Protection::payload` and the prompt-leak guard
use the same instruction. A live synthetic test showed that whole-citation protection
alone could still be omitted; the explicit instruction addressed that failure.

`rust/src/http.rs` — persistent app-lifetime WinHTTP session/connection, sends directly to the Responses API. `src/OpenAiSpellcheckService.cs` is the retained C# reference with its own `HttpClient`. Settings supports `gpt-4.1` (default) and `gpt-5.4-mini`; changes apply to the next request.

## API key storage

Named API keys are saved as one DPAPI-encrypted collection through `SettingsStore` using `DataProtectionScope.CurrentUser` in `%LocalAppData%\{BuildChannel.AppDataFolder}\apikey.dat`. The dashboard shows only each name and a masked identifier derived in memory; full keys are never displayed or logged. The active key can be changed without restarting and applies to the next request.

The collection, including names and active-key selection, is never written to `settings.json` or any plain-text file. Prod and Dev collections remain isolated because `BuildChannel.AppDataFolder` differs. Existing single-key encrypted files load as a `Default` entry and migrate to the collection format on the next key change. Legacy install-directory migration copies `apikey.dat` only when the durable destination is missing, so it cannot replace an existing collection.

When `SettingsStore` receives a custom data directory, an omitted key-path override
must resolve inside that directory. The earlier independent fallback to
`AppPaths.ApiKeyPath` is superseded: it let an offline benchmark overwrite the
active saved credential. Regression tests inspect the resolved path before any
write, then verify encrypted round-trip isolation. Benchmarks also pass both paths
explicitly and reject the offline placeholder in live mode before any request.

---

## Standard vs. Reasoning models — do not mix parameters

| Feature | Standard (`gpt-4.1`) | Reasoning (`gpt-5.4-mini`) |
|---|---|---|
| `temperature` | Yes | **No — API error if present** |
| `top_p`, penalties, `logprobs` | Yes | No |
| `reasoning` block | No | Yes |
| `text.verbosity` | `"medium"` | `"low"` |
| API shape | Responses API | Responses API |

This is a hard rule (see `AGENTS.md` §2). Reasoning models return a 4xx API error when `temperature` is sent — always verify parameter compatibility, not just model name.

---

## Current payload — `gpt-4.1`

```json
{
  "model": "gpt-4.1",
  "input": [{"role": "user", "content": [{"type": "input_text", "text": "..."}]}],
  "store": true,
  "text": {"verbosity": "medium"},
  "temperature": 0.3
}
```

Prompt shape sent as `input[0].content[0].text`:

```
instructions: <PromptInstruction>
text input: <selected text>
```

The Rust instruction is `core::PROMPT`; `Protection::payload` adds the T3-specific
suffix when applicable. `Replacements::process` strips an echoed instruction and
`text input:` before restoration. `OpenAiSpellcheckService.PromptInstruction` and
`TextPostProcessor` remain the retained C# reference, superseded as runtime owners.

---

## Current reasoning payload — `gpt-5.4-mini`

```json
{"model": "gpt-5.4-mini", "input": [...], "store": true, "text": {"verbosity": "low"}, "reasoning": {"effort": "none", "summary": "auto"}}
```

- Correct shape is `reasoning: { effort: ... }`, NOT `reasoning_effort`.
- `store: true` required for all models.

---

## Response parsing

`OpenAiSpellcheckService.ExtractOutputText` tries two paths:
1. Top-level `output_text` string property (fast path).
2. Walk `output[].content[]` looking for `type == "output_text"` and a `text` property.

On parse failure (`output_text` empty or absent): logs `parse_failed empty_output_text`, returns `SpellcheckResult.Fail(ParseFailed, ...)`.

---

## Migration checklist when switching models

1. Model name/identifier correct.
2. Endpoint correct for the family.
3. **Every request parameter supported by this model type** — not just the version. Remove `temperature` for reasoning models; add `reasoning` block.
4. Response shape compatible with `ExtractOutputText`.
5. `text.verbosity` correct: `"medium"` for standard, `"low"` for reasoning.
6. Test with a real sample request before declaring done.

For any model change, also verify:
- Persistent `HttpClient` remains app-lifetime; do not recreate it per hotkey press.
- Failure paths still return a `SpellcheckResult` and do not paste over the user's selected text.
- Logs include `request_ms`, `request_attempts`, `status_code`, and `error_code` for failures.
- Post-processing (`TextPostProcessor`) still runs after successful model output and before paste.

## Native request attribution (October 23 follow-up)

The observed 720–1,843 ms desktop request samples put almost all time in the
existing `request_send_ms`, with about 0.03 ms in the header-wait bucket. These
labels are API-call boundaries, not proof of upload or server computation time.
Microsoft documents that WinHttpSendRequest may operate synchronously even on an
asynchronous session. The native diagnostics now split the API call from waiting
for its SENDREQUEST_COMPLETE callback without changing transport or payload.

`spellcheck_detail.request_native` contains `final_response_attempt`, `send_api_ms`,
`send_completion_wait_ms` and `native`. These describe the final parsed HTTP response attempt (1-based), including
terminal non-2xx responses; they do not indicate API success. Legacy aggregate request times still include retries/backoff. Replay
has null native data. Optional native queries record request-times QPC values,
their frequency, connection request index, request-stat flags and negotiated
protocol flags. Unsupported queries remain null; zero raw timestamp entries are
not evidence of a measured zero-duration phase. No headers, key or host identity
is captured by these diagnostics. Collection occurs after body read and has not
been shown to accelerate corrections.

Primary API contracts consulted this session:
[WinHttpSendRequest](https://learn.microsoft.com/en-us/windows/win32/api/winhttp/nf-winhttp-winhttpsendrequest),
[request times](https://learn.microsoft.com/en-us/windows/win32/api/winhttp/ns-winhttp-winhttp_request_times),
[time entries](https://learn.microsoft.com/en-us/windows/win32/api/winhttp/ne-winhttp-winhttp_request_time_entry),
[request stats](https://learn.microsoft.com/en-us/windows/win32/api/winhttp/ns-winhttp-winhttp_request_stats).

Native option queries are opt-in to the request-only diagnostic client; ordinary
corrections do not pay for these queries and report null `native` metadata.
The API-call/completion-wait split remains available in ordinary correction logs.
