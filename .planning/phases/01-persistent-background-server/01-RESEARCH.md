# Phase 1: Persistent Background Server - Research

**Researched:** 2026-04-11
**Domain:** Local HTTP proxy server with persistent upstream connection pooling
**Confidence:** HIGH

## Summary

The goal is to eliminate the 50-150ms TLS/TCP handshake overhead that occurs on every spell-check invocation because the AHK script creates a fresh `WinHttp.WinHttpRequest.5.1` COM object per request. The solution is a lightweight Python HTTP server running on localhost that maintains a warm `httpx` connection pool to `api.openai.com`, so the AHK script's request path becomes: AHK -> plain HTTP localhost (sub-1ms) -> pre-established TLS connection to OpenAI.

The critical insight is that all the required Python libraries are **already installed** on this machine: `httpx` 0.28.1 (with HTTP/2 via `h2` 4.3.0), `starlette` 1.0.0, and `uvicorn` 0.42.0. No new dependencies need to be installed. The server should be a single `.pyw` file (runs via `pythonw.exe` with no console window) that the AHK script auto-launches on first use if not already running.

**Primary recommendation:** Build a single-file Starlette ASGI app served by uvicorn on `http://127.0.0.1:{port}`, using a global `httpx.AsyncClient(http2=True)` for persistent connection pooling to OpenAI. The AHK script hits this localhost endpoint with its existing WinHttp COM pattern (but targeting `http://127.0.0.1:{port}` instead of `https://api.openai.com`), with fallback to direct API if the server is unreachable.

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| PERF-01 | WinHTTP COM object persisted across invocations (eliminate 50-150ms TLS handshake per call) | Persistent background server with httpx connection pool eliminates per-invocation TLS handshake. AHK communicates over plain HTTP to localhost (no TLS), and the server maintains warm TLS connections to OpenAI via httpx's built-in connection pooling. |
</phase_requirements>

## Project Constraints (from CLAUDE.md)

- **Platform**: Windows only (AHK v2, WinHTTP COM, Windows clipboard API)
- **Runtime**: AutoHotkey v2.0 interpreter + Python 3.x (already present)
- **Performance**: Every operation optimized for minimal latency -- speed is the core value
- **Simplicity**: Self-contained files with minimal external dependencies
- **No build step**: Scripts run directly
- **API key**: Currently hardcoded in AHK script (line 877)
- **Logging**: Comprehensive JSONL timing instrumentation expected
- **scriptVersion**: Must be bumped before commits that include the AHK script

## Standard Stack

### Core

| Library | Version (Installed) | Latest | Purpose | Why Standard |
|---------|-------------------|--------|---------|--------------|
| httpx | 0.28.1 | 0.28.1 | Async HTTP client with connection pooling and HTTP/2 | Already installed; used by openai-python SDK itself; connection pooling built-in with configurable keep-alive [VERIFIED: pip show httpx] |
| h2 | 4.3.0 | 4.3.0 | HTTP/2 protocol support for httpx | Already installed; enables HTTP/2 multiplexing to OpenAI [VERIFIED: pip show h2] |
| starlette | 1.0.0 | 1.0.0 | Lightweight ASGI framework for the local server | Already installed; minimal overhead; single-route app [VERIFIED: pip show starlette] |
| uvicorn | 0.42.0 | 0.44.0 | ASGI server to run the starlette app | Already installed; fast startup; production-grade [VERIFIED: pip show uvicorn] |
| httpcore | 1.0.9 | - | Low-level HTTP transport (httpx dependency) | Already installed; manages actual connection pool [VERIFIED: pip show httpcore] |

### Supporting

| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| pythonw.exe | (Python 3.13.6) | Run server without console window | Always -- server must be invisible [VERIFIED: where pythonw.exe -> C:\Python313\pythonw.exe] |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Starlette + uvicorn | Python stdlib `http.server` | stdlib has no async support, no connection pooling, known performance issues (gethostbyaddr per request), would require hand-rolling everything. Starlette/uvicorn already installed. |
| Starlette + uvicorn | Flask | Flask not installed, WSGI (sync), would need waitress or gunicorn. No benefit over what's already available. |
| Starlette + uvicorn | Node.js http server | Node 22.14.0 is available, but would mean maintaining JS code alongside AHK+Python. Python stack already installed and used in project. |
| Starlette + uvicorn | aiohttp server | aiohttp 3.13.2 is installed. Viable alternative but heavier than starlette for a single-endpoint proxy. More dependencies (multidict, yarl, etc). |
| Starlette + uvicorn | Pure asyncio TCP server | No framework overhead but requires hand-rolling HTTP parsing, error handling, content-length, chunked encoding. Not worth it for a single endpoint. |
| httpx | requests | requests has no HTTP/2 support, no async, less sophisticated connection pooling. httpx already installed. |

**Installation:** No installation needed. All required packages are already present.

```bash
# Verification only -- all packages already installed
pip show httpx h2 starlette uvicorn httpcore
```

**Version verification:**
- httpx 0.28.1 -- installed, matches latest on PyPI [VERIFIED: pip index versions httpx]
- uvicorn 0.42.0 -- installed, latest is 0.44.0 (minor update, not required) [VERIFIED: pip index versions uvicorn]
- starlette 1.0.0 -- installed, matches latest on PyPI [VERIFIED: pip index versions starlette]
- h2 4.3.0 -- installed [VERIFIED: pip show h2]
- Python 3.13.6 -- installed [VERIFIED: python --version]

## Architecture Patterns

### Recommended Project Structure

```
Universal Spell Check/
|-- Universal Spell Checker.ahk          # Modified: add proxy client + fallback logic
|-- spellcheck-server.pyw                # NEW: background proxy server (single file)
|-- replacements.json                    # Unchanged
|-- generate_log_viewer.py               # Unchanged
|-- logs/                                # Unchanged
```

### Pattern 1: Single-File ASGI Proxy Server

**What:** A `.pyw` file containing a Starlette app with one POST endpoint that forwards requests to OpenAI via a persistent `httpx.AsyncClient`.

**When to use:** Always -- this is the server.

**Key design decisions:**
- `.pyw` extension so `pythonw.exe` runs it without a console window [CITED: coderslegacy.com/pythonw-exe-tutorial]
- Single global `httpx.AsyncClient(http2=True)` created at startup, reused for all requests [CITED: python-httpx.org/advanced/clients]
- Server binds to `127.0.0.1` only (not `0.0.0.0`) for security
- Plain HTTP on localhost (no TLS needed for loopback)
- Fixed port (e.g., `48080`) with fallback detection in AHK

**Example:**
```python
# spellcheck-server.pyw
# Source: httpx docs (python-httpx.org/advanced/clients) + starlette docs (starlette.io)
import asyncio
import logging
import sys
import os

import httpx
import uvicorn
from starlette.applications import Starlette
from starlette.requests import Request
from starlette.responses import Response
from starlette.routing import Route

OPENAI_API_URL = "https://api.openai.com/v1/responses"
LISTEN_HOST = "127.0.0.1"
LISTEN_PORT = 48080
LOG_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "logs", "server.log")

# Global persistent client -- connection pool reused across all requests
http_client: httpx.AsyncClient | None = None

async def startup():
    global http_client
    http_client = httpx.AsyncClient(
        http2=True,
        limits=httpx.Limits(
            max_keepalive_connections=5,
            max_connections=10,
            keepalive_expiry=120.0,  # Keep idle connections alive 2 min
        ),
        timeout=httpx.Timeout(
            connect=5.0,
            read=35.0,   # OpenAI can take up to 30s
            write=5.0,
            pool=5.0,
        ),
    )

async def shutdown():
    global http_client
    if http_client:
        await http_client.aclose()

async def proxy_request(request: Request) -> Response:
    """Forward spell-check request to OpenAI, reusing persistent connection."""
    body = await request.body()
    api_key = request.headers.get("authorization", "")

    resp = await http_client.post(
        OPENAI_API_URL,
        content=body,
        headers={
            "content-type": "application/json; charset=utf-8",
            "authorization": api_key,
        },
    )

    return Response(
        content=resp.content,
        status_code=resp.status_code,
        headers={"content-type": resp.headers.get("content-type", "application/json")},
    )

async def health(request: Request) -> Response:
    """Health check endpoint for AHK to verify server is running."""
    return Response(content='{"status":"ok"}', media_type="application/json")

app = Starlette(
    routes=[
        Route("/v1/responses", proxy_request, methods=["POST"]),
        Route("/health", health, methods=["GET"]),
    ],
    on_startup=[startup],
    on_shutdown=[shutdown],
)

if __name__ == "__main__":
    uvicorn.run(app, host=LISTEN_HOST, port=LISTEN_PORT, log_level="warning")
```

