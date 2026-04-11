---
phase: 01-persistent-background-server
verified: 2026-04-11T23:23:10Z
status: human_needed
score: 3/4 must-haves verified
overrides_applied: 0
human_verification:
  - test: "Run two consecutive spell checks and compare proxy_ms values in JSONL logs"
    expected: "Second check shows proxy_ms < 10ms (warm connection) vs first check 50ms+ (cold)"
    why_human: "Requires running the full system end-to-end and comparing timing data across invocations"
  - test: "Kill the pythonw.exe process, then press Ctrl+Alt+U"
    expected: "Error tooltip appears with connection failure message; no text replacement occurs; no direct API fallback"
    why_human: "Requires interacting with live AHK script and observing tooltip behavior"
---

# Phase 1: Persistent Background Server Verification Report

**Phase Goal:** A transparent HTTP proxy server on localhost:48080 that maintains a warm httpx connection pool to the OpenAI API, so the AHK script delegates requests through it instead of opening a new HTTP connection every invocation -- eliminating cold-start TLS/HTTP overhead. Also migrates the API key from hardcoded to environment variable (SEC-01).
**Verified:** 2026-04-11T23:23:10Z
**Status:** human_needed
**Re-verification:** No -- initial verification

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | A background server process starts automatically and stays running across spell-check invocations | VERIFIED | `spellcheck-server.pyw` (276 lines) runs as Starlette ASGI on 127.0.0.1:48080. AHK calls `EnsureServerRunning()` at line 415 on startup, launching via `pythonw.exe --parent-pid`. PID file prevents duplicates. Keepalive ping every 45s keeps connections warm. |
| 2 | The AHK script sends spell-check requests to the local server instead of directly to the OpenAI API | VERIFIED | Line 1403: `http.Open("POST", proxyApiUrl, false)` where `proxyApiUrl = "http://127.0.0.1:48080/v1/responses"`. The direct `apiUrl` variable (line 64) exists for reference but is never used in any `http.Open()` call. |
| 3 | The second and subsequent spell checks in a session are measurably faster than a cold direct-to-API call | NEEDS HUMAN | Architecture supports this: `httpx.AsyncClient(http2=True, keepalive_expiry=120)`, 45s keepalive ping, `X-Proxy-Ms` header captured in logs. Actual measurement requires running the system. |
| 4 | If the server is not running, the AHK script shows an error tooltip (no fallback to direct API) | VERIFIED | Startup failure: line 416 shows "Spell check proxy server failed to start" tooltip. Request failure: WinHttp exception caught at line 1625, shows "Error: " tooltip. No direct API fallback -- `apiUrl` never used in `http.Open()`. |

**Score:** 3/4 truths verified (1 needs human testing)

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `spellcheck-server.pyw` | Transparent proxy server with persistent connection pooling | VERIFIED | 276 lines. Contains `httpx.AsyncClient`, Starlette routes, PID management, keepalive loop, health endpoint, timing headers. Syntax valid (ast.parse passes). All imports resolve. |
| `Universal Spell Checker.ahk` | Modified spell checker using proxy server | VERIFIED | Contains `127.0.0.1:48080` proxy routing, `EnsureServerRunning()`, `IsProxyAvailable()`, `.env` API key loading, `X-Proxy-Ms` header capture, `proxy` object in JSONL output. scriptVersion = 18. |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `Universal Spell Checker.ahk` | `spellcheck-server.pyw` | `Run pythonw.exe` to launch server | WIRED | Line 230: `Run('"pythonw.exe" "' . serverScriptPath . '" --parent-pid ' . ProcessExist(), A_ScriptDir, "Hide")` |
| `Universal Spell Checker.ahk` | `http://127.0.0.1:48080` | WinHttp POST to proxy | WIRED | Line 1403: `http.Open("POST", proxyApiUrl, false)` where `proxyApiUrl` = `"http://127.0.0.1:48080/v1/responses"` |
| `Universal Spell Checker.ahk` logData | `X-Proxy-Ms` response header | `GetResponseHeader` after API call | WIRED | Lines 1444-1449: `logData.proxyMs := http.GetResponseHeader("X-Proxy-Ms")` with try/catch, logged in events |
| `spellcheck-server.pyw` | `https://api.openai.com/v1/responses` | `httpx.AsyncClient.post` | WIRED | Line 202: `await http_client.post(target_url, ...)` where `target_url = OPENAI_BASE_URL + request.url.path` |
| `spellcheck-server.pyw` | `logs/server.pid` | PID file write/delete | WIRED | Lines 89-113: `write_pid_file()` writes PID, `remove_pid_file()` deletes on shutdown |

### Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
|----------|--------------|--------|--------------------|--------|
| `spellcheck-server.pyw` proxy_request | `resp` | `httpx.AsyncClient.post()` to OpenAI | Yes -- forwards real API response | FLOWING |
| `Universal Spell Checker.ahk` proxyMs | `logData.proxyMs` | `http.GetResponseHeader("X-Proxy-Ms")` | Yes -- real measurement from server `time.perf_counter()` delta | FLOWING |
| `Universal Spell Checker.ahk` apiKey | `apiKey` | `.env` file via `LoadRequiredEnvValue()` | Yes -- reads from file at startup | FLOWING |

### Behavioral Spot-Checks

| Behavior | Command | Result | Status |
|----------|---------|--------|--------|
| Server Python syntax valid | `python -c "import ast; ast.parse(open('spellcheck-server.pyw').read())"` | SYNTAX OK | PASS |
| Python dependencies available | `python -c "import httpx, starlette, uvicorn"` | IMPORTS OK | PASS |
| No hardcoded API key in AHK | `grep "sk-proj-" "Universal Spell Checker.ahk"` | No matches | PASS |
| Server listens on correct port | grep for `LISTEN_PORT = 48080` in server | Found at line 27 | PASS |
| AHK routes to proxy not direct API | `apiUrl` never used in `http.Open()` | Only `proxyApiUrl` used (line 1403) | PASS |

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|------------|-------------|--------|----------|
| PERF-01 | 01-01, 01-02 | Persistent connection eliminates TLS handshake per call | SATISFIED | Proxy server with `httpx.AsyncClient(http2=True, keepalive_expiry=120)` and 45s keepalive ping maintains warm connection pool. AHK routes through proxy. Note: REQUIREMENTS.md traceability table maps PERF-01 to Phase 3 but ROADMAP.md assigns it to Phase 1 -- implementation satisfies the requirement. |
| SEC-01 | 01-02 | API key moved from hardcoded source to environment variable | SATISFIED | Hardcoded `sk-proj-` key fully removed from AHK source. API key loaded from `.env` file via `LoadRequiredEnvValue()` at startup (line 412). Server also loads from `.env` via `_load_api_key()` with env var fallback. Deviation: uses `.env` file instead of system env var -- functionally equivalent, avoids terminal restart requirement. |

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| Universal Spell Checker.ahk | 64 | `apiUrl` defined but never used in requests | INFO | Dead variable kept for reference. Not a blocker -- just a reference constant. |

### Human Verification Required

### 1. Connection Reuse Performance

**Test:** Run two consecutive spell checks. Open the latest JSONL log file and compare the `proxy_ms` values.
**Expected:** First invocation may show 50-150ms proxy_ms (cold TLS handshake). Second and subsequent invocations should show <10ms proxy_ms (warm connection reuse). The keepalive ping at 45s intervals should keep connections warm indefinitely.
**Why human:** Requires running the full AHK + server system end-to-end, typing text, pressing Ctrl+Alt+U, and examining log files. Cannot be tested programmatically without starting the server and making real API calls.

### 2. Server-Down Error Tooltip

**Test:** Kill the pythonw.exe process (Task Manager or `taskkill`), then select text and press Ctrl+Alt+U.
**Expected:** An error tooltip appears with a connection failure message. No text replacement occurs. No attempt to call OpenAI directly.
**Why human:** Requires interacting with live AHK hotkey and observing Windows tooltip behavior. The error path (WinHttp connection exception) cannot be tested without killing the server process.

### Gaps Summary

No blocking gaps found. All artifacts exist, are substantive (276 and 1639+ lines), are properly wired to each other, and data flows through real sources. The only open item is human verification of actual performance improvement (SC-3), which is architecturally sound but requires live measurement.

**Noted deviations (non-blocking, documented in SUMMARY):**
- scriptVersion bumped to 18 instead of 17 (17 already used in prior commit 9aad25c)
- API key loaded from `.env` file instead of system env var (functionally equivalent, user-friendlier)
- Parent-PID lifecycle tie added (server auto-shuts down when AHK exits -- improvement over plan)
- Windows `OpenProcess` API replaces `os.kill(pid, 0)` for process checking (Python 3.13 compatibility fix)

---

_Verified: 2026-04-11T23:23:10Z_
_Verifier: Claude (gsd-verifier)_
