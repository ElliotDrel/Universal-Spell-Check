"""Diff two bench result JSON files into a phase-level delta table."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

PHASES = ["total_ms", "coordinator_total_ms", "capture_ms", "request_ms", "post_process_ms", "paste_ms"]


def build_delta_rows(before_path: Path, after_path: Path) -> list[dict]:
    before = json.loads(Path(before_path).read_text(encoding="utf-8"))
    after = json.loads(Path(after_path).read_text(encoding="utf-8"))

    after_by_name = {i["name"]: i for i in after.get("inputs", [])}

    rows: list[dict] = []
    for b in before.get("inputs", []):
        a = after_by_name.get(b["name"])
        if a is None:
            continue
        for phase in PHASES:
            b_med = float(b.get(phase, {}).get("median", 0) or 0)
            a_med = float(a.get(phase, {}).get("median", 0) or 0)
            delta = a_med - b_med
            pct = (delta / b_med * 100.0) if b_med > 0 else 0.0
            rows.append({
                "input": b["name"],
                "phase": phase,
                "before_median": b_med,
                "after_median": a_med,
                "delta_ms": delta,
                "delta_pct": pct,
            })
    after_only = sorted(set(after_by_name.keys()) - {b["name"] for b in before.get("inputs", [])})
    if after_only:
        # Surface as a note row rather than data rows
        for name in after_only:
            rows.append({
                "input": name,
                "phase": "(new input)",
                "before_median": 0.0,
                "after_median": 0.0,
                "delta_ms": 0.0,
                "delta_pct": 0.0,
            })
    return rows


def format_delta_table(rows: list[dict]) -> str:
    if not rows:
        return "(no comparable inputs)"
    header = f"{'input':<6} {'phase':<22} {'before_ms':>10} {'after_ms':>10} {'delta_ms':>10} {'delta_%':>8}"
    lines = [header, "-" * len(header)]
    for r in rows:
        marker = ""
        if r["delta_pct"] <= -5:
            marker = " ✓"
        elif r["delta_pct"] >= 5:
            marker = " ✗"
        lines.append(
            f"{r['input']:<6} {r['phase']:<22} "
            f"{r['before_median']:>10.1f} {r['after_median']:>10.1f} "
            f"{r['delta_ms']:>10.1f} {r['delta_pct']:>7.1f}%{marker}"
        )
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Diff two bench result JSON files.")
    parser.add_argument("before", type=Path)
    parser.add_argument("after", type=Path)
    args = parser.parse_args(argv)

    rows = build_delta_rows(args.before, args.after)
    print(format_delta_table(rows))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
