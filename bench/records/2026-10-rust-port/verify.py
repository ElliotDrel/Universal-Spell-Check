"""Verify the portable historical measurement record without running the app."""

import hashlib
import json
import math
import statistics
from pathlib import Path


def main():
    root = Path(__file__).resolve().parent
    load = lambda name: json.loads((root / name).read_text(encoding="utf-8"))
    for name, expected in load("checksums.json").items():
        assert hashlib.sha256((root / name).read_bytes()).hexdigest() == expected, name

    rows = [json.loads(line) for line in (root / "trials.jsonl").read_text(encoding="utf-8").splitlines()]
    summaries = load("summary.json")
    assert set(row["block"] for row in rows) == set(summaries)
    case_ids = {case["id"] for case in load("cases.json")}
    manifests = load("manifests.json")
    for block, expected in summaries.items():
        samples = [row for row in rows if row["block"] == block]
        measured = [row for row in samples if not row["warmup"]]
        warmups = [row for row in samples if row["warmup"]]
        successful = [row for row in measured if row["success"]]
        assert {row["case_id"] for row in measured} == case_ids, block
        assert len({(row["case_id"], row["index"], row["warmup"]) for row in samples}) == len(samples), block
        for case in case_ids:
            assert sum(row["case_id"] == case for row in measured) == manifests[block]["options"]["runs"], block
            assert sum(row["case_id"] == case for row in warmups) == manifests[block]["options"]["warmup"], block
        actual = {"attempted": len(measured), "succeeded": len(successful),
                  "failed": len(measured) - len(successful),
                  "quality_matches": sum(row["quality_match"] for row in measured),
                  "warmup_attempted": len(warmups),
                  "warmup_succeeded": sum(row["success"] for row in warmups)}
        for key, value in actual.items():
            assert value == expected[key], (block, key)
        values = sorted(row["timings"]["total_ms"] for row in successful)
        rank = .95 * (len(values) - 1)
        low, high = math.floor(rank), math.ceil(rank)
        stats = {"count": len(values), "median": statistics.median(values),
                 "mean": statistics.mean(values), "min": values[0], "max": values[-1],
                 "p95": values[low] + (values[high] - values[low]) * (rank - low)}
        for key, value in stats.items():
            assert math.isclose(value, expected["total_ms"][key], abs_tol=1e-6), (block, key)
    launches = sum(len(block["trials"]) for block in load("startup.json").values())
    assert launches == 22
    print(f"Verified {len(summaries)} blocks, {len(rows)} trials including warmups, and {launches} startup launches.")


if __name__ == "__main__":
    main()
