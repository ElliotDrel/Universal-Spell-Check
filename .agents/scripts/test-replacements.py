#!/usr/bin/env python3
"""
test-replacements.py — Dry-run input text through the current replacements.json.

Python reimplementation of TextPostProcessor.ApplyReplacements. Matches the C# behavior:
  - BOM-stripped JSON, canonical → variants[]
  - Variants where canonical.Contains(variant) are rejected at load time (same guard as C#)
  - Pairs sorted longest-first
  - Left-to-right single-pass scan; output is never re-scanned (no cascading)
  - URLs (https?://...) are protected via __URL_N__ placeholders

Usage:
  python .agents/scripts/test-replacements.py "Burton Morgan Competition - Fall 2025"
  python .agents/scripts/test-replacements.py --run <ISO8601-timestamp>
  python .agents/scripts/test-replacements.py --replacements /path/to/replacements.json "some text"
"""
import argparse
import json
import os
import re
import sys
from pathlib import Path

URL_RE = re.compile(r"https?://\S+")

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
DEFAULT_REPLACEMENTS = REPO_ROOT / "replacements.json"
LOG_DIR = Path(os.environ.get("LOCALAPPDATA", "")) / "UniversalSpellCheck" / "logs"
LINE_RE = re.compile(
    r"^(?P<ts>\S+)\s+channel=\S+\s+app_version=\S+\s+pid=\d+\s+(?P<event>\S+)(?:\s+(?P<rest>.*))?$"
)


def load_pairs(replacements_path):
    """Load and validate (variant, canonical) pairs from replacements.json."""
    with open(replacements_path, encoding="utf-8-sig") as f:
        data = json.load(f)

    pairs = []
    skipped = []
    for canonical, variants in data.items():
        for variant in variants:
            if not variant or variant == canonical:
                continue
            if variant in canonical:  # mirrors canonical.Contains(variant) in C#
                skipped.append((variant, canonical))
                continue
            pairs.append((variant, canonical))

    pairs.sort(key=lambda p: len(p[0]), reverse=True)
    return pairs, skipped


def apply_replacements(text, pairs):
    """Left-to-right single-pass scan; matches TextPostProcessor.ApplyReplacements exactly."""
    urls = []

    def extract_url(m):
        urls.append(m.group(0))
        return f"__URL_{len(urls)}__"

    text = URL_RE.sub(extract_url, text)
    applied = []

    if pairs:
        result = []
        pos = 0
        while pos < len(text):
            best_pos = -1
            best_pair = None
            for variant, canonical in pairs:
                idx = text.find(variant, pos)
                if idx < 0:
                    continue
                if (best_pos < 0 or idx < best_pos or
                        (idx == best_pos and len(variant) > len(best_pair[0]))):
                    best_pos = idx
                    best_pair = (variant, canonical)

            if best_pair is None:
                result.append(text[pos:])
                break

            result.append(text[pos:best_pos])
            result.append(best_pair[1])
            entry = f"{best_pair[0]} -> {best_pair[1]}"
            if entry not in applied:
                applied.append(entry)
            pos = best_pos + len(best_pair[0])

        text = "".join(result)

    for i in range(len(urls), 0, -1):
        text = text.replace(f"__URL_{i}__", urls[i - 1])

    return text, applied


def fetch_from_log(timestamp_prefix):
    """Pull raw_ai_output from the log for a run matching the given timestamp prefix."""
    from datetime import date, timedelta

    today = date.today()
    for delta in range(7):
        candidate = LOG_DIR / f"spellcheck-{today - timedelta(days=delta)}.jsonl"
        if not candidate.exists():
            continue
        with open(candidate, encoding="utf-8") as fh:
            for line in fh:
                m = LINE_RE.match(line.rstrip())
                if not m or m.group("event") != "spellcheck_detail":
                    continue
                if not m.group("ts").startswith(timestamp_prefix):
                    continue
                try:
                    detail = json.loads(m.group("rest") or "")
                    return detail.get("raw_ai_output", "")
                except json.JSONDecodeError:
                    continue
    return None


def main():
    parser = argparse.ArgumentParser(
        description="Dry-run text through replacements.json without touching the live app.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""Examples:
  python .agents/scripts/test-replacements.py "Burton Morgan Competition"
  python .agents/scripts/test-replacements.py "open ai and github stuff"
  python .agents/scripts/test-replacements.py --run 2026-05-30T14
  python .agents/scripts/test-replacements.py --replacements /alt/replacements.json "text"
        """,
    )
    parser.add_argument("text", nargs="?", help="Input text to run through replacements")
    parser.add_argument("--run", metavar="TIMESTAMP",
                        help="Pull raw_ai_output from the log for this timestamp prefix (e.g. 2026-05-30T14:32)")
    parser.add_argument("--replacements", metavar="PATH", default=str(DEFAULT_REPLACEMENTS),
                        help=f"Path to replacements.json (default: {DEFAULT_REPLACEMENTS})")
    parser.add_argument("--show-skipped", action="store_true",
                        help="Show variants rejected as unsafe (canonical contains variant)")

    args = parser.parse_args()

    if not args.text and not args.run:
        parser.error("Provide input text or --run TIMESTAMP")

    replacements_path = Path(args.replacements)
    if not replacements_path.exists():
        print(f"Error: replacements.json not found: {replacements_path}", file=sys.stderr)
        sys.exit(1)

    pairs, skipped = load_pairs(replacements_path)

    if args.show_skipped and skipped:
        print("Skipped (unsafe - variant is substring of canonical):")
        for variant, canonical in skipped:
            print(f"  {variant!r} -> {canonical!r}")
        print()

    if args.run:
        text = fetch_from_log(args.run)
        if text is None:
            print(f"Error: no spellcheck_detail found for timestamp prefix {args.run!r}", file=sys.stderr)
            sys.exit(1)
        print(f"Replaying raw_ai_output from log ({args.run}):")
    else:
        text = args.text

    output, applied = apply_replacements(text, pairs)

    print(f"Input:   {text}")
    print(f"Output:  {output}")
    if applied:
        print(f"Applied: {', '.join(applied)}")
    else:
        print("Applied: (none)")


if __name__ == "__main__":
    main()