### Pattern 2: AHK Client with Proxy + Fallback

**What:** The AHK script tries the local proxy first, falls back to direct API on failure.

**When to use:** Always -- this is the client modification.

**Example:**
```autohotkey
; Source: AHK v2 docs (autohotkey.com/docs/v2/lib/Run.htm)
; Configuration
proxyUrl := "http://127.0.0.1:48080"
proxyHealthUrl := proxyUrl . "/health"
serverScriptPath := A_ScriptDir . "\spellcheck-server.pyw"

; Try proxy first, fall back to direct
useProxy := IsProxyAvailable()

if (useProxy) {
    apiTarget := proxyUrl . "/v1/responses"
} else {
    apiTarget := "https://api.openai.com/v1/responses"
}

; ... existing request logic using apiTarget instead of apiUrl ...

IsProxyAvailable() {
    try {
        http := ComObject("WinHttp.WinHttpRequest.5.1")
        http.SetTimeouts(500, 500, 500, 500)  ; Very short timeout for health check
        http.Open("GET", proxyHealthUrl, false)
        http.Send()
        return (http.Status = 200)
    } catch {
        return false
    }
}
```

### Pattern 3: AHK Auto-Launch Server

**What:** AHK launches the Python server on first use if not running.

**When to use:** Always -- server must auto-start without user intervention.

**Example:**
```autohotkey
; Source: AHK v2 docs (autohotkey.com/docs/v2/lib/Run.htm + ProcessExist)
EnsureServerRunning() {
    ; Check if server is already responding
    if (IsProxyAvailable())
        return true

    ; Launch server hidden via pythonw.exe
    serverScript := A_ScriptDir . "\spellcheck-server.pyw"
    if !FileExist(serverScript)
        return false

    Run('"C:\Python313\pythonw.exe" "' . serverScript . '"', A_ScriptDir, "Hide")

    ; Wait for server to become available (up to 3 seconds)
    startWait := A_TickCount
    while (A_TickCount - startWait < 3000) {
        if (IsProxyAvailable())
            return true
        Sleep(100)
    }
    return false  ; Server didn't start in time, use direct path
}
```

### Anti-Patterns to Avoid

- **Creating httpx client per request:** Defeats the entire purpose. The client MUST be a long-lived global. Recreating it per request means a new TLS handshake every time. [CITED: python-httpx.org/advanced/clients]
- **Using HTTPS between AHK and localhost:** Adds unnecessary TLS overhead on the local loop. Plain HTTP on `127.0.0.1` is both faster and secure (loopback never leaves the machine).
- **Using `http.server` from stdlib:** Synchronous, known performance issues (reverse DNS lookup per request), no connection pooling. [CITED: bugs.python.org/issue14622]
- **Hardcoding pythonw.exe path:** Use `A_ScriptDir` or detect the path dynamically. Different machines may have Python in different locations.
- **Persisting WinHttp COM object in AHK instead:** While `Static whr := ComObject(...)` can persist the COM object, WinHttp.WinHttpRequest.5.1 does NOT expose connection pool management. Each `.Open()` call may or may not reuse connections depending on OS-level WinHTTP session behavior, which is opaque and unreliable for guaranteeing warm connections. The Python proxy approach gives explicit control over connection pooling. [ASSUMED]

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| HTTP connection pooling to OpenAI | Custom socket management | `httpx.AsyncClient` with `http2=True` | Connection pooling, TLS session resumption, HTTP/2 multiplexing, retry logic -- hundreds of edge cases [CITED: python-httpx.org/advanced/clients] |
| Local HTTP server | Raw `asyncio` TCP server with manual HTTP parsing | Starlette + uvicorn | HTTP parsing, content-length, error responses, graceful shutdown -- all solved [VERIFIED: starlette already installed] |
| HTTP/2 protocol handling | Manual frame parsing | `h2` library (via httpx) | HTTP/2 is complex (streams, flow control, HPACK header compression) [VERIFIED: h2 already installed] |
| Process management (console-less) | Custom Windows service via pywin32 | `pythonw.exe` with `.pyw` extension | Zero-dependency way to run Python without console [VERIFIED: pythonw.exe at C:\Python313\pythonw.exe] |
| Health checking | Custom TCP probe | Simple GET endpoint + WinHttp with short timeout | Reliable, testable, minimal code |

