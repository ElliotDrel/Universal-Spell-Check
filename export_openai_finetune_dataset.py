#!/usr/bin/env python3
"""Export stable spellcheck cases into OpenAI fine-tuning JSONL files."""

from __future__ import annotations

import argparse
import json
import random
from collections import Counter
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path

import benchmark_spellcheck_models as bench


SCRIPT_DIR = Path(__file__).resolve().parent
FINE_TUNE_DATA_DIR = SCRIPT_DIR / "fine_tune_data"
DEFAULT_VALIDATION_RATIO = 0.15


@dataclass(frozen=True)
class FineTuneExample:
    case_id: str
    bucket: str
    user_text: str
    assistant_text: str


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Export a stable spellcheck dataset to OpenAI fine-tuning JSONL files."
    )
    parser.add_argument(
        "--dataset",
        type=Path,
        default=bench.resolve_default_dataset_path(),
        help="Stable benchmark dataset JSON file.",
    )
    parser.add_argument(
        "--output-dir",
        type=Path,
        default=None,
        help="Directory to write train.jsonl, validation.jsonl, and summary.json.",
    )
    parser.add_argument(
        "--seed",
        type=int,
        default=42,
        help="Random seed used for deterministic splitting.",
    )
    parser.add_argument(
        "--validation-ratio",
        type=float,
        default=DEFAULT_VALIDATION_RATIO,
        help="Fraction of examples to reserve for validation.",
    )
    parser.add_argument(
        "--buckets",
        nargs="+",
        choices=bench.DEFAULT_BUCKETS,
        default=bench.DEFAULT_BUCKETS,
        help="Gold buckets to include in the export.",
    )
    parser.add_argument(
        "--max-per-bucket",
        type=int,
        default=None,
        help="Optional cap on the number of examples exported per bucket.",
    )
    return parser.parse_args()


def build_versioned_output_dir(now: datetime | None = None) -> Path:
    stamp = (now or datetime.now()).strftime("%Y-%m-%d-%H%M%S")
    return FINE_TUNE_DATA_DIR / stamp


def case_to_example(case: bench.GoldCase) -> FineTuneExample | None:
    assistant_text = case.historical_final_output
    if not case.ai_input_text or not assistant_text:
        return None
    return FineTuneExample(
        case_id=case.case_id,
        bucket=case.bucket,
        user_text=case.ai_input_text,
        assistant_text=assistant_text,
    )


def build_examples(
    cases: list[bench.GoldCase],
    buckets: list[str],
    max_per_bucket: int | None = None,
) -> list[FineTuneExample]:
    requested_buckets = set(buckets)
    bucket_counts: Counter[str] = Counter()
    examples: list[FineTuneExample] = []
    seen_pairs: set[tuple[str, str]] = set()

    for case in sorted(cases, key=lambda item: (item.timestamp, item.case_id)):
        if case.bucket not in requested_buckets:
            continue

        example = case_to_example(case)
        if example is None:
            continue

        dedupe_key = (example.user_text, example.assistant_text)
        if dedupe_key in seen_pairs:
            continue

        if max_per_bucket is not None and bucket_counts[example.bucket] >= max_per_bucket:
            continue

        seen_pairs.add(dedupe_key)
        bucket_counts[example.bucket] += 1
        examples.append(example)

    return examples


