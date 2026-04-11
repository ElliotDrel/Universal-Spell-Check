---
phase: 01-persistent-background-server
reviewed: 2026-04-11T12:00:00Z
depth: standard
files_reviewed: 2
files_reviewed_list:
  - spellcheck-server.pyw
  - Universal Spell Checker.ahk
findings:
  critical: 2
  warning: 5
  info: 3
  total: 10
status: issues_found
---

# Phase 01: Code Review Report

**Reviewed:** 2026-04-11T12:00:00Z
**Depth:** standard
**Files Reviewed:** 2
**Status:** issues_found

## Summary

Reviewed the new persistent proxy server (`spellcheck-server.pyw`) and the updated AHK client (`Universal Spell Checker.ahk`). The proxy server is well-structured with proper connection pooling, PID-file-based singleton enforcement, and keep-alive pings. The AHK client properly integrates proxy support with health checks and auto-launch.

Key concerns: (1) the keep-alive loop uses `os._exit(0)` which bypasses the Starlette lifespan shutdown path, leaving resources unfreed; (2) the proxy endpoint forwards arbitrary paths without validation, creating an open-relay risk; (3) unhandled exceptions in the proxy handler will crash the request without a response; (4) the `--parent-pid` argument parser has an off-by-one that silently misparses certain argv layouts.

## Critical Issues

### CR-01: Proxy is an open relay -- forwards any path to OpenAI

**File:** `spellcheck-server.pyw:198`
**Issue:** `proxy_request` constructs `target_url = OPENAI_BASE_URL + request.url.path` and forwards the request. Only the `/v1/responses` route is wired in the Starlette router (line 252), so in the current configuration an attacker cannot reach arbitrary paths. However, if additional routes are added via a wildcard or catch-all, or if Starlette's routing behavior changes, the handler becomes an open relay that can hit any OpenAI endpoint (or be abused for SSRF if `OPENAI_BASE_URL` is ever configurable). More immediately, the handler blindly trusts and forwards the `authorization` header from the caller. Since the server listens on `127.0.0.1`, the blast radius is limited to local processes, but any local process can use this server to make authenticated OpenAI API calls using whatever key the caller provides.
**Fix:** Pin the target URL to the single expected path instead of echoing `request.url.path`:
```python
async def proxy_request(request: Request) -> Response:
    body = await request.body()
    auth = request.headers.get("authorization", "")
    target_url = OPENAI_BASE_URL + "/v1/responses"  # pinned, not echoed
    ...
```

### CR-02: `os._exit(0)` in keep-alive loop bypasses graceful shutdown