**Key insight:** Every component needed for this phase is already installed on the machine. The entire server can be built with zero new `pip install` commands.

## Common Pitfalls

### Pitfall 1: httpx keepalive_expiry Too Short

**What goes wrong:** Default httpx `keepalive_expiry` is 5 seconds. If the user waits more than 5 seconds between spell checks (extremely likely), the connection is closed and the next request does a fresh TLS handshake -- defeating the entire purpose.
**Why it happens:** httpx defaults are tuned for high-throughput servers making many requests per second, not for intermittent single-user use.
**How to avoid:** Set `keepalive_expiry=120.0` (2 minutes) or even `300.0` (5 minutes). OpenAI servers appear to allow connections to idle for approximately 60 seconds before server-side termination. [CITED: community.openai.com/t/how-to-reuse-keep-alive-connections-for-streaming-responses/882953]
**Warning signs:** Log the connection state on each proxy request. If `httpx` is creating new connections frequently, the expiry is too short.

### Pitfall 2: Port Conflicts

**What goes wrong:** Port 48080 (or whatever is chosen) is already in use by another application.
**Why it happens:** Developers often run many local services.
**How to avoid:** Use a high port number (49152-65535 is the dynamic/private range). Detect port conflicts at server startup and log a clear error. Consider a small port scan or configurable port.
**Warning signs:** Server fails to start, AHK falls back to direct API every time.

### Pitfall 3: Server Zombie Process

**What goes wrong:** Multiple server instances running after repeated AHK script reloads.
**Why it happens:** AHK launches `pythonw.exe` but doesn't track or clean up the process.
**How to avoid:** Write a PID file at server startup. Before launching, check if the PID file process is still alive. Kill stale processes. Also: the health check endpoint is the primary mechanism -- if the server is already healthy, don't launch another.
**Warning signs:** Multiple `pythonw.exe` processes in Task Manager.

### Pitfall 4: Windows 11 KB5066835 Localhost HTTP/2 Bug

**What goes wrong:** A Windows 11 update (October 2025) broke localhost HTTP/2 connections via HTTP.sys.
**Why it happens:** OS-level regression in HTTP/2 TLS negotiation on loopback.
**How to avoid:** Use plain HTTP/1.1 between AHK and the local server (no TLS on localhost). HTTP/2 is only used on the outbound connection to OpenAI (which goes through httpx, not HTTP.sys). This bug does not affect our architecture. [CITED: bleepingcomputer.com -- "Microsoft fixes Windows bug breaking localhost HTTP connections"]
**Warning signs:** If someone adds HTTPS to the localhost connection, this bug could resurface. Don't.

### Pitfall 5: pythonw.exe Swallows All Output

**What goes wrong:** Server crashes silently with no error output visible anywhere.
**Why it happens:** `pythonw.exe` has no stdout/stderr console. Unhandled exceptions vanish.
**How to avoid:** Configure Python `logging` module to write to a log file (`logs/server.log`) from the very first line of the server script. Wrap the entire main block in try/except that logs to file.
**Warning signs:** Server not running, no indication why. Check `logs/server.log`.

### Pitfall 6: Fallback Path Not Tested

**What goes wrong:** The direct-to-API fallback path has a bug that's never caught because the server is always running during development.
**Why it happens:** Happy path bias during testing.
**How to avoid:** Explicitly test with the server stopped. Log which path was used (proxy vs direct) in the AHK JSONL logs.
**Warning signs:** Server goes down and spell checking breaks entirely.

### Pitfall 7: DNS Resolution Overhead on Localhost

