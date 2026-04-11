"""Transparent proxy server with persistent connection pooling to OpenAI API.

Maintains a warm httpx connection pool so the AHK spell-checker avoids
per-invocation TLS/TCP handshake overhead (~40-100ms savings).
"""

import asyncio
from contextlib import asynccontextmanager
import ctypes
import logging
import os
import sys
import time

import httpx
import uvicorn
from starlette.applications import Starlette
from starlette.requests import Request
from starlette.responses import Response, JSONResponse
from starlette.routing import Route

# ---------------------------------------------------------------------------
# Configuration
# ---------------------------------------------------------------------------

LISTEN_HOST = "127.0.0.1"
LISTEN_PORT = 48080
PARENT_PID = None  # Set via --parent-pid; server exits when parent dies
OPENAI_BASE_URL = "https://api.openai.com"
KEEPALIVE_EXPIRY = 120.0          # seconds -- keep idle connections warm (D-07)
KEEPALIVE_PING_INTERVAL = 45      # seconds between keep-alive pings (D-08)

_script_dir = os.path.dirname(os.path.abspath(__file__))
PID_FILE = os.path.join(_script_dir, "logs", "server.pid")
LOG_FILE = os.path.join(_script_dir, "logs", "server.log")
_ENV_FILE = os.path.join(_script_dir, ".env")


def _load_api_key() -> str:
    """Load OPENAI_API_KEY from .env file, falling back to environment variable."""
    if os.path.exists(_ENV_FILE):
        with open(_ENV_FILE, "r") as f:
            for line in f:
                line = line.strip()
                if not line or line.startswith("#"):
                    continue
                if line.startswith("export "):
                    line = line[7:].lstrip()
                if line.startswith("OPENAI_API_KEY="):
                    value = line.split("=", 1)[1].strip()
                    if len(value) >= 2 and value[0] == value[-1] and value[0] in ('"', "'"):
                        value = value[1:-1]
                    return value
    return os.environ.get("OPENAI_API_KEY", "")

# ---------------------------------------------------------------------------
# Logging -- critical because pythonw.exe swallows all stdout/stderr (D-11)
# ---------------------------------------------------------------------------

os.makedirs(os.path.dirname(LOG_FILE), exist_ok=True)
logging.basicConfig(
    filename=LOG_FILE,
    level=logging.INFO,
    format="%(asctime)s %(levelname)s %(message)s",
)
log = logging.getLogger("spellcheck-server")

# ---------------------------------------------------------------------------
# Global state
# ---------------------------------------------------------------------------

http_client: httpx.AsyncClient | None = None
keepalive_task: asyncio.Task | None = None

# ---------------------------------------------------------------------------
# PID file management (D-05)
# ---------------------------------------------------------------------------

def _is_process_alive(pid: int) -> bool:
    """Check if a process is alive using Windows API (os.kill unreliable on Win)."""
    kernel32 = ctypes.windll.kernel32
    handle = kernel32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
    if handle:
        kernel32.CloseHandle(handle)
        return True
    return False


def write_pid_file():
    """Write current PID to file. Exit if another instance is alive."""
    if os.path.exists(PID_FILE):
        try:
            with open(PID_FILE, "r") as f:
                existing_pid = int(f.read().strip())
            if _is_process_alive(existing_pid):
                log.warning(
                    "Another instance is running (PID %d). Exiting.", existing_pid
                )
                sys.exit(1)
        except ValueError:
            # PID file is corrupt -- stale, overwrite
            pass

    with open(PID_FILE, "w") as f:
        f.write(str(os.getpid()))


def remove_pid_file():
    """Remove PID file on shutdown."""
    try:
        os.remove(PID_FILE)
    except OSError:
        pass

# ---------------------------------------------------------------------------
# Keep-alive ping loop (D-08)
# ---------------------------------------------------------------------------

