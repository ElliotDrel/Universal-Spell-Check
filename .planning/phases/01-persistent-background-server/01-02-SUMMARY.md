---
phase: 01-persistent-background-server
plan: 02
subsystem: ahk-integration
tags: [autohotkey, proxy-integration, env-var, logging, lifecycle]

# Dependency graph
requires: [01-01]
provides:
  - "AHK auto-launches proxy server on startup via pythonw.exe"
  - "All spell-check requests routed through http://127.0.0.1:48080/v1/responses"
  - "API key loaded from .env file (hardcoded key removed)"
  - "Proxy timing (proxy_ms) captured in JSONL log entries"
  - "Server auto-shuts down when AHK exits (parent-PID lifecycle tie)"
affects: [performance-tuning, logging, security]

# Tech tracking
tech-stack:
  added: []
  patterns: [parent-pid-lifecycle, env-file-api-key, proxy-timing-headers, windows-openprocess-api]

key-files:
  created: []
  modified:
    - Universal Spell Checker.ahk
    - spellcheck-server.pyw

key-decisions:
  - "Used .env file loading (not system env var) to match existing AHK approach — avoids terminal restart requirement"
  - "scriptVersion bumped to 18 (not 17) to avoid version reuse from commit 9aad25c"
  - "No per-request health check — proxy failure caught by existing error handling path"
  - "Parent-PID passed via --parent-pid CLI arg, checked every 45s in keep-alive loop"
  - "Replaced os.kill(pid, 0) with Windows OpenProcess API — os.kill unreliable on Python 3.13"
  - "Added log_config=None to uvicorn.run() to fix formatter crash on Windows"
---

## Summary

Integrated the AHK spell checker with the proxy server from Plan 01. The script auto-launches `spellcheck-server.pyw` via `pythonw.exe` on startup, routes all API requests through `127.0.0.1:48080`, and captures `X-Proxy-Ms` timing headers in JSONL logs under a `"proxy"` object.

## Changes

### Universal Spell Checker.ahk
- Proxy configuration constants (`proxyHost`, `proxyPort`, `proxyApiUrl`, `proxyHealthUrl`, `serverScriptPath`)
- `IsProxyAvailable()` — 300ms timeout health check (startup only, not per-request)
- `EnsureServerRunning()` — launches server via pythonw.exe, polls up to 5s for readiness, passes `--parent-pid`
- Auto-launch call at script initialization
- API endpoint changed from `apiUrl` (direct OpenAI) to `proxyApiUrl` (localhost proxy)
- Restored `.env`-based API key loading (hardcoded `sk-proj-...` removed)
- Captures `X-Proxy-Ms` response header into `logData.proxyMs`
- JSONL output extended with `"proxy":{"proxy_ms":"..."}` object
- scriptVersion bumped to 18

### spellcheck-server.pyw
- `_load_api_key()` reads from `.env` file (handles comments, `export` prefix, quoted values)
- `--parent-pid` CLI argument parsing
- `_is_process_alive()` using Windows `OpenProcess` API (replaces unreliable `os.kill(pid, 0)`)
- Parent process check every 45s — server shuts down when AHK exits
- `log_config=None` on `uvicorn.run()` to prevent formatter crash

## Deviations

1. **Re-applied .env API key loading** — Plan 01 agent accidentally reverted the `.env` loading from commit 9aad25c. Restored the existing `LoadRequiredEnvValue()`/`ReadEnvValueFromFile()` functions.
2. **scriptVersion 18 instead of 17** — Version 17 was used in commit 9aad25c; bumped to 18 to avoid reuse.
3. **Parent-PID lifecycle tie (not in original plan)** — Added so server auto-shuts down when AHK exits, preventing orphan processes.
4. **Windows OpenProcess API (not in original plan)** — `os.kill(pid, 0)` returns `WinError 87` on Python 3.13 for alive processes. Replaced with `ctypes.windll.kernel32.OpenProcess`.
5. **uvicorn log_config=None (not in original plan)** — uvicorn's default logging config crashes with "Unable to configure formatter 'default'" on Windows.

## Verification

- Human-verified end-to-end: spell checking works through proxy
- Keep-alive pings authenticate with 200 OK (not 401)
- Parent-PID lifecycle holds — server stays alive while AHK runs, shuts down when AHK exits
- Proxy timing captured in JSONL logs
- No hardcoded API key in source
- Server stable for 16+ minutes with continuous 45s pings

## Self-Check: PASSED