def split_examples(
    examples: list[FineTuneExample],
    validation_ratio: float,
    seed: int,
) -> tuple[list[FineTuneExample], list[FineTuneExample]]:
    if not 0 <= validation_ratio < 1:
        raise ValueError("validation_ratio must be >= 0 and < 1")

    bucket_groups: dict[str, list[FineTuneExample]] = {bucket: [] for bucket in bench.DEFAULT_BUCKETS}
    for example in examples:
        bucket_groups.setdefault(example.bucket, []).append(example)

    rng = random.Random(seed)
    train_examples: list[FineTuneExample] = []
    validation_examples: list[FineTuneExample] = []

    for bucket in bench.DEFAULT_BUCKETS:
        items = list(bucket_groups.get(bucket, []))
        if not items:
            continue

        rng.shuffle(items)
        validation_count = int(round(len(items) * validation_ratio))
        validation_count = max(0, min(validation_count, len(items) - 1))

        validation_examples.extend(items[:validation_count])
        train_examples.extend(items[validation_count:])

    if not validation_examples and validation_ratio > 0 and len(train_examples) > 1:
        train_examples.sort(key=lambda item: (item.bucket, item.case_id))
        validation_examples.append(train_examples.pop())

    train_examples.sort(key=lambda item: (bench.DEFAULT_BUCKETS.index(item.bucket), item.case_id))
    validation_examples.sort(key=lambda item: (bench.DEFAULT_BUCKETS.index(item.bucket), item.case_id))
    return train_examples, validation_examples


def fine_tune_record(example: FineTuneExample) -> dict[str, object]:
    return {
        "messages": [
            {"role": "user", "content": example.user_text},
            {"role": "assistant", "content": example.assistant_text},
        ]
    }


def write_jsonl(path: Path, examples: list[FineTuneExample]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="\n") as handle:
        for example in examples:
            handle.write(json.dumps(fine_tune_record(example), ensure_ascii=False))
            handle.write("\n")


def summarize_examples(
    dataset_path: Path,
    buckets: list[str],
    max_per_bucket: int | None,
    validation_ratio: float,
    seed: int,
    train_examples: list[FineTuneExample],
    validation_examples: list[FineTuneExample],
) -> dict[str, object]:
    return {
        "dataset_path": str(dataset_path),
        "exported_at": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
        "seed": seed,
        "validation_ratio": validation_ratio,
        "included_buckets": buckets,
        "max_per_bucket": max_per_bucket,
        "train_count": len(train_examples),
        "validation_count": len(validation_examples),
        "train_by_bucket": dict(Counter(example.bucket for example in train_examples)),
        "validation_by_bucket": dict(Counter(example.bucket for example in validation_examples)),
    }


def export_fine_tune_dataset(
    dataset_path: Path,
    output_dir: Path,
    buckets: list[str],
    validation_ratio: float,
    seed: int,
    max_per_bucket: int | None = None,
) -> dict[str, object]:
    cases, dataset_summary = bench.load_stable_dataset(dataset_path)
    examples = build_examples(cases, buckets=buckets, max_per_bucket=max_per_bucket)
    if len(examples) < 2:
        raise RuntimeError("Need at least 2 examples to export train/validation files.")

    train_examples, validation_examples = split_examples(
        examples,
        validation_ratio=validation_ratio,
        seed=seed,
    )
    if not train_examples:
        raise RuntimeError("Training split is empty.")

    output_dir.mkdir(parents=True, exist_ok=True)
    train_path = output_dir / "train.jsonl"
    validation_path = output_dir / "validation.jsonl"
    summary_path = output_dir / "summary.json"

    write_jsonl(train_path, train_examples)
    write_jsonl(validation_path, validation_examples)

    summary = summarize_examples(
        dataset_path=dataset_path,
        buckets=buckets,
        max_per_bucket=max_per_bucket,
        validation_ratio=validation_ratio,
        seed=seed,
        train_examples=train_examples,
        validation_examples=validation_examples,
    )
    summary["source_dataset_summary"] = dataset_summary
    summary_path.write_text(json.dumps(summary, indent=2, ensure_ascii=False), encoding="utf-8")

    return {
        "train_path": str(train_path),
        "validation_path": str(validation_path),
        "summary_path": str(summary_path),
        "summary": summary,
    }


def main() -> None:
    args = parse_args()
    output_dir = args.output_dir or build_versioned_output_dir()
    result = export_fine_tune_dataset(
        dataset_path=args.dataset,
        output_dir=output_dir,
        buckets=args.buckets,
        validation_ratio=args.validation_ratio,
        seed=args.seed,
        max_per_bucket=args.max_per_bucket,
    )
    print(json.dumps(result, indent=2, ensure_ascii=False))


if __name__ == "__main__":
    main()
