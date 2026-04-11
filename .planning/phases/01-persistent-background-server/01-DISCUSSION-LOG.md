# Phase 1: Persistent Background Server - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-04-11
**Phase:** 01-persistent-background-server
**Areas discussed:** AHK-to-Server Communication, Server Lifecycle, API Key Handling, Logging and Observability, Connection Tuning

---

## AHK-to-Server Communication

### Q-01: Request/response contract

| Option | Description | Selected |
|--------|-------------|----------|
| Transparent proxy | AHK sends full OpenAI payload, server forwards unchanged | |
| Smart proxy | AHK sends text + model name, server builds the payload | |

**User's initial response (chat-more):** Asked if AHK can be removed entirely. Wanted to know which option is fastest to paste.

**Claude's response:** Explained AHK is still needed for hotkey capture, clipboard copy, and paste (OS-level operations). Presented comparison table showing transparent vs smart proxy have identical latency — the 40-100ms savings comes from warm connection either way. Recommended transparent proxy for Phase 1 (lowest risk, fastest to build).

**User's choice:** Transparent proxy
**Notes:** User agreed after understanding AHK can't be fully removed without a native Windows app (v2 scope).

### Q-02: Port selection

| Option | Description | Selected |
|--------|-------------|----------|
| Fixed port (48080) | Simplest, AHK always knows where to connect | X |
| Dynamic port with file discovery | Zero conflict risk, more complex | |

**User's choice:** Fixed port (48080)

---

## Server Lifecycle

### Q-03: How the server starts

| Option | Description | Selected |
|--------|-------------|----------|
| AHK auto-launches on startup | Always warm for first spell check | X |
| Lazy start on first spell check | 3s cold-start penalty on first use | |
| Windows startup task | Independent of AHK, always warm | |

**User's choice:** AHK auto-launches on startup

### Q-04: Server shutdown behavior

| Option | Description | Selected |
|--------|-------------|----------|
| Runs until system shutdown | Always warm, PID prevents duplicates | X |
| Tied to AHK lifecycle | Clean but cold start on reload | |
| Auto-shutdown after idle | Saves resources, cold start after idle | |

**User's choice:** Runs until system shutdown

### Q-05: Fallback detection strategy

| Option | Description | Selected |
|--------|-------------|----------|
| Health check before each request | Proactive detection, 300ms timeout | |
| Try proxy first, fall back on error | No extra round-trip | |
| Cache status at startup | One check, re-check on failure | |

**User's initial response (chat-more):** Pushed back on the entire premise. Said health check and fallback are separate concepts. Wants no graceful fallback — show error, fix manually. Personal tool.

**User's choice:** No fallback. Error on server down.
**Notes:** Simplified the architecture significantly — no dual-path logic needed.

---

## API Key Handling

### Q-06: Where the API key lives

| Option | Description | Selected |
|--------|-------------|----------|
| AHK passes in header, server forwards | Server never stores key | |
| Hardcode in server too | Duplicates key in two places | |
| Move to env var (pull SEC-01) | Both read OPENAI_API_KEY | X |

**User's choice:** Environment variable (pull SEC-01 into Phase 1)

---

## Logging and Observability

### Q-07: Server-side logging

| Option | Description | Selected |
|--------|-------------|----------|
| Server returns timing headers, AHK logs all | Single source of truth | X |
| Minimal server logging | Errors only, AHK logs round-trip | |
| Server logs detailed JSONL too | Two files, rich debugging | |

**User's initial response (chat-more):** Wanted whichever is faster. Open to moving everything to server or keeping in AHK. Emphasized single source of truth.

**User's choice:** Server returns timing headers, AHK logs everything

### Q-08: AHK log changes

| Option | Description | Selected |
|--------|-------------|----------|
| Add proxy_used + proxy_ms | Minimal addition | |
| Full breakdown from server | proxy_ms, connection_reused, tls_ms, dns_ms, tcp_ms | X |

**User's initial response (chat-more):** Doesn't want dual-path maintenance. Wants thorough logging for debugging and optimization.

**User's choice:** Full breakdown from server
**Notes:** User explicitly said no dual-path (proxy vs direct) — the server IS the path.

---

## Connection Tuning

### Q-09: Keep-alive expiry

| Option | Description | Selected |
|--------|-------------|----------|
| keepalive_expiry=120s | Research recommendation baseline | X |
| keepalive_expiry=300s | More aggressive, may outlast server timeout | |

**User's initial response (chat-more):** Didn't understand what this was. Claude explained the concept.

**Additional discussion:** User asked about proactive pinging to keep connections alive. Claude explained the approach (GET /v1/models every 45s). User requested research on safety/best practices. Research found: no official prohibition, no community reports of issues, HTTP/2 PING frames not available (OpenAI serves HTTP/1.1), TCP keep-alive doesn't prevent server-side closure. User decided to include pinging in Phase 1.

**User's choice:** keepalive_expiry=120s + proactive ping every 45s

---

## Claude's Discretion

- Server error response format
- Exact server.log format
- PID file cleanup strategy
- Health check endpoint shape

## Deferred Ideas

- Smart proxy (server handles payload construction) — Phase 4 candidate
- Advanced keep-alive tuning — Phase 4
- Server-side model configuration — future refactor
