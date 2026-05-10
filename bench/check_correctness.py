"""Verify bench results against behavioral contracts.

Contracts are derived automatically from inputs.json + replacements.json:
  - URL passthrough: every https?:// URL in the input must appear byte-identical in the output.
  - Brand replacements: every replacements.json variant present in the input text (outside URLs)
    must appear as its canonical form in the output.

Inputs with no URLs and no matched variants are skipped — no assertion is possible for them.

Usage: python bench/check_correctness.py bench/results/<file>.json

Exit code 0 = all contracts satisfied; non-zero = at least one failed.
"""
import json
import re
import sys
from pathlib import Path

URL_RE = re.compile(r'https?://[^\s\]"\'<>)]+')


def load_json(p: Path):
    with open(p, "r", encoding="utf-8") as f:
        return json.load(f)


def strip_trailing_punct(url: str) -> str:
    return url.rstrip(".,;:)")


def extract_urls(text: str) -> list[str]:
    return [strip_trailing_punct(u) for u in URL_RE.findall(text)]


def build_variant_map(replacements: dict) -> dict[str, str]:
    """Flat map: variant_text -> canonical."""
    return {v: canonical for canonical, variants in replacements.items() for v in variants}


def find_variants(text: str, variant_map: dict[str, str]) -> dict[str, str]:
    """Return {variant: canonical} for all variants present in text (case-sensitive substring)."""
    return {v: c for v, c in variant_map.items() if v in text}


def main() -> int:
    if len(sys.argv) != 2:
        print("usage: check_correctness.py <results.json>", file=sys.stderr)
        return 2

    results_path = Path(sys.argv[1])
    if not results_path.exists():
        print(f"results file not found: {results_path}", file=sys.stderr)
        return 2

    bench_dir = Path(__file__).parent
    inputs_path = bench_dir / "inputs.json"
    replacements_path = bench_dir.parent / "replacements.json"

    for p in (inputs_path, replacements_path):
        if not p.exists():
            print(f"required file not found: {p}", file=sys.stderr)
            return 2

    results = load_json(results_path)
    inputs_by_id = {e["id"]: e["text"] for e in load_json(inputs_path)}
    variant_map = build_variant_map(load_json(replacements_path))

    failures: list[str] = []
    checked = 0
    skipped = 0

    for inp in results.get("inputs", []):
        name = inp["name"]
        original = inputs_by_id.get(name)
        if original is None:
            continue

        urls = extract_urls(original)
        text_no_urls = URL_RE.sub("", original)
        variants = find_variants(text_no_urls, variant_map)

        if not urls and not variants:
            skipped += 1
            continue

        sample = inp.get("sample_output")
        if sample is None:
            if inp.get("success_count", 0) > 0:
                failures.append(f"  [{name}] no sample_output captured (bench instrumentation issue)")
            else:
                failures.append(f"  [{name}] all trials failed — cannot verify correctness")
            continue

        for url in urls:
            if url not in sample:
                failures.append(f"  [{name}] URL not preserved: {url}")

        for variant, canonical in variants.items():
            if canonical not in sample:
                failures.append(f"  [{name}] replacement missing: '{variant}' → '{canonical}' not in output")

        checked += 1

    total = checked + skipped
    if failures:
        print(
            f"FAIL: {len(failures)} contract violation(s) across {checked} checked "
            f"input(s) ({skipped}/{total} skipped — no URL/replacement)"
        )
        for f in failures:
            print(f)
        return 1

    print(
        f"PASS: all contracts satisfied across {checked} checked "
        f"input(s) ({skipped}/{total} skipped — no URL/replacement)"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