**What goes wrong:** Using `localhost` instead of `127.0.0.1` in the AHK URL triggers a DNS lookup on every request.
**Why it happens:** `localhost` may resolve via DNS, hosts file, or mDNS. On some Windows configs it resolves to `::1` (IPv6) first, adding delay.
**How to avoid:** Always use `http://127.0.0.1:48080` not `http://localhost:48080`. Bind the server to `127.0.0.1` explicitly. [ASSUMED]
**Warning signs:** Inconsistent latency on the local hop.

## Code Examples

### Full Server Lifecycle

```python
# Source: httpx docs + starlette docs + uvicorn docs
# spellcheck-server.pyw -- complete minimal implementation

import asyncio
import logging
import os
import sys

import httpx
import uvicorn
from starlette.applications import Starlette
from starlette.requests import Request
from starlette.responses import Response, JSONResponse
from starlette.routing import Route

# --- Configuration ---
LISTEN_HOST = "127.0.0.1"
LISTEN_PORT = 48080
OPENAI_URL = "https://api.openai.com/v1/responses"
KEEPALIVE_EXPIRY = 120.0  # seconds to keep idle connections warm
PID_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "logs", "server.pid")
LOG_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "logs", "server.log")

# --- Logging (critical for pythonw.exe where stdout is /dev/null) ---
os.makedirs(os.path.dirname(LOG_FILE), exist_ok=True)
logging.basicConfig(
    filename=LOG_FILE,
    level=logging.INFO,
    format="%(asctime)s %(levelname)s %(message)s",
)
log = logging.getLogger("spellcheck-server")

# --- Global state ---
http_client: httpx.AsyncClient | None = None

async def startup():
    global http_client
    http_client = httpx.AsyncClient(
        http2=True,
        limits=httpx.Limits(
            max_keepalive_connections=5,
            max_connections=10,
            keepalive_expiry=KEEPALIVE_EXPIRY,
        ),
        timeout=httpx.Timeout(connect=5.0, read=35.0, write=5.0, pool=5.0),
    )
    # Write PID file
    with open(PID_FILE, "w") as f:
        f.write(str(os.getpid()))
    log.info("Server started on %s:%d (PID %d)", LISTEN_HOST, LISTEN_PORT, os.getpid())

async def shutdown():
    global http_client
    if http_client:
        await http_client.aclose()
    # Clean up PID file
    try:
        os.remove(PID_FILE)
    except OSError:
        pass
    log.info("Server shut down")

async def proxy(request: Request) -> Response:
    body = await request.body()
    auth = request.headers.get("authorization", "")
    try:
        resp = await http_client.post(
            OPENAI_URL,
            content=body,
            headers={
                "content-type": "application/json; charset=utf-8",
                "authorization": auth,
            },
        )
        return Response(
            content=resp.content,
            status_code=resp.status_code,
            headers={"content-type": resp.headers.get("content-type", "application/json")},
        )
    except httpx.ConnectError as e:
        log.error("Upstream connection failed: %s", e)
        return JSONResponse({"error": "upstream_connect_failed"}, status_code=502)
    except httpx.TimeoutException as e:
        log.error("Upstream timeout: %s", e)
        return JSONResponse({"error": "upstream_timeout"}, status_code=504)

async def health(request: Request) -> Response:
    return JSONResponse({"status": "ok"})

app = Starlette(
    routes=[
        Route("/v1/responses", proxy, methods=["POST"]),
        Route("/health", health, methods=["GET"]),
    ],
    on_startup=[startup],
    on_shutdown=[shutdown],
)

if __name__ == "__main__":
    uvicorn.run(app, host=LISTEN_HOST, port=LISTEN_PORT, log_level="warning")
```

### AHK Health Check with Short Timeout

```autohotkey
; Source: AHK v2 WinHttp pattern from existing codebase
IsProxyAvailable() {
    try {
        http := ComObject("WinHttp.WinHttpRequest.5.1")
        http.SetTimeouts(300, 300, 300, 300)  ; 300ms total -- fast fail
        http.Open("GET", "http://127.0.0.1:48080/health", false)
        http.Send()
        return (http.Status = 200)
    } catch {
        return false
    }
}
```