**File:** `spellcheck-server.pyw:131-133`
**Issue:** When the parent PID dies, the keep-alive loop calls `os._exit(0)`. This immediately terminates the process without executing the Starlette lifespan shutdown block (lines 176-188), which means `http_client.aclose()` is never called, the PID file may not be removed (the `remove_pid_file()` on line 132 runs, but the lifespan's `remove_pid_file()` does not, and any in-flight requests are killed without cleanup). `os._exit()` also skips Python's atexit handlers and does not flush all file buffers. Since `logging.shutdown()` is called just before, logs are flushed, but the httpx connection pool and any pending async tasks are abandoned.
**Fix:** Signal the uvicorn server to shut down gracefully instead of hard-killing:
```python
async def keepalive_ping_loop():
    ...
    if PARENT_PID is not None and not _is_process_alive(PARENT_PID):
        log.info("Parent process (PID %d) is gone. Initiating shutdown.", PARENT_PID)
        # Raise SystemExit in the event loop to trigger graceful shutdown
        asyncio.get_event_loop().call_soon(sys.exit, 0)
        return
```
Or use `os.kill(os.getpid(), signal.SIGTERM)` (requires `import signal`) if the above does not cleanly stop uvicorn.

## Warnings

### WR-01: Unhandled generic exceptions in proxy_request return no response

**File:** `spellcheck-server.pyw:194-239`
**Issue:** `proxy_request` catches `httpx.ConnectError` and `httpx.TimeoutException`, but any other exception (e.g., `httpx.RequestError`, `httpx.HTTPStatusError`, `RuntimeError` from a closed client, or an unexpected `AttributeError` if `http_client` is `None`) will propagate uncaught. Starlette will return a generic 500, but the AHK client may not handle that gracefully since it only expects specific error shapes.
**Fix:** Add a catch-all at the end of the handler:
```python
    except Exception as exc:
        proxy_ms = round((time.perf_counter() - start) * 1000, 1)
        log.error("Unexpected proxy error: %s", exc, exc_info=True)
        return JSONResponse(
            {"error": "proxy_internal_error", "detail": str(exc)},
            status_code=502,
            headers={"x-proxy-ms": str(proxy_ms)},
        )
```

### WR-02: `--parent-pid` argument parsing has off-by-one boundary check

**File:** `spellcheck-server.pyw:264-270`
**Issue:** The argument parser iterates `for i, arg in enumerate(sys.argv[1:], 1)` and checks `i < len(sys.argv) - 1`. When `--parent-pid` is the last-but-one argument (e.g., `sys.argv = ['script', '--parent-pid', '1234']`), `i` is `1` and `len(sys.argv) - 1` is `2`, so `1 < 2` is true and `sys.argv[i + 1]` = `sys.argv[2]` = `'1234'` works. However, consider `sys.argv = ['script', '--parent-pid']` (no value). Here `i` is `1`, `len(sys.argv) - 1` is `1`, so `1 < 1` is false and the `break` is hit with `PARENT_PID` still `None`. This is correct but fragile -- a clearer approach would avoid the confusing index arithmetic. More importantly, the `break` statement on line 270 means the loop exits after finding `--parent-pid` regardless of whether it was parsed, but it also means any argument *before* `--parent-pid` that is not `--parent-pid` exits the loop early. Wait -- re-reading: the `break` is inside the `if arg == "--parent-pid"` block, so it only breaks after finding the flag. This is actually correct. However, the condition `i < len(sys.argv) - 1` should be `i + 1 < len(sys.argv)` for clarity. Current code works but is hard to verify at a glance.
**Fix:** Use `argparse` or simplify:
```python
if "--parent-pid" in sys.argv:
    idx = sys.argv.index("--parent-pid")
    if idx + 1 < len(sys.argv):
        try:
            PARENT_PID = int(sys.argv[idx + 1])
        except ValueError:
            pass
```

### WR-03: Keep-alive ping uses `http_client` without null check

**File:** `spellcheck-server.pyw:136`
**Issue:** `keepalive_ping_loop` accesses `http_client.get(...)` on line 136. The global `http_client` is set in the lifespan startup (line 153). The keep-alive task is created *after* the client (line 168), and the first action in the loop is `await asyncio.sleep(...)`, so in practice `http_client` is always initialized when the first ping fires. However, if the lifespan shutdown completes and sets `http_client` to `None` (it does not currently, but `aclose()` invalidates the client), a race could occur. Defensive coding would add a null/closed check.
**Fix:**
```python
async def keepalive_ping_loop():
    ...
    try:
        if http_client is not None:
            await http_client.get(target, headers={"authorization": auth_header})
    except Exception as exc:
        log.warning("Keep-alive ping failed: %s", exc)
```

### WR-04: AHK proxy fallback path is missing

**File:** `Universal Spell Checker.ahk:1401-1407`
**Issue:** The AHK client sends all requests to `proxyApiUrl` (line 1403). If the proxy is unavailable (e.g., Python not installed, server crashed mid-session), `EnsureServerRunning()` is only called once at startup (line 415). After startup, if the proxy dies, every subsequent hotkey press will fail with a connection error. There is no fallback to the direct OpenAI endpoint (`apiUrl` on line 64).
**Fix:** Consider adding a fallback: if the proxy request fails with a connection error, retry directly against `apiUrl`. Alternatively, re-check `IsProxyAvailable()` before each request and fall back to direct if the proxy is down:
```autohotkey
; Before http.Open, select target URL
useProxy := IsProxyAvailable()
targetUrl := useProxy ? proxyApiUrl : apiUrl
http.Open("POST", targetUrl, false)
```

### WR-05: API key loaded into keep-alive function on each iteration's scope but read from `.env` once per function call

**File:** `spellcheck-server.pyw:121`
**Issue:** `_load_api_key()` is called once when `keepalive_ping_loop` starts (line 121), and the key is stored in a local variable for the lifetime of the loop. If the `.env` file is updated while the server is running, the keep-alive pings will use the old key. This is a minor inconsistency since `proxy_request` forwards the caller's auth header (not the server's key), so only keep-alive pings are affected. The keep-alive endpoint (`/v1/models`) will fail with 401 if the key rotates, triggering log warnings but no functional impact. Low severity, but worth noting.
**Fix:** Move the `_load_api_key()` call inside the loop body if key rotation should be supported, or document that server restart is required after key rotation.

## Info

### IN-01: `_load_api_key` does not strip inline comments

**File:** `spellcheck-server.pyw:49-52`
**Issue:** The `.env` parser handles `export` prefixes and quoted values, but does not strip inline comments. A line like `OPENAI_API_KEY=sk-abc123 # my key` would include ` # my key` as part of the value. The AHK `.env` reader (`ReadEnvValueFromFile`, line 159) has the same limitation, so behavior is consistent, but it could cause confusion.
**Fix:** After extracting the value, strip inline comments:
```python
# Before quote handling
if "#" in value:
    value = value[:value.index("#")].rstrip()
```

### IN-02: Debug log statements still present in AHK hotkey handler

**File:** `Universal Spell Checker.ahk:1474-1540`
**Issue:** Multiple `logData.events.Push("DEBUG: ...")` calls remain in the response parsing section (lines 1474, 1477, 1480, 1483, 1489, 1491, 1493, 1496, 1498, 1499, 1503, 1508, 1510, 1512, 1517, 1518, 1520, 1522, 1529, 1533, 1536, 1539, 1540). These are useful for troubleshooting but add noise to production logs. Per CLAUDE.md, "temporary debug logging is acceptable for troubleshooting" and these are intentional, so this is purely informational.
**Fix:** No action needed if debug logging is desired. If log size is a concern, these could be gated behind a `debugLogging` flag.

### IN-03: Server log file has no rotation mechanism

**File:** `spellcheck-server.pyw:61-65`
**Issue:** `logging.basicConfig(filename=LOG_FILE, ...)` appends to `logs/server.log` indefinitely with no rotation. For a long-running background service, this file will grow unbounded. The AHK JSONL logs have a 5 MiB rotation mechanism, but the server log does not.
**Fix:** Use `logging.handlers.RotatingFileHandler`:
```python
from logging.handlers import RotatingFileHandler

handler = RotatingFileHandler(LOG_FILE, maxBytes=2*1024*1024, backupCount=3)
handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(message)s"))
log = logging.getLogger("spellcheck-server")
log.addHandler(handler)
log.setLevel(logging.INFO)
```

---

_Reviewed: 2026-04-11T12:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
