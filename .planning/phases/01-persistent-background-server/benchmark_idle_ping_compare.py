import json
import os
import pathlib
import subprocess
import sys
import time

import httpx


ROOT_DIR = pathlib.Path(__file__).resolve().parents[3]
SERVER_SCRIPT = pathlib.Path(__file__).with_name("benchmark_proxy_server.py")
ENV_PATH = ROOT_DIR / ".env"
PROXY_PORT = 48124
HEALTH_URL = f"http://127.0.0.1:{PROXY_PORT}/health"
MODELS_URL = f"http://127.0.0.1:{PROXY_PORT}/v1/models"
IDLE_GAPS = [30, 60, 90, 180]
TIMEOUT = httpx.Timeout(connect=10.0, read=60.0, write=10.0, pool=10.0)


def load_api_key():
    if not ENV_PATH.exists():
        raise RuntimeError(".env file missing; OPENAI_API_KEY required.")
    for raw_line in ENV_PATH.read_text(encoding="utf-8").splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        if key == "OPENAI_API_KEY" and value.strip():
            return value.strip()
    raise RuntimeError("OPENAI_API_KEY missing from .env.")


def wait_for_health():
    deadline = time.time() + 15.0
    while time.time() < deadline:
        try:
            with httpx.Client(timeout=2.0) as client:
                resp = client.get(HEALTH_URL)
            if resp.status_code == 200:
                return resp.json()
        except Exception:
            time.sleep(0.2)
    raise RuntimeError("Proxy server did not become healthy in time.")


def proxy_get_models(headers):
    started = time.perf_counter()
    with httpx.Client(timeout=TIMEOUT) as client:
        resp = client.get(MODELS_URL, headers=headers)
    elapsed_ms = (time.perf_counter() - started) * 1000.0
    return {
        "status": resp.status_code,
        "elapsed_ms": elapsed_ms,
        "proxy_ms": float(resp.headers.get("x-proxy-ms", "nan")),
        "connection_reused": resp.headers.get("x-connection-reused", ""),
        "ping_enabled": resp.headers.get("x-ping-enabled", ""),
        "ping_count": int(resp.headers.get("x-ping-count", "0")),
    }


def run_trial(gap_seconds, ping_enabled, headers):
    env = dict(os.environ)
    env["PROXY_TEST_PORT"] = str(PROXY_PORT)
    env["ENABLE_KEEPALIVE_PING"] = "1" if ping_enabled else "0"
    env["KEEPALIVE_PING_INTERVAL"] = "45"
    proc = subprocess.Popen([sys.executable, str(SERVER_SCRIPT)], cwd=str(ROOT_DIR), env=env)
    try:
        health = wait_for_health()
        warm_1 = proxy_get_models(headers)
        warm_2 = proxy_get_models(headers)
        time.sleep(gap_seconds)
        probe_1 = proxy_get_models(headers)
        probe_2 = proxy_get_models(headers)
        return {
            "gap_seconds": gap_seconds,
            "ping_enabled": ping_enabled,
            "health": health,
            "warm_1": warm_1,
            "warm_2": warm_2,
            "probe_1": probe_1,
            "probe_2": probe_2,
        }
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()


def main():
    api_key = load_api_key()
    headers = {"authorization": f"Bearer {api_key}"}
    results = []
    for ping_enabled in (False, True):
        mode_name = "ping" if ping_enabled else "no-ping"
        for gap in IDLE_GAPS:
            result = run_trial(gap, ping_enabled, headers)
            results.append(result)
            warm_ms = result["warm_2"]["proxy_ms"]
            probe_ms = result["probe_1"]["proxy_ms"]
            follow_ms = result["probe_2"]["proxy_ms"]
            ping_count = result["probe_1"]["ping_count"]
            print(
                f"{mode_name.upper()} gap={gap}s "
                f"warm={warm_ms:.1f}ms "
                f"probe1={probe_ms:.1f}ms "
                f"probe2={follow_ms:.1f}ms "
                f"delta_vs_warm={probe_ms - warm_ms:.1f}ms "
                f"ping_count={ping_count}"
            )
    print("SUMMARY " + json.dumps(results))


if __name__ == "__main__":
    main()
