"""Run staged-capture candidates and injected failures without desktop or network access."""
import argparse
import ctypes
import json
from pathlib import Path
import subprocess
import tempfile


def assert_reaped(pid):
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.OpenProcess.restype = ctypes.c_void_p
    kernel.OpenProcess.argtypes = [ctypes.c_ulong, ctypes.c_int, ctypes.c_ulong]
    kernel.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
    kernel.CloseHandle.argtypes = [ctypes.c_void_p]
    handle = kernel.OpenProcess(0x00100000, False, pid)
    if handle:
        try:
            if kernel.WaitForSingleObject(handle, 0) != 0:
                raise RuntimeError(f"Helper {pid} remains running")
        finally:
            kernel.CloseHandle(handle)
    elif ctypes.get_last_error() != 87:
        raise RuntimeError(f"Cannot verify helper {pid}: {ctypes.get_last_error()}")


def record(child, name, trials):
    row = {"case": name, "exit_code": child.returncode, "stderr": child.stderr,
           "stdout": child.stdout}
    try:
        row.update(json.loads(child.stdout))
    except (ValueError, TypeError) as error:
        row["parse_error"] = str(error)
        trials.append(row)
        raise RuntimeError(f"Invalid candidate result for {name}") from error
    trials.append(row)
    return row


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    exe = str(Path(args.exe).resolve())
    output = Path(args.output)
    output.mkdir(parents=True, exist_ok=False)
    fixture = json.loads((Path(__file__).parent / "fixtures/staged-capture.json").read_text())
    trials = []
    try:
        with tempfile.TemporaryDirectory(prefix="usc-staged-") as temporary:
            path = Path(temporary) / "fixture.json"
            for variant in ["unicode-first", "owned-prepare", "helper"]:
                for name, updates in [
                    ("response-first", {"rich_delay_ms": 40, "request_delay_ms": 0}),
                    ("rich-first", {"rich_delay_ms": 0, "request_delay_ms": 40}),
                    ("rich-failure", {"fail_rich": True}),
                    ("stale-snapshot", {"stale": True}),
                    ("capture-panic", {"panic_capture": True}),
                ]:
                    value = dict(fixture, **updates)
                    path.write_text(json.dumps(value), encoding="utf-8")
                    child = subprocess.run([exe, "--bench-staged-capture", "--variant", variant,
                                            "--fixture", str(path)], capture_output=True,
                                           text=True, encoding="utf-8", timeout=12)
                    row = record(child, name, trials)
                    failure = name in {"rich-failure", "stale-snapshot", "capture-panic"}
                    if bool(child.returncode) != failure:
                        raise RuntimeError(f"Unexpected status: {variant}/{name}")
                    expected_calls = 0 if failure and variant == "owned-prepare" else 1
                    if row["request_count"] != expected_calls:
                        raise RuntimeError(f"Request count mismatch: {variant}/{name}")
                    if not failure:
                        events = [event["event"] for event in row["trace"]]
                        if events[-1] != "both_gates_ready":
                            raise RuntimeError("Missing two-gate completion")
                        for gate in ["snapshot_ready", "response_ready", "rich_source_ready"]:
                            if events.index(gate) >= events.index("both_gates_ready"):
                                raise RuntimeError(f"Gate order mismatch: {gate}")
                        if row["rich"]["text"] != "This is a test.":
                            raise RuntimeError("Unexpected corrected text")
                    if variant == "helper":
                        if not row["helper_reaped"] or not row["helper_pid"]:
                            raise RuntimeError("Helper cleanup did not complete")
                        assert_reaped(row["helper_pid"])
            value = dict(fixture, helper_hang_ms=6000)
            path.write_text(json.dumps(value), encoding="utf-8")
            child = subprocess.run([exe, "--bench-staged-capture", "--variant", "helper",
                                    "--fixture", str(path)], capture_output=True,
                                   text=True, encoding="utf-8", timeout=12)
            row = record(child, "helper-timeout", trials)
            if not child.returncode or row["request_count"] != 0 or not row["helper_reaped"]:
                raise RuntimeError("Helper timeout did not fail closed and reap child")
            assert_reaped(row["helper_pid"])
    finally:
        (output / "trials.json").write_text(json.dumps(trials, indent=2), encoding="utf-8")
    (output / "summary.json").write_text(json.dumps({"passed": len(trials),
        "scope": "component replay and same-exe IPC; no clipboard/editor or speed claim"}, indent=2))
    print(f"Passed {len(trials)} staged candidate and failure checks")


if __name__ == "__main__":
    main()
