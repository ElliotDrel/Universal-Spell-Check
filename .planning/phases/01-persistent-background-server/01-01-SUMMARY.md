---
phase: 01-persistent-background-server
plan: 01
subsystem: infra
tags: [python, starlette, httpx, uvicorn, proxy, http2, connection-pooling]

# Dependency graph
requires: []
provides:
  - "Transparent proxy server (spellcheck-server.pyw) on 127.0.0.1:48080"
  - "Persistent httpx connection pool with HTTP/2 to api.openai.com"
  - "X-Proxy-Ms timing header on proxied responses"
  - "Health endpoint at GET /health"
  - "PID-based duplicate instance prevention"
  - "Keep-alive ping loop every 45s to keep connections warm"
affects: [01-02, ahk-integration, performance-tuning]

# Tech tracking
tech-stack:
  added: [starlette, httpx, uvicorn, h2]
  patterns: [async-lifespan-context-manager, global-httpx-client, pid-file-locking, background-keepalive-task]

key-files:
  created:
    - spellcheck-server.pyw
  modified: []

key-decisions:
  - "Used Starlette 1.0 lifespan context manager instead of deprecated on_startup/on_shutdown"
  - "Path-transparent proxy: forwards request.url.path so any /v1/* route works"

patterns-established:
  - "Lifespan pattern: asynccontextmanager for startup/shutdown lifecycle in Starlette 1.0+"
  - "Global httpx.AsyncClient: single persistent client instance, never per-request"
  - "PID file locking: write PID, check if existing process alive via os.kill(pid, 0)"

requirements-completed: [PERF-01]

# Metrics
duration: 6min
completed: 2026-04-11
---

# Phase 1 Plan 01: Proxy Server Summary

**Single-file Starlette ASGI proxy on 127.0.0.1:48080 with persistent httpx HTTP/2 connection pool and 45s keep-alive ping to OpenAI**

## Performance

- **Duration:** 6 min
- **Started:** 2026-04-11T21:47:19Z
- **Completed:** 2026-04-11T21:53:46Z
- **Tasks:** 2
- **Files created:** 1

## Accomplishments
- Created spellcheck-server.pyw (231 lines) with full proxy, health, keep-alive, PID management, and logging
- Verified server starts on port 48080, health endpoint returns 200, PID and log files created correctly
- Fixed Starlette 1.0 API incompatibility (lifespan context manager replaces deprecated on_startup/on_shutdown)

## Task Commits

Each task was committed atomically:

1. **Task 1: Create spellcheck-server.pyw proxy server** - `9ed5fb8` (feat)
2. **Task 2: Verify server starts and proxies correctly** - `3cae7ee` (fix -- Starlette 1.0 lifespan compat)

## Files Created/Modified
- `spellcheck-server.pyw` - Transparent proxy server with persistent connection pooling to OpenAI API

## Decisions Made
- Used Starlette 1.0 `lifespan` async context manager instead of the removed `on_startup`/`on_shutdown` kwargs (Starlette 1.0 breaking change)
- Made proxy path-transparent (`OPENAI_BASE_URL + request.url.path`) so it can forward any OpenAI API path, not just `/v1/responses`

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Starlette 1.0 removed on_startup/on_shutdown**
- **Found during:** Task 2 (server start verification)
- **Issue:** Starlette 1.0.0 removed the `on_startup` and `on_shutdown` keyword arguments from `Starlette.__init__()`. The plan's code examples (from research) used the deprecated API.
- **Fix:** Replaced with `@asynccontextmanager`-based `lifespan` function passed as `lifespan=lifespan` kwarg. Startup code runs before `yield`, shutdown code runs after.
- **Files modified:** spellcheck-server.pyw
- **Verification:** Server starts, health returns 200, PID file created, log file written
- **Committed in:** 3cae7ee (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (1 blocking)
**Impact on plan:** Essential fix for runtime compatibility. No scope creep.

## Issues Encountered
- PID file not cleaned up on `terminate()` signal (Windows SIGTERM behavior) -- this is expected; graceful shutdown via Ctrl+C works. PID staleness is handled by the duplicate-check logic at startup.

## User Setup Required
None - no external service configuration required. Server uses OPENAI_API_KEY from environment variable (same as the AHK script after Plan 02 wiring).

## Next Phase Readiness
- Server ready for AHK integration in Plan 02
- AHK script needs to: swap apiUrl to localhost, auto-launch server via pythonw.exe, read X-Proxy-Ms header
- No blockers

## Self-Check: PASSED

- spellcheck-server.pyw: FOUND (230 lines)
- 01-01-SUMMARY.md: FOUND
- Commit 9ed5fb8: FOUND
- Commit 3cae7ee: FOUND

---
*Phase: 01-persistent-background-server*
*Completed: 2026-04-11*
