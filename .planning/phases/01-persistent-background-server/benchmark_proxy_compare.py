import json
import pathlib
import re
import statistics
import subprocess
import sys
import time

import httpx


ROOT_DIR = pathlib.Path(__file__).resolve().parents[3]
SERVER_SCRIPT = pathlib.Path(__file__).with_name("benchmark_proxy_server.py")
SCRIPT_PATH = ROOT_DIR / "Universal Spell Checker.ahk"
ENV_PATH = ROOT_DIR / ".env"
API_URL = "https://api.openai.com/v1/responses"
PROXY_URL = "http://127.0.0.1:48124/v1/responses"
HEALTH_URL = "http://127.0.0.1:48124/health"
PAIR_COUNT = 5
TIMEOUT = httpx.Timeout(connect=10.0, read=60.0, write=10.0, pool=10.0)


def parse_ahk_config():
    script = SCRIPT_PATH.read_text(encoding="utf-8")
    prompt_match = re.search(r'promptInstructionText := "([^"]+)"', script)
    model_match = re.search(r'modelModule := "([^"]+)"', script)
    if not (prompt_match and model_match):
        raise RuntimeError("Failed to parse benchmark config from AHK script.")

    api_key = load_api_key_from_env()
    if not api_key:
        key_match = re.search(r'apiKey := "([^"]+)"', script)
        api_key = key_match.group(1) if key_match else ""
    if not api_key:
        raise RuntimeError("Failed to find OPENAI_API_KEY in .env or AHK script.")

    model = model_match.group(1)
    config = {
        "api_key": api_key,
        "prompt_text": prompt_match.group(1),
        "model": model,
        "verbosity": "medium" if model == "gpt-4.1" else "low",
    }
    if model == "gpt-4.1":
        config["temperature"] = 0.3
    else:
        config["reasoning"] = {
            "effort": "minimal" if model == "gpt-5-mini" else "none",
            "summary": "auto",
        }
    return config


def load_api_key_from_env():
    if not ENV_PATH.exists():
        return ""
    for raw_line in ENV_PATH.read_text(encoding="utf-8").splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        if key == "OPENAI_API_KEY":
            return value.strip()
    return ""


def wait_for_health():
    deadline = time.time() + 15.0
    while time.time() < deadline:
        try:
            with httpx.Client(timeout=2.0) as client:
                resp = client.get(HEALTH_URL)
            if resp.status_code == 200:
                return
        except Exception:
            time.sleep(0.2)
    raise RuntimeError("Proxy server did not become healthy in time.")


def build_payload(config, tag):
    source_text = f"ths is a tset of teh spel chekcer benchmark {tag}"
    prompt = f"instructions: {config['prompt_text']}\ntext input: {source_text}"
    payload = {
        "model": config["model"],
        "input": [{"role": "user", "content": [{"type": "input_text", "text": prompt}]}],
        "store": True,
        "text": {"verbosity": config["verbosity"]},
    }
    if "temperature" in config:
        payload["temperature"] = config["temperature"]
    else:
        payload["reasoning"] = config["reasoning"]
    return payload


def run_direct(config, tag):
    payload = build_payload(config, tag)
    headers = {
        "authorization": f"Bearer {config['api_key']}",
        "content-type": "application/json; charset=utf-8",
    }
    started = time.perf_counter()
    with httpx.Client(http2=True, timeout=TIMEOUT) as client:
        resp = client.post(API_URL, headers=headers, json=payload)
    elapsed_ms = (time.perf_counter() - started) * 1000.0
    return {
        "arm": "direct",
        "tag": tag,
        "status": resp.status_code,
        "elapsed_ms": elapsed_ms,
    }


def run_proxy(config, tag):
    payload = build_payload(config, tag)
    headers = {
        "authorization": f"Bearer {config['api_key']}",
        "content-type": "application/json; charset=utf-8",
    }
    started = time.perf_counter()
    with httpx.Client(timeout=TIMEOUT) as client:
        resp = client.post(PROXY_URL, headers=headers, json=payload)
    elapsed_ms = (time.perf_counter() - started) * 1000.0
    return {
        "arm": "proxy",
        "tag": tag,
        "status": resp.status_code,
        "elapsed_ms": elapsed_ms,
        "proxy_ms": float(resp.headers.get("x-proxy-ms", "nan")),
        "connection_reused": resp.headers.get("x-connection-reused", ""),
    }


def summarize(results):
    direct = [r["elapsed_ms"] for r in results if r["arm"] == "direct" and r["status"] == 200]
    proxy = [r["elapsed_ms"] for r in results if r["arm"] == "proxy" and r["status"] == 200]
    if not direct or not proxy:
        raise RuntimeError("One benchmark arm had no successful requests.")
    return {
        "direct_mean_ms": round(statistics.mean(direct), 1),
        "direct_median_ms": round(statistics.median(direct), 1),
        "proxy_mean_ms": round(statistics.mean(proxy), 1),
        "proxy_median_ms": round(statistics.median(proxy), 1),
        "median_delta_ms": round(statistics.median(direct) - statistics.median(proxy), 1),
        "mean_delta_ms": round(statistics.mean(direct) - statistics.mean(proxy), 1),
        "direct_samples_ms": [round(x, 1) for x in direct],
        "proxy_samples_ms": [round(x, 1) for x in proxy],
    }


def main():
    config = parse_ahk_config()
    proc = subprocess.Popen([sys.executable, str(SERVER_SCRIPT)], cwd=str(ROOT_DIR))
    results = []
    try:
        wait_for_health()

        warm_direct = run_direct(config, "warm-direct")
        warm_proxy = run_proxy(config, "warm-proxy")
        print(
            f"WARMUP direct={warm_direct['status']}:{warm_direct['elapsed_ms']:.1f}ms "
            f"proxy={warm_proxy['status']}:{warm_proxy['elapsed_ms']:.1f}ms "
            f"proxy_upstream={warm_proxy['proxy_ms']:.1f}ms"
        )

        for idx in range(1, PAIR_COUNT + 1):
            order = [("direct", run_direct), ("proxy", run_proxy)]
            if idx % 2 == 0:
                order.reverse()
            for arm_name, fn in order:
                tag = f"pair-{idx}-{arm_name}"
                result = fn(config, tag)
                results.append(result)
                extra = ""
                if arm_name == "proxy":
                    extra = (
                        f" proxy_ms={result['proxy_ms']:.1f}ms"
                        f" reused={result['connection_reused']}"
                    )
                print(
                    f"{arm_name.upper()} {tag} status={result['status']}"
                    f" elapsed={result['elapsed_ms']:.1f}ms{extra}"
                )

        print("SUMMARY " + json.dumps(summarize(results)))
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()


if __name__ == "__main__":
    main()