async def keepalive_ping_loop():
    """Ping OpenAI every KEEPALIVE_PING_INTERVAL seconds to keep connections warm."""
    api_key = _load_api_key()
    auth_header = "Bearer " + api_key
    target = OPENAI_BASE_URL + "/v1/models"

    while True:
        await asyncio.sleep(KEEPALIVE_PING_INTERVAL)

        # Check if parent AHK process is still alive
        if PARENT_PID is not None and not _is_process_alive(PARENT_PID):
            log.info("Parent process (PID %d) is gone. Shutting down.", PARENT_PID)
            logging.shutdown()
            remove_pid_file()
            os._exit(0)

        try:
            await http_client.get(target, headers={"authorization": auth_header})
        except Exception as exc:
            log.warning("Keep-alive ping failed: %s", exc)

# ---------------------------------------------------------------------------
# Lifecycle (Starlette 1.0 lifespan context manager)
# ---------------------------------------------------------------------------

@asynccontextmanager
async def lifespan(app):
    """Initialize connection pool, PID file, and keep-alive task on startup;
    tear down on shutdown."""
    global http_client, keepalive_task

    # --- Startup ---
    write_pid_file()

    http_client = httpx.AsyncClient(
        http2=True,
        limits=httpx.Limits(
            max_keepalive_connections=5,
            max_connections=10,
            keepalive_expiry=KEEPALIVE_EXPIRY,
        ),
        timeout=httpx.Timeout(
            connect=5.0,
            read=35.0,
            write=5.0,
            pool=5.0,
        ),
    )

    keepalive_task = asyncio.create_task(keepalive_ping_loop())

    log.info(
        "Server started on %s:%d (PID %d)", LISTEN_HOST, LISTEN_PORT, os.getpid()
    )

    yield

    # --- Shutdown ---
    if keepalive_task is not None:
        keepalive_task.cancel()
        try:
            await keepalive_task
        except asyncio.CancelledError:
            pass

    if http_client is not None:
        await http_client.aclose()

    remove_pid_file()
    log.info("Server shut down")

# ---------------------------------------------------------------------------
# Endpoints
# ---------------------------------------------------------------------------

async def proxy_request(request: Request) -> Response:
    """Forward POST /v1/responses to OpenAI with warm connection pool."""
    body = await request.body()
    auth = request.headers.get("authorization", "")
    target_url = OPENAI_BASE_URL + request.url.path

    start = time.perf_counter()
    try:
        resp = await http_client.post(
            target_url,
            content=body,
            headers={
                "content-type": "application/json; charset=utf-8",
                "authorization": auth,
            },
        )
        proxy_ms = round((time.perf_counter() - start) * 1000, 1)

        response_headers = {
            "content-type": resp.headers.get("content-type", "application/json"),
            "x-proxy-ms": str(proxy_ms),
        }

        return Response(
            content=resp.content,
            status_code=resp.status_code,
            headers=response_headers,
        )

    except httpx.ConnectError as exc:
        proxy_ms = round((time.perf_counter() - start) * 1000, 1)
        log.error("Upstream connection failed: %s", exc)
        return JSONResponse(
            {"error": "upstream_connect_failed", "detail": str(exc)},
            status_code=502,
            headers={"x-proxy-ms": str(proxy_ms)},
        )

    except httpx.TimeoutException as exc:
        proxy_ms = round((time.perf_counter() - start) * 1000, 1)
        log.error("Upstream timeout: %s", exc)
        return JSONResponse(
            {"error": "upstream_timeout", "detail": str(exc)},
            status_code=504,
            headers={"x-proxy-ms": str(proxy_ms)},
        )


async def health(request: Request) -> Response:
    """Health check endpoint for AHK to verify server is running."""
    return JSONResponse({"status": "ok"})

# ---------------------------------------------------------------------------
# App assembly
# ---------------------------------------------------------------------------

app = Starlette(
    routes=[
        Route("/v1/responses", proxy_request, methods=["POST"]),
        Route("/health", health, methods=["GET"]),
    ],
    lifespan=lifespan,
)

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

if __name__ == "__main__":
    # Parse --parent-pid argument
    for i, arg in enumerate(sys.argv[1:], 1):
        if arg == "--parent-pid" and i < len(sys.argv) - 1:
            try:
                PARENT_PID = int(sys.argv[i + 1])
            except ValueError:
                pass
            break

    try:
        uvicorn.run(app, host=LISTEN_HOST, port=LISTEN_PORT, log_level="warning", log_config=None)
    except Exception as e:
        log.critical("Server failed to start: %s", e)
        remove_pid_file()
