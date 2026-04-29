"""Extract length-stratified spellcheck inputs from JSONL logs into bench/inputs.json."""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path


def extract_inputs(log_files: list[Path], target_count: int = 10) -> list[str]:
    """Read JSONL logs, pull successful spellcheck inputs, return up to target_count
    deduped strings stratified by length (short/medium/long buckets)."""
    seen: set[str] = set()
    candidates: list[str] = []

    for log_path in log_files:
        try:
            text = log_path.read_text(encoding="utf-8")
        except OSError:
            continue
        for raw in text.splitlines():
            marker = " spellcheck_detail "
            idx = raw.find(marker)
            if idx < 0:
                continue
            json_part = raw[idx + len(marker):]
            try:
                payload = json.loads(json_part)
            except json.JSONDecodeError:
                continue
            if payload.get("status") != "success":
                continue
            input_text = payload.get("input_text") or ""
            if not input_text or input_text in seen:
                continue
            seen.add(input_text)
            candidates.append(input_text)

    return _stratify(candidates, target_count)


def _stratify(candidates: list[str], target_count: int) -> list[str]:
    """Spread the picks across short (<100ch), medium (100-1000ch), long (>1000ch)."""
    if len(candidates) <= target_count:
        return candidates

    short = [c for c in candidates if len(c) < 100]
    medium = [c for c in candidates if 100 <= len(c) <= 1000]
    long_ = [c for c in candidates if len(c) > 1000]

    per_bucket = max(1, target_count // 3)
    picked = short[:per_bucket] + medium[:per_bucket] + long_[:per_bucket]

    # Backfill from any bucket if we under-shot due to empty buckets.
    leftover = [c for c in candidates if c not in picked]
    while len(picked) < target_count and leftover:
        picked.append(leftover.pop(0))

    return picked[:target_count]


def _default_log_dir() -> Path:
    local_appdata = os.environ.get("LOCALAPPDATA")
    if local_appdata:
        return Path(local_appdata) / "UniversalSpellCheck" / "logs"
    return Path.home() / "AppData" / "Local" / "UniversalSpellCheck" / "logs"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Extract bench inputs from JSONL spellcheck logs.")
    parser.add_argument(
        "--log-dir",
        type=Path,
        default=_default_log_dir(),
        help="Directory containing spellcheck-*.jsonl files.",
    )
    parser.add_argument(
        "--out",
        type=Path,
        default=Path(__file__).parent / "inputs.json",
        help="Output JSON file path.",
    )
    parser.add_argument("--count", type=int, default=10)
    args = parser.parse_args(argv)

    log_files = sorted(args.log_dir.glob("spellcheck-*.jsonl"))
    if not log_files:
        print(f"No spellcheck-*.jsonl files found in {args.log_dir}", file=sys.stderr)
        return 1

    texts = extract_inputs(log_files, args.count)
    if not texts:
        print("No successful inputs found in logs.", file=sys.stderr)
        return 1

    entries = [{"id": f"{i:02d}", "text": t} for i, t in enumerate(texts, start=1)]
    args.out.write_text(json.dumps(entries, indent=2, ensure_ascii=False), encoding="utf-8")

    print(f"Wrote {len(entries)} input(s) to {args.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
