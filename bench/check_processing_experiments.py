"""Run all four offline processing arms against frozen compute and rich/native contracts.

Uses actual feature-built executable paths. No clipboard, GUI, API or settings.
Timings are component diagnostics; they cannot prove editor-visible speedups.
"""
import argparse
import json
import subprocess
from pathlib import Path

ARMS = ("baseline", "replacements-ac", "restore-splice", "rich-index")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exe", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--runs", type=int, default=10)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    exe = str(args.exe.resolve())
    rich_input = (root / "rust/tests/fixtures/csharp-formatting.jsonl").read_text(encoding="utf-8-sig")
    expected = [json.loads(line)["expected"] for line in rich_input.splitlines()]
    report = {}
    for arm in ARMS:
        compute = output / arm
        command = [exe, "--bench-compute", "--processing-arm", arm,
                   "--cases", str(root / "bench/records/2026-10-rust-port/cases.json"),
                   "--fixtures", str(root / "bench/records/2026-10-rust-port/fixtures.json"),
                   "--replacements", str(root / "replacements.json"),
                   "--runs", str(args.runs), "--warmup", "2", "--output", str(compute)]
        result = subprocess.run(command, cwd=root, capture_output=True, timeout=120)
        (output / (arm + ".stdout")).write_bytes(result.stdout)
        (output / (arm + ".stderr")).write_bytes(result.stderr)
        if result.returncode:
            raise RuntimeError(f"{arm}: compute failed; retained outputs in {output}")
        manifest = json.loads((compute / "manifest.json").read_text(encoding="utf-8"))
        if manifest.get("processing_arm") != arm:
            raise RuntimeError("Executable did not record requested experimental arm")
        rich = subprocess.run([exe, "--rich-json", "--processing-arm", arm],
                              input=rich_input, text=True, encoding="utf-8", capture_output=True,
                              cwd=root, timeout=120)
        (output / (arm + "-rich.jsonl")).write_text(rich.stdout, encoding="utf-8")
        rows = [json.loads(line) for line in rich.stdout.splitlines()]
        if rich.returncode or len(rows) != len(expected):
            raise RuntimeError(f"{arm}: rich protocol failed")
        for i, (actual, reference) in enumerate(zip(rows, expected)):
            if any(actual.get(key) != value for key, value in reference.items()):
                raise RuntimeError(f"{arm}: rich/native/reference mismatch at {i}")
        report[arm] = {"rich_cases": len(rows), "compute": json.loads((compute / "summary.json").read_text(encoding="utf-8"))["aggregate"]}
    (output / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({arm: {"rich_cases": value["rich_cases"], "compute_succeeded": value["compute"]["succeeded"], "compute_failed": value["compute"]["failed"]} for arm, value in report.items()}))


if __name__ == "__main__":
    main()
