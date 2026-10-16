"""Compare two v2 baseline directories; reject mismatched workloads/settings."""

import argparse
import json
from pathlib import Path


def load(directory):
    directory = Path(directory)
    return tuple(json.loads((directory / name).read_text(encoding="utf-8"))
                 for name in ("manifest.json", "summary.json"))


def validate(before, after):
    keys = ("schema_version", "cases_sha256", "replacements_sha256", "fixture_sha256", "model", "prompt", "developer_logging", "machine_name", "case_ids")
    differences = [key for key in keys if before.get(key) != after.get(key)]
    for key in ("mode", "runs", "warmup"):
        if before["options"].get(key) != after["options"].get(key):
            differences.append("options." + key)
    if before.get("schema_version") != 2 or after.get("schema_version") != 2:
        differences.append("unsupported schema")
    if differences:
        raise ValueError("Incomparable runs: " + ", ".join(differences))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("before")
    parser.add_argument("after")
    args = parser.parse_args()
    before_m, before = load(args.before)
    after_m, after = load(args.after)
    validate(before_m, after_m)
    for manifest, result in ((before_m, before), (after_m, after)):
        if set(result["cases"]) != set(manifest["case_ids"]) or any(
                group["attempted"] != manifest["options"]["runs"] for group in result["cases"].values()):
            raise ValueError("Incomplete measured case coverage")
    for label, result in (("before", before), ("after", after)):
        group = result["aggregate"]
        print(f"{label}: {group['succeeded']}/{group['attempted']} successful; "
              f"{group['quality_matches']} exact expected-text matches; "
              f"{result['warmup']['failed']} failed warmups")
    if set(before["cases"]) != set(after["cases"]):
        raise ValueError("Incomparable case coverage")
    if any(result["aggregate"]["failed"] or result["warmup"]["failed"] for result in (before, after)):
        raise ValueError("At least one run failed; speed alone is not a passing comparison")
    print("\nScope / phase / statistic: before ms -> after ms; saved ms; reduction; speed ratio")
    groups = [("all", before["aggregate"], after["aggregate"])]
    groups += [(name, before["cases"][name], after["cases"][name]) for name in before["cases"]]
    for name, b, a in groups:
        for phase in sorted(set(b["timings"]) & set(a["timings"])):
            for stat in ("median", "p95"):
                old, new = b["timings"][phase][stat], a["timings"][phase][stat]
                if old is None or new is None:
                    continue
                percent = f"{100 * (old - new) / old:+.1f}%" if old else "n/a"
                ratio = f"{old / new:.2f}x" if new else "n/a"
                print(f"{name}/{phase}/{stat}: {old:.3f} -> {new:.3f}; {old-new:+.3f}; {percent}; {ratio}")
    print("\nObserved differences, not a statistical significance test. Inspect order effects and repeat matched blocks.")


if __name__ == "__main__":
    main()