### AHK Auto-Launch with PID Tracking

```autohotkey
; Source: AHK v2 docs Run/ProcessExist
EnsureServerRunning() {
    if (IsProxyAvailable())
        return true

    serverScript := A_ScriptDir . "\spellcheck-server.pyw"
    if !FileExist(serverScript)
        return false

    ; Launch hidden (no console)
    Run('"pythonw.exe" "' . serverScript . '"', A_ScriptDir, "Hide", &serverPID)

    ; Poll for server readiness
    deadline := A_TickCount + 3000
    while (A_TickCount < deadline) {
        if (IsProxyAvailable())
            return true
        Sleep(100)
    }
    return false
}
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| requests library (HTTP/1.1 only) | httpx with HTTP/2 | 2023 (openai-python v1) | Multiplexed connections, reduced overhead [CITED: github.com/openai/openai-python/issues/632] |
| New TLS per request | Connection pool with keep-alive | Standard practice | 50-150ms saved per request after first [ASSUMED based on project measurements] |
| WinHttp.WinHttpRequest COM per call | Persistent local proxy | This phase | Eliminates per-invocation COM + TLS overhead |

**Deprecated/outdated:**
- `requests` library for OpenAI: The official openai-python SDK moved to httpx in v1 (Oct 2023). requests lacks HTTP/2. [CITED: github.com/openai/openai-python/issues/632]
- Python `http.server` for API proxying: Known performance bug with reverse DNS per request. Not suitable for latency-sensitive work. [CITED: bugs.python.org/issue14622]

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | WinHttp.WinHttpRequest.5.1 does not reliably maintain connection pools across `.Open()` calls, making AHK-side persistence insufficient | Anti-Patterns | If WinHttp actually does reliable keep-alive, the proxy server is unnecessary overhead. However, the project explicitly measured 50-150ms overhead per call, confirming fresh connections. |
| A2 | OpenAI servers idle-timeout connections at approximately 60 seconds | Pitfall 1 | If shorter, need more aggressive keepalive pinging. If longer, current design still works fine. |
| A3 | Using `127.0.0.1` avoids DNS lookup overhead vs `localhost` on Windows | Pitfall 7 | If Windows resolves localhost instantly from hosts file, minimal impact either way. Using IP is safer regardless. |
| A4 | Port 48080 is unlikely to conflict on a personal development machine | Configuration | If it conflicts, server won't start. Detectable at startup. |
| A5 | The 50-150ms overhead figure from REQUIREMENTS.md is primarily TLS handshake + TCP setup, not WinHttp COM instantiation | Summary | If COM object creation is the bottleneck (not TLS), the proxy may not help as much. But COM creation is fast (~1ms); TLS is the dominant cost. |

## Open Questions

1. **Exact OpenAI keep-alive timeout**
   - What we know: Community reports ~60 second idle timeout. Official docs don't specify. [CITED: community.openai.com forum]
   - What's unclear: The exact server-side idle timeout for api.openai.com connections.
   - Recommendation: Set httpx `keepalive_expiry=120.0` and monitor. If connections are being reset, the server should log it and we can tune down. The proxy transparently re-establishes connections anyway.

2. **HTTP/2 multiplexing benefit for single-user**
   - What we know: HTTP/2 allows multiple concurrent streams over one connection. [VERIFIED: httpx HTTP/2 works locally]
   - What's unclear: For a single user making one request at a time, does HTTP/2 provide measurable benefit over HTTP/1.1 with keep-alive?
   - Recommendation: Enable HTTP/2 (`http2=True`) since it's zero-cost (h2 already installed) and provides faster connection reuse semantics. Measure whether it actually helps vs HTTP/1.1.

3. **Optimal keepalive pinging strategy**
   - What we know: If the connection idles too long, OpenAI closes it and next request incurs full handshake.
   - What's unclear: Whether proactive keep-alive pinging (periodic lightweight requests) is worth the complexity.
   - Recommendation: Don't implement proactive pinging in Phase 1. Let the connection die naturally and re-establish on next request. The latency penalty is only on the first request after a long idle. Phase 4 can add smarter keep-alive tuning.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| Python 3.x | Server runtime | Yes | 3.13.6 | -- |
| pythonw.exe | Console-less execution | Yes | (Python 3.13.6) | python.exe with window hidden |
| httpx | Connection pooling | Yes | 0.28.1 | -- |
| h2 | HTTP/2 support | Yes | 4.3.0 | HTTP/1.1 (still works, less optimal) |
| starlette | ASGI framework | Yes | 1.0.0 | -- |
| uvicorn | ASGI server | Yes | 0.42.0 | -- |
| httpcore | HTTP transport | Yes | 1.0.9 | -- |
| Node.js | Not needed | Yes | 22.14.0 | -- |

**Missing dependencies with no fallback:** None.

**Missing dependencies with fallback:** None. All required software is present.

## Latency Analysis

### Current Path (per invocation)
```
AHK hotkey press
  -> ComObject("WinHttp.WinHttpRequest.5.1")  ~1-2ms [ASSUMED]
  -> DNS resolve api.openai.com                ~1-5ms (cached) [ASSUMED]
  -> TCP handshake to OpenAI                   ~20-40ms [ASSUMED: US East/West]
  -> TLS 1.3 handshake                         ~20-60ms (1-RTT) [CITED: thousandeyes.com]
  -> HTTP request/response                     ~200-2000ms (model inference)
  -> COM object destroyed                      ~0ms
