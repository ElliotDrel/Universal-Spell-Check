"""Compare end-to-end latency of two release builds on this machine.

    python bench/perf.py --base main.exe --candidate pr.exe [--base-root ../main] [--rounds 4]

Runs `--bench` (replay mode) for both builds in alternating order so machine noise hits both
equally, then compares the median keypress-to-paste time (`user_ms`) per case and overall.
Each build runs from its own checkout root (default: the current directory), so it is checked
against its own bench/replay.json and replacements.json. Prints a markdown table and exits 1
when the geometric mean of the per-case ratios shows the candidate slower by more than
--tolerance-ms AND --tolerance-pct (both must be exceeded, so tiny or noisy deltas pass).
"""

import argparse
import json
import statistics
import subprocess
import sys
import tempfile
from pathlib import Path


def bench(exe: Path, root: Path, runs: int, out: Path) -> list[dict]:
    # One retry absorbs the rare native paste no-op (docs/architecture.md); a repeat is a real failure.
    for attempt in (1, 2):
        folder = out.with_name(f"{out.name}-{attempt}")
        result = subprocess.run(
            [str(exe), "--bench", "--runs", str(runs), "--warmup", "1", "--output", str(folder)],
            cwd=root, capture_output=True, text=True, timeout=600,
        )
        summary = folder / "summary.json"
        if not summary.exists():
            sys.exit(f"{exe} --bench produced no summary:\n{result.stdout}\n{result.stderr}")
        data = json.loads(summary.read_text(encoding="utf-8"))
        if not data["failed"] and not data["error"]:
            return [t for t in data["trials"] if not t["warmup"]]
        print(f"{exe} --bench attempt {attempt} failed: {data['error'] or data['failures']}", file=sys.stderr)
    sys.exit(f"{exe} --bench failed twice; see {folder}")


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--base", type=Path, required=True)
    p.add_argument("--candidate", type=Path, required=True)
    p.add_argument("--base-root", type=Path, default=Path.cwd(), help="Checkout the base build came from")
    p.add_argument("--candidate-root", type=Path, default=Path.cwd())
    p.add_argument("--rounds", type=int, default=4)
    p.add_argument("--runs", type=int, default=5)
    p.add_argument("--tolerance-ms", type=float, default=3.0)
    p.add_argument("--tolerance-pct", type=float, default=5.0)
    p.add_argument("--markdown", type=Path, help="Also write the table to this file")
    a = p.parse_args()

    times: dict[str, dict[str, list[float]]] = {"base": {}, "candidate": {}}
    work = Path(tempfile.mkdtemp(prefix="usc-perf-"))
    for round_ in range(a.rounds):
        order = [("base", a.base, a.base_root), ("candidate", a.candidate, a.candidate_root)]
        for name, exe, root in order if round_ % 2 == 0 else order[::-1]:
            for t in bench(exe.resolve(), root.resolve(), a.runs, work / f"{name}-{round_}"):
                times[name].setdefault(t["case_id"], []).append(t["timings"]["user_ms"])

    def median(name: str, case: str | None = None) -> float:
        values = times[name][case] if case else [v for vs in times[name].values() for v in vs]
        return statistics.median(values)

    rows = ["| case | base ms | candidate ms | change |", "|---|---:|---:|---:|"]
    cases = sorted(set(times["base"]) & set(times["candidate"]))
    for case in cases + [None]:
        b, c = median("base", case), median("candidate", case)
        label = "**overall**" if case is None else case
        rows.append(f"| {label} | {b:.1f} | {c:.1f} | {c - b:+.1f} ms ({(c - b) / b * 100:+.1f}%) |")
    # Geometric mean of per-case ratios: one case doubling moves it about 7%, while noise in a
    # single case averages out. The ms threshold applies the same ratio to the overall median.
    ratio = statistics.geometric_mean([median("candidate", c) / median("base", c) for c in cases])
    delta_ms = (ratio - 1) * median("base")
    slower = delta_ms > a.tolerance_ms and (ratio - 1) * 100 > a.tolerance_pct
    rows += ["", f"Geometric mean of per-case ratios: {ratio:.3f} ({delta_ms:+.1f} ms at the overall median)."]
    verdict = (
        f"Latency regression; the threshold is {a.tolerance_ms} ms and {a.tolerance_pct}%."
        if slower else "No latency regression."
    )
    report = "\n".join(
        [f"Keypress-to-paste latency, replayed API, {a.rounds} alternating rounds x {a.runs} runs per case.", "", *rows, "", verdict]
    )
    print(report)
    if a.markdown:
        a.markdown.write_text(report + "\n", encoding="utf-8")
    sys.exit(1 if slower else 0)


if __name__ == "__main__":
    main()
