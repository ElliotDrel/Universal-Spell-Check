"""Diff two bench result JSON files — aggregate median per phase across all inputs."""

from __future__ import annotations

import argparse
import json
import statistics
from pathlib import Path

PHASES = [
    "total_ms",
    "request_ms",
    "post_process_ms",
    "after_copy_format_ms",
    "before_paste_format_ms",
    "capture_ms",
    "paste_ms",
]
PRIMARY = "request_ms"


def aggregate_median(results: dict, phase: str) -> float:
    medians = [
        float(inp.get(phase, {}).get("median", 0) or 0)
        for inp in results.get("inputs", [])
    ]
    values = [v for v in medians if v > 0]
    return statistics.median(values) if values else 0.0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Diff two bench result JSON files.")
    parser.add_argument("before", type=Path)
    parser.add_argument("after", type=Path)
    args = parser.parse_args(argv)

    before = json.loads(args.before.read_text(encoding="utf-8"))
    after = json.loads(args.after.read_text(encoding="utf-8"))

    header = f"{'phase':<22} {'before_ms':>10} {'after_ms':>10} {'delta_ms':>10} {'delta_%':>8}"
    print(header)
    print("-" * len(header))

    for phase in PHASES:
        b = aggregate_median(before, phase)
        a = aggregate_median(after, phase)
        delta = a - b
        pct = (delta / b * 100.0) if b > 0 else 0.0
        marker = ""
        if phase == PRIMARY:
            if pct <= -5:
                marker = " ✓"
            elif pct >= 5:
                marker = " ✗"
        print(f"{phase:<22} {b:>10.1f} {a:>10.1f} {delta:>10.1f} {pct:>7.1f}%{marker}")

    # Summary verdict for the primary metric
    b_req = aggregate_median(before, PRIMARY)
    a_req = aggregate_median(after, PRIMARY)
    pct_req = ((a_req - b_req) / b_req * 100.0) if b_req > 0 else 0.0
    verdict = "IMPROVED ✓" if pct_req <= -5 else ("REGRESSED ✗" if pct_req >= 5 else "NO CHANGE")
    print(f"\n{PRIMARY}: {verdict} ({pct_req:+.1f}%)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
