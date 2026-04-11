# Phase 1: Persistent Background Server - Context

**Gathered:** 2026-04-11
**Status:** Ready for planning

<domain>
## Phase Boundary

A transparent HTTP proxy server running on localhost that maintains a warm connection to the OpenAI API. The AHK script sends its existing payload to the local server instead of directly to OpenAI, eliminating per-invocation TLS/TCP handshake overhead (~40-100ms savings). Current functionality is preserved exactly — the server is a connection-warmer only.

</domain>

<decisions>
## Implementation Decisions

### Server Architecture
- **D-01:** Transparent proxy — AHK sends the full OpenAI JSON payload unchanged, server forwards it to `api.openai.com` with a warm connection. All model logic, payload construction, response parsing, replacements, and prompt-leak guard stay in AHK. Server is a connection-warmer only.
- **D-02:** Stack is Python (Starlette + httpx + uvicorn), all already installed. Single `.pyw` file (`spellcheck-server.pyw`). No new dependencies.
- **D-03:** Server binds to `http://127.0.0.1:48080` (plain HTTP, fixed port, loopback only). AHK swaps its API URL from `https://api.openai.com/v1/responses` to `http://127.0.0.1:48080/v1/responses`.

### Server Lifecycle
- **D-04:** AHK auto-launches the server on script startup via `pythonw.exe` (no console window). Health check confirms server is ready before first use.
- **D-05:** Server runs until system shutdown or manual kill. PID file (`logs/server.pid`) prevents duplicate instances on AHK reload.
- **D-06:** No fallback to direct API. If the server is down, show an error tooltip to the user. This is a personal tool — no graceful degradation needed.

### Connection Management
- **D-07:** Global `httpx.AsyncClient(http2=True)` with `keepalive_expiry=120s`. Connection pool reused across all requests.
- **D-08:** Proactive keep-alive ping: background task sends `GET /v1/models` every 45 seconds to prevent OpenAI's ~60s idle timeout from killing the connection. Research found no official prohibition, no community reports of issues, zero cost (non-billable endpoint).

### API Key Handling
- **D-09:** Pull SEC-01 into Phase 1. Both AHK and server read the API key from the `OPENAI_API_KEY` environment variable. AHK passes it in the Authorization header, server forwards it transparently. Remove the hardcoded key from the AHK script.

### Logging and Observability
- **D-10:** Single source of truth: AHK logs everything to existing JSONL. Server returns `X-Proxy-Ms` response header (total proxy round-trip). Connection reuse is inferable from the value (sub-10ms = warm, 50ms+ = cold). Detailed breakdown (TLS, DNS, TCP, explicit reuse flag) deferred to Phase 4.
- **D-11:** Server writes minimal logs to `logs/server.log` (startup, shutdown, errors only). Critical because `pythonw.exe` swallows stdout/stderr.
- **D-12:** New AHK log field: `proxy_ms` (from server `X-Proxy-Ms` response header). Single real measurement — no placeholder fields.
- **D-13:** No dual-path logging. The server IS the path — no "proxy vs direct" distinction needed in logs.

### Claude's Discretion
- Server error response format (502/504 JSON structure)
- Exact logging format for server.log (plain text vs structured)
- PID file cleanup strategy on unclean shutdown
- Health check endpoint response shape

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Research
- `.planning/phases/01-persistent-background-server/01-RESEARCH.md` — Standard stack, architecture patterns, code examples, common pitfalls, latency analysis

### Requirements
- `.planning/REQUIREMENTS.md` §PERF-01 — Persistent WinHTTP connection requirement (now satisfied by proxy)
- `.planning/REQUIREMENTS.md` §SEC-01 — API key to environment variable (pulled into Phase 1)

### Codebase
- `.planning/codebase/ARCHITECTURE.md` — Current AHK HTTP communication pattern (lines 1280-1286 in script)
- `.planning/codebase/STACK.md` — Current technology stack and dependencies
- `.planning/codebase/CONVENTIONS.md` — Naming patterns, logging conventions, function design

</canonical_refs>

<code_context>
## Existing Code Insights

### Integration Points
- `Universal Spell Checker.ahk` lines 1280-1286: Current WinHttp COM request creation — swap `apiUrl` to localhost
- `Universal Spell Checker.ahk` line 1259: Hardcoded API key — remove, replace with env var read
- `Universal Spell Checker.ahk` line 60: `apiUrl` global — change to proxy URL
- `Universal Spell Checker.ahk` lines 333-483: Logging layer — add new proxy timing fields
- `logs/` directory: Already exists, server.log and server.pid go here

### Established Patterns
- WinHttp COM for HTTP requests (AHK will reuse this for localhost)
- JSONL structured logging with timing deltas
- `A_TickCount` millisecond timing instrumentation
- `pythonw.exe` available at `C:\Python313\pythonw.exe` for headless Python execution
- PascalCase with spaces for script files, lowercase for config/utility files

### Reusable Assets
- `GetUtf8Response()` — UTF-8 response reading via ADODB.Stream (reuse for proxy responses)
- `JsonEscape()` — JSON string escaping (reuse for new log fields)
- `ShowTemporaryToolTip()` — error display (reuse for server-down errors)
- Existing log schema — extend with new fields, don't restructure

</code_context>

<specifics>
## Specific Ideas

- User emphasized: "maintain current functionality and decrease the amount of time it takes from hotkey activation to paste"
- User wants comprehensive timing breakdown in logs for debugging and optimization visibility
- User explicitly chose no fallback path — error on server down, fix manually
- Keep-alive pinging researched and approved — `GET /v1/models` every 45s, no official prohibition found
- Phase 4 will optimize the server further (connection tuning, Predicted Outputs, AHK delays)

</specifics>

<deferred>
## Deferred Ideas

- **Smart proxy** (server handles payload construction, parsing, replacements) — good candidate for Phase 4 or future refactor. Would simplify AHK significantly but is a larger change.
- **Proactive keep-alive tuning** — Phase 4 can experiment with different ping intervals, endpoints, and HTTP/2 behavior
- **Server manages model configuration** — move model selector to server-side config. Deferred to avoid scope creep.

</deferred>

---

*Phase: 01-persistent-background-server*
*Context gathered: 2026-04-11*
