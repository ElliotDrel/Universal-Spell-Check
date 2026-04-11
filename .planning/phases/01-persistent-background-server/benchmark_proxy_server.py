import os
import pathlib
import time
import asyncio
from contextlib import asynccontextmanager

import httpx
import uvicorn
from starlette.applications import Starlette
from starlette.requests import Request
from starlette.responses import JSONResponse, Response
from starlette.routing import Route


LISTEN_HOST = "127.0.0.1"
LISTEN_PORT = int(os.environ.get("PROXY_TEST_PORT", "48124"))
OPENAI_BASE_URL = "https://api.openai.com"
OPENAI_API_URL = OPENAI_BASE_URL + "/v1/responses"
OPENAI_MODELS_URL = OPENAI_BASE_URL + "/v1/models"
ROOT_DIR = pathlib.Path(__file__).resolve().parents[3]
ENV_PATH = ROOT_DIR / ".env"
ENABLE_KEEPALIVE_PING = os.environ.get("ENABLE_KEEPALIVE_PING", "0") == "1"
KEEPALIVE_PING_INTERVAL = float(os.environ.get("KEEPALIVE_PING_INTERVAL", "45"))

http_client = None
request_count = 0
ping_count = 0
keepalive_task = None


def load_env_file():
    if not ENV_PATH.exists():
        return
    for raw_line in ENV_PATH.read_text(encoding="utf-8").splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        if key and key not in os.environ:
            os.environ[key] = value


async def startup():
    global http_client
    global keepalive_task
    load_env_file()
    http_client = httpx.AsyncClient(
        http2=True,
        limits=httpx.Limits(
            max_keepalive_connections=5,
            max_connections=10,
            keepalive_expiry=120.0,
        ),
        timeout=httpx.Timeout(connect=10.0, read=60.0, write=10.0, pool=10.0),
    )
    if ENABLE_KEEPALIVE_PING:
        keepalive_task = asyncio.create_task(keepalive_ping_loop())


async def shutdown():
    global http_client
    global keepalive_task
    if keepalive_task is not None:
        keepalive_task.cancel()
        try:
            await keepalive_task
        except asyncio.CancelledError:
            pass
    if http_client is not None:
        await http_client.aclose()


@asynccontextmanager
async def lifespan(app):
    await startup()
    try:
        yield
    finally:
        await shutdown()


async def health(request: Request):
    return JSONResponse(
        {
            "status": "ok",
            "ping_enabled": ENABLE_KEEPALIVE_PING,
            "ping_interval": KEEPALIVE_PING_INTERVAL,
            "ping_count": ping_count,
        }
    )


async def keepalive_ping_loop():
    global ping_count
    auth_header = os.environ.get("OPENAI_API_KEY", "")
    if auth_header:
        auth_header = "Bearer " + auth_header
    headers = {}
    if auth_header:
        headers["authorization"] = auth_header
    while True:
        await asyncio.sleep(KEEPALIVE_PING_INTERVAL)
        try:
            await http_client.get(OPENAI_MODELS_URL, headers=headers)
            ping_count += 1
        except Exception:
            pass


def build_proxy_headers(elapsed_ms):
    return {
        "x-proxy-ms": str(elapsed_ms),
        "x-connection-reused": "true" if request_count > 1 else "false",
        "x-ping-enabled": "true" if ENABLE_KEEPALIVE_PING else "false",
        "x-ping-count": str(ping_count),
    }


async def proxy_models(request: Request):
    global request_count
    auth = request.headers.get("authorization", "")
    started = time.perf_counter()
    try:
        resp = await http_client.get(
            OPENAI_MODELS_URL,
            headers={"authorization": auth} if auth else {},
        )
    except Exception as exc:
        elapsed_ms = round((time.perf_counter() - started) * 1000.0, 1)
        return JSONResponse(
            {"error": type(exc).__name__, "detail": str(exc)},
            status_code=502,
            headers=build_proxy_headers(elapsed_ms),
        )

    request_count += 1
    elapsed_ms = round((time.perf_counter() - started) * 1000.0, 1)
    return Response(
        content=resp.content,
        status_code=resp.status_code,
        media_type=resp.headers.get("content-type", "application/json"),
        headers=build_proxy_headers(elapsed_ms),
    )


async def proxy_request(request: Request):
    global request_count
    body = await request.body()
    auth = request.headers.get("authorization", "")
    started = time.perf_counter()
    try:
        resp = await http_client.post(
            OPENAI_API_URL,
            content=body,
            headers={
                "content-type": "application/json; charset=utf-8",
                "authorization": auth,
            },
        )
    except Exception as exc:
        elapsed_ms = round((time.perf_counter() - started) * 1000.0, 1)
        return JSONResponse(
            {"error": type(exc).__name__, "detail": str(exc)},
            status_code=502,
            headers=build_proxy_headers(elapsed_ms),
        )

    request_count += 1
    elapsed_ms = round((time.perf_counter() - started) * 1000.0, 1)
    return Response(
        content=resp.content,
        status_code=resp.status_code,
        media_type=resp.headers.get("content-type", "application/json"),
        headers=build_proxy_headers(elapsed_ms),
    )


app = Starlette(
    routes=[
        Route("/health", health, methods=["GET"]),
        Route("/v1/models", proxy_models, methods=["GET"]),
        Route("/v1/responses", proxy_request, methods=["POST"]),
    ],
    lifespan=lifespan,
)


if __name__ == "__main__":
    uvicorn.run(app, host=LISTEN_HOST, port=LISTEN_PORT, log_level="warning")