Total overhead before model inference:         ~42-107ms
```

### Proposed Path (second+ invocation)
```
AHK hotkey press
  -> ComObject("WinHttp.WinHttpRequest.5.1")  ~1-2ms
  -> HTTP POST to 127.0.0.1:48080             ~0.1-0.5ms (loopback, no TLS)
  -> httpx forwards on warm connection         ~0.1-0.5ms (no handshake)
  -> HTTP request/response                     ~200-2000ms (model inference)
Total overhead before model inference:         ~1.3-3ms
```

**Expected savings: ~40-100ms per invocation** (after first request warms the connection). The first request through the proxy still incurs the full TLS handshake to OpenAI, but all subsequent requests reuse the connection.

## Sources

### Primary (HIGH confidence)
- [httpx official docs - Connection Pooling](https://www.python-httpx.org/advanced/clients/) -- connection reuse, client lifecycle
- [httpx official docs - Resource Limits](https://www.python-httpx.org/advanced/resource-limits/) -- keepalive_expiry defaults, pool limits
- [Starlette official docs](https://www.starlette.io/) -- routing, middleware, ASGI lifecycle
- [OpenAI API Latency Optimization](https://developers.openai.com/api/docs/guides/latency-optimization) -- official latency guidance
- [pip show / pip index versions] -- verified all package versions locally

### Secondary (MEDIUM confidence)
- [OpenAI Python SDK HTTP/2 Issue #632](https://github.com/openai/openai-python/issues/632) -- confirmed api.openai.com supports HTTP/2, SDK uses httpx
- [BleepingComputer - KB5066835 localhost fix](https://www.bleepingcomputer.com/news/microsoft/microsoft-fixes-windows-bug-breaking-localhost-http-connections/) -- Windows 11 localhost HTTP/2 bug and resolution
- [OpenAI Community - Keep-alive connections](https://community.openai.com/t/how-to-reuse-keep-alive-connections-for-streaming-responses/882953) -- ~60s idle timeout observation
- [TLS 1.3 performance analysis](https://www.thousandeyes.com/blog/optimizing-web-performance-tls-1-3) -- TLS handshake latency measurements

### Tertiary (LOW confidence)
- [AHK community - WinHttp reuse](https://www.autohotkey.com/boards/viewtopic.php?t=91213) -- patterns for persistent WinHttp objects (forum, not official)

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH -- all packages verified installed locally, versions confirmed against PyPI
- Architecture: HIGH -- standard reverse-proxy pattern, all components battle-tested
- Pitfalls: HIGH -- common issues well-documented across httpx/starlette ecosystems
- Latency estimates: MEDIUM -- based on general TLS benchmarks, not measured for this specific setup
- OpenAI keep-alive behavior: LOW -- community reports only, no official docs

**Research date:** 2026-04-11
**Valid until:** 2026-05-11 (stable stack, 30 days)
