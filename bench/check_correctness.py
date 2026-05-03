"""Verify bench results against bench/correctness.json behavioral assertions.

Usage: python bench/check_correctness.py bench/results/<file>.json

Exit code 0 = all assertions passed; non-zero = at least one failed.
Prints a per-input pass/fail report. Designed to be called from the autoopt
loop as a hard correctness gate before any change is committed.
"""
import json
import sys
from pathlib import Path


def load_json(p):
    with open(p, "r", encoding="utf-8") as f:
        return json.load(f)


def main():
    if len(sys.argv) != 2:
        print("usage: check_correctness.py <results.json>", file=sys.stderr)
        return 2

    results_path = Path(sys.argv[1])
    if not results_path.exists():
        print(f"results file not found: {results_path}", file=sys.stderr)
        return 2

    correctness_path = Path(__file__).parent / "correctness.json"
    if not correctness_path.exists():
        print(f"correctness.json not found: {correctness_path}", file=sys.stderr)
        return 2

    results = load_json(results_path)
    assertions = {a["id"]: a for a in load_json(correctness_path)}

    failures = []
    checked = 0

    for inp in results.get("inputs", []):
        name = inp["name"]
        if name not in assertions:
            failures.append(f"  [{name}] no assertion entry in correctness.json")
            continue

        sample = inp.get("sample_output")
        if sample is None:
            if inp.get("success_count", 0) > 0:
                failures.append(f"  [{name}] no sample_output captured (bench instrumentation issue)")
            else:
                failures.append(f"  [{name}] all trials failed; cannot verify correctness")
            continue

        a = assertions[name]
        for s in a.get("must_contain", []):
            if s not in sample:
                failures.append(f"  [{name}] must_contain '{s}' missing from output")
        for s in a.get("must_contain_exact", []):
            if s not in sample:
                failures.append(f"  [{name}] must_contain_exact '{s}' missing from output")
        if a.get("must_equal_input"):
            # Look up original input text by reading inputs.json
            inputs_path = Path(__file__).parent / "inputs.json"
            originals = {e["id"]: e["text"] for e in load_json(inputs_path)}
            if sample != originals.get(name):
                failures.append(f"  [{name}] must_equal_input: output differs from input")
        checked += 1

    if failures:
        print(f"FAIL: {len(failures)} assertion(s) failed across {checked} input(s)")
        for f in failures:
            print(f)
        return 1

    print(f"PASS: all assertions satisfied across {checked} input(s)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
