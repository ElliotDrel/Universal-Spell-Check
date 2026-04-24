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
from typing import Any

import benchmark_spellcheck_models as bench


SCRIPT_DIR = Path(__file__).resolve().parent
FINE_TUNE_DATA_DIR = SCRIPT_DIR / "fine_tune_data"
PREVIOUS_BATCHES_DIR = FINE_TUNE_DATA_DIR / "previous_batches"
LATEST_BATCH_DIR = FINE_TUNE_DATA_DIR / "latest_batch"
FINE_TUNE_RUNS_DIR = SCRIPT_DIR / "fine_tune_runs"
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
        "--source",
        choices=("stable", "logs"),
        default="stable",
        help="Export examples from the frozen stable dataset or directly from live weekly logs.",
    )
    parser.add_argument(
        "--dataset",
        type=Path,
        default=bench.resolve_default_dataset_path(),
        help="Stable benchmark dataset JSON file when --source=stable.",
    )
    parser.add_argument(
        "--log-dir",
        type=Path,
        default=bench.LOGS_DIR,
        help="Weekly spellcheck log directory when --source=logs.",
    )
    parser.add_argument(
        "--weeks",
        type=int,
        default=8,
        help="How many recent weekly log groups to scan when --source=logs.",
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
    parser.add_argument(
        "--exclude-unchanged",
        action="store_true",
        help="Drop unchanged examples when --source=logs.",
    )
    parser.add_argument(
        "--previous-data-dir",
        type=Path,
        default=PREVIOUS_BATCHES_DIR,
        help="Directory containing prior fine-tune train/validation JSONL files to exclude.",
    )
    parser.add_argument(
        "--run-dir",
        type=Path,
        default=None,
        help=(
            "Fine-tune run directory (e.g. fine_tune_runs/2026-04-23-143000). "
            "When set, writes train.jsonl/validation.jsonl/dataset_summary.json into this dir, "
            "appends a dataset section to <run-dir>/summary.md, and deduplicates against "
            "fine_tune_runs/*/train.jsonl and fine_tune_runs/*/validation.jsonl."
        ),
    )
    return parser.parse_args()


def build_versioned_output_dir(now: datetime | None = None) -> Path:
    stamp = (now or datetime.now()).strftime("%Y-%m-%d-%H%M%S")
    return FINE_TUNE_DATA_DIR / stamp


def resolve_default_output_dir(source: str) -> Path:
    if source == "logs":
        return LATEST_BATCH_DIR
    return build_versioned_output_dir()


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
    excluded_pairs: set[tuple[str, str]] | None = None,
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
        if excluded_pairs is not None and dedupe_key in excluded_pairs:
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


def load_existing_finetune_pairs(
    previous_data_dir: Path,
    ignore_output_dir: Path | None = None,
) -> set[tuple[str, str]]:
    pairs: set[tuple[str, str]] = set()
    if not previous_data_dir.exists():
        return pairs

    ignored_root = ignore_output_dir.resolve() if ignore_output_dir is not None else None
    for path in previous_data_dir.rglob("*.jsonl"):
        if path.name not in {"train.jsonl", "validation.jsonl"}:
            continue

        resolved_path = path.resolve()
        if ignored_root is not None:
            try:
                resolved_path.relative_to(ignored_root)
                continue
            except ValueError:
                pass

        with path.open(encoding="utf-8", errors="replace") as handle:
            for raw_line in handle:
                line = raw_line.strip()
                if not line:
                    continue
                try:
                    record = json.loads(line)
                except json.JSONDecodeError:
                    continue

                messages = record.get("messages")
                if not isinstance(messages, list) or len(messages) < 2:
                    continue
                user_content = messages[0].get("content") if isinstance(messages[0], dict) else None
                assistant_content = messages[1].get("content") if isinstance(messages[1], dict) else None
                if isinstance(user_content, str) and isinstance(assistant_content, str):
                    pairs.add((user_content, assistant_content))
    return pairs


def summarize_examples(
    dataset_path: Path | None,
    buckets: list[str],
    max_per_bucket: int | None,
    validation_ratio: float,
    seed: int,
    train_examples: list[FineTuneExample],
    validation_examples: list[FineTuneExample],
    source: str,
) -> dict[str, object]:
    return {
        "dataset_path": str(dataset_path) if dataset_path is not None else None,
        "exported_at": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
        "source": source,
        "seed": seed,
        "validation_ratio": validation_ratio,
        "included_buckets": buckets,
        "max_per_bucket": max_per_bucket,
        "train_count": len(train_examples),
        "validation_count": len(validation_examples),
        "train_by_bucket": dict(Counter(example.bucket for example in train_examples)),
        "validation_by_bucket": dict(Counter(example.bucket for example in validation_examples)),
    }


def load_cases_from_logs(
    log_dir: Path,
    weeks: int,
    buckets: list[str],
    include_unchanged: bool,
) -> tuple[list[bench.GoldCase], dict[str, Any]]:
    selected_files = bench.select_recent_week_groups(log_dir, weeks)
    if not selected_files:
        raise RuntimeError(f"No weekly spellcheck logs found in {log_dir}")

    requested_buckets = set(buckets)
    entries = bench.iter_jsonl_entries(selected_files)
    deduped: dict[tuple[str, str], bench.GoldCase] = {}
    excluded_short_input_cases = 0
    skipped_prompt_leak = 0
    skipped_missing_text = 0
    skipped_non_success = 0
    skipped_non_41 = 0
    skipped_non_canonical_prompt = 0

    newest_first = sorted(entries, key=lambda item: item[2].get("timestamp", ""), reverse=True)
    for path, line_no, entry in newest_first:
        if entry.get("status") != "SUCCESS":
            skipped_non_success += 1
            continue
        if entry.get("model") != "gpt-4.1":
            skipped_non_41 += 1
            continue
        prompt_leak = entry.get("prompt_leak")
        if isinstance(prompt_leak, dict) and prompt_leak.get("triggered"):
            skipped_prompt_leak += 1
            continue

        output_text = entry.get("output_text")
        if not isinstance(output_text, str) or not output_text:
            skipped_missing_text += 1
            continue

        metadata = bench.parse_request_metadata(entry.get("raw_request"))
        source_text = ""
        if metadata.prompt_text:
            source_text = bench.extract_source_text_from_prompt(metadata.prompt_text)
        if not source_text:
            fallback_input = entry.get("input_text")
            if isinstance(fallback_input, str):
                source_text = fallback_input
        if not source_text:
            skipped_missing_text += 1
            continue
        if bench.source_word_count(source_text) < bench.MIN_SOURCE_WORDS:
            excluded_short_input_cases += 1
            continue

        canonical_prompt_text = bench.build_prompt(source_text)
        if metadata.prompt_text and metadata.prompt_text != canonical_prompt_text:
            skipped_non_canonical_prompt += 1
            continue

        bucket = bench.classify_gold_bucket(source_text, output_text)
        if not include_unchanged and bucket == "unchanged":
            continue
        if bucket not in requested_buckets:
            continue

        prompt_text = metadata.prompt_text or canonical_prompt_text
        dedupe_key = (prompt_text, output_text)
        if dedupe_key in deduped:
            continue

        deduped[dedupe_key] = bench.GoldCase(
            case_id=f"{path.stem}:{line_no}",
            source_file=path.name,
            source_line=line_no,
            timestamp=str(entry.get("timestamp", "")),
            bucket=bucket,
            source_text=source_text,
            ai_input_text=prompt_text,
            gold_raw_output=output_text,
            historical_final_output=output_text,
            request_metadata=metadata,
        )

    cases = sorted(
        deduped.values(),
        key=lambda case: (bench.DEFAULT_BUCKETS.index(case.bucket), case.timestamp, case.case_id),
    )
    summary = {
        "source": "weekly_logs",
        "log_dir": str(log_dir),
        "weeks": weeks,
        "selected_files": [path.name for path in selected_files],
        "case_count": len(cases),
        "selected_by_bucket": dict(Counter(case.bucket for case in cases)),
        "excluded_short_input_cases": excluded_short_input_cases,
        "skipped_prompt_leak_cases": skipped_prompt_leak,
        "skipped_missing_text_cases": skipped_missing_text,
        "skipped_non_success_cases": skipped_non_success,
        "skipped_non_gpt41_cases": skipped_non_41,
        "skipped_non_canonical_prompt_cases": skipped_non_canonical_prompt,
        "include_unchanged": include_unchanged,
        "min_source_words": bench.MIN_SOURCE_WORDS,
        "recommended_next_step": (
            "Dataset is below OpenAI's recommended starting size of 50 well-crafted examples."
            if len(cases) < 50
            else "Dataset meets OpenAI's recommended starting size; benchmark it before adding more."
        ),
    }
    return cases, summary


def export_fine_tune_dataset(
    dataset_path: Path,
    output_dir: Path,
    buckets: list[str],
    validation_ratio: float,
    seed: int,
    max_per_bucket: int | None = None,
    previous_data_dir: Path | None = None,
) -> dict[str, object]:
    cases, dataset_summary = bench.load_stable_dataset(dataset_path)
    excluded_pairs = (
        load_existing_finetune_pairs(previous_data_dir, ignore_output_dir=output_dir)
        if previous_data_dir is not None
        else set()
    )
    examples = build_examples(
        cases,
        buckets=buckets,
        max_per_bucket=max_per_bucket,
        excluded_pairs=excluded_pairs,
    )
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
        source="stable_dataset",
    )
    summary["source_dataset_summary"] = dataset_summary
    summary["excluded_previously_trained_pair_count"] = len(excluded_pairs)
    summary_path.write_text(json.dumps(summary, indent=2, ensure_ascii=False), encoding="utf-8")

    return {
        "train_path": str(train_path),
        "validation_path": str(validation_path),
        "summary_path": str(summary_path),
        "summary": summary,
    }


def export_fine_tune_dataset_from_logs(
    log_dir: Path,
    weeks: int,
    output_dir: Path,
    buckets: list[str],
    validation_ratio: float,
    seed: int,
    max_per_bucket: int | None = None,
    include_unchanged: bool = True,
    previous_data_dir: Path | None = None,
) -> dict[str, object]:
    cases, log_summary = load_cases_from_logs(
        log_dir=log_dir,
        weeks=weeks,
        buckets=buckets,
        include_unchanged=include_unchanged,
    )
    excluded_pairs = (
        load_existing_finetune_pairs(previous_data_dir, ignore_output_dir=output_dir)
        if previous_data_dir is not None
        else set()
    )
    examples = build_examples(
        cases,
        buckets=buckets,
        max_per_bucket=max_per_bucket,
        excluded_pairs=excluded_pairs,
    )
    if len(examples) < 2:
        raise RuntimeError("Need at least 2 examples from logs to export train/validation files.")

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
        dataset_path=None,
        buckets=buckets,
        max_per_bucket=max_per_bucket,
        validation_ratio=validation_ratio,
        seed=seed,
        train_examples=train_examples,
        validation_examples=validation_examples,
        source="weekly_logs",
    )
    summary["source_dataset_summary"] = log_summary
    summary["excluded_previously_trained_pair_count"] = len(excluded_pairs)
    summary_path.write_text(json.dumps(summary, indent=2, ensure_ascii=False), encoding="utf-8")

    return {
        "train_path": str(train_path),
        "validation_path": str(validation_path),
        "summary_path": str(summary_path),
        "summary": summary,
    }


def load_existing_finetune_pairs_from_runs(
    runs_dir: Path,
) -> set[tuple[str, str]]:
    """Scan fine_tune_runs/*/train.jsonl and fine_tune_runs/*/validation.jsonl for dedup."""
    pairs: set[tuple[str, str]] = set()
    if not runs_dir.exists():
        return pairs

    for path in runs_dir.rglob("*.jsonl"):
        if path.name not in {"train.jsonl", "validation.jsonl"}:
            continue

        with path.open(encoding="utf-8", errors="replace") as handle:
            for raw_line in handle:
                line = raw_line.strip()
                if not line:
                    continue
                try:
                    record = json.loads(line)
                except json.JSONDecodeError:
                    continue

                messages = record.get("messages")
                if not isinstance(messages, list) or len(messages) < 2:
                    continue
                user_content = messages[0].get("content") if isinstance(messages[0], dict) else None
                assistant_content = messages[1].get("content") if isinstance(messages[1], dict) else None
                if isinstance(user_content, str) and isinstance(assistant_content, str):
                    pairs.add((user_content, assistant_content))
    return pairs


def append_dataset_section_to_summary_md(
    run_dir: Path,
    train_examples: list[FineTuneExample],
    validation_examples: list[FineTuneExample],
    excluded_count: int,
) -> None:
    """Append a ## 1. Dataset Export section to <run-dir>/summary.md (idempotent)."""
    summary_md_path = run_dir / "summary.md"
    existing_content = summary_md_path.read_text(encoding="utf-8") if summary_md_path.exists() else ""

    if "## 1. Dataset Export" in existing_content:
        return

    now_str = datetime.now().strftime("%H:%M")
    total_count = len(train_examples) + len(validation_examples)
    train_count = len(train_examples)
    val_count = len(validation_examples)

    bucket_counts: Counter[str] = Counter(ex.bucket for ex in train_examples + validation_examples)
    table_rows = "\n".join(
        f"| {bucket} | {count} |"
        for bucket in bench.DEFAULT_BUCKETS
        if (count := bucket_counts.get(bucket, 0)) > 0
    )
    table = f"| Bucket | Count |\n|--------|-------|\n{table_rows}"

    section = (
        f"\n## 1. Dataset Export (completed {now_str})\n"
        f"{total_count} examples ({train_count} train / {val_count} val)"
        f" · {excluded_count} excluded as duplicates\n"
        f"{table}\n"
    )

    run_dir.mkdir(parents=True, exist_ok=True)
    with summary_md_path.open("a", encoding="utf-8") as fh:
        fh.write(section)


def main() -> None:
    args = parse_args()

    if args.run_dir is not None:
        run_dir: Path = args.run_dir
        run_dir.mkdir(parents=True, exist_ok=True)
        output_dir = run_dir

        # Check if dataset already exported to this run directory (write-once semantics).
        train_path = output_dir / "train.jsonl"
        if train_path.exists():
            print(f"Dataset already exported to {run_dir} — skipping.")
            return

        # Dedup against all fine_tune_runs/*/train.jsonl and validation.jsonl
        excluded_pairs = load_existing_finetune_pairs_from_runs(FINE_TUNE_RUNS_DIR)

        if args.source == "logs":
            cases, log_summary = load_cases_from_logs(
                log_dir=args.log_dir,
                weeks=args.weeks,
                buckets=args.buckets,
                include_unchanged=not args.exclude_unchanged,
            )
        else:
            cases, dataset_summary = bench.load_stable_dataset(args.dataset)

        examples = build_examples(
            cases,
            buckets=args.buckets,
            max_per_bucket=args.max_per_bucket,
            excluded_pairs=excluded_pairs,
        )
        if len(examples) < 2:
            raise RuntimeError("Need at least 2 examples to export train/validation files.")

        train_examples, validation_examples = split_examples(
            examples,
            validation_ratio=args.validation_ratio,
            seed=args.seed,
        )
        if not train_examples:
            raise RuntimeError("Training split is empty.")

        validation_path = output_dir / "validation.jsonl"
        summary_path = output_dir / "dataset_summary.json"

        write_jsonl(train_path, train_examples)
        write_jsonl(validation_path, validation_examples)

        source_label = "weekly_logs" if args.source == "logs" else "stable_dataset"
        summary = summarize_examples(
            dataset_path=args.dataset if args.source == "stable" else None,
            buckets=args.buckets,
            max_per_bucket=args.max_per_bucket,
            validation_ratio=args.validation_ratio,
            seed=args.seed,
            train_examples=train_examples,
            validation_examples=validation_examples,
            source=source_label,
        )
        if args.source == "logs":
            summary["source_dataset_summary"] = log_summary
        else:
            summary["source_dataset_summary"] = dataset_summary
        summary["excluded_previously_trained_pair_count"] = len(excluded_pairs)
        summary_path.write_text(json.dumps(summary, indent=2, ensure_ascii=False), encoding="utf-8")

        append_dataset_section_to_summary_md(
            run_dir=run_dir,
            train_examples=train_examples,
            validation_examples=validation_examples,
            excluded_count=len(excluded_pairs),
        )

        result = {
            "train_path": str(train_path),
            "validation_path": str(validation_path),
            "summary_path": str(summary_path),
            "summary": summary,
        }
    else:
        output_dir = args.output_dir or resolve_default_output_dir(args.source)
        if args.source == "logs":
            result = export_fine_tune_dataset_from_logs(
                log_dir=args.log_dir,
                weeks=args.weeks,
                output_dir=output_dir,
                buckets=args.buckets,
                validation_ratio=args.validation_ratio,
                seed=args.seed,
                max_per_bucket=args.max_per_bucket,
                include_unchanged=not args.exclude_unchanged,
                previous_data_dir=args.previous_data_dir,
            )
        else:
            result = export_fine_tune_dataset(
                dataset_path=args.dataset,
                output_dir=output_dir,
                buckets=args.buckets,
                validation_ratio=args.validation_ratio,
                seed=args.seed,
                max_per_bucket=args.max_per_bucket,
                previous_data_dir=args.previous_data_dir,
            )

    print(json.dumps(result, indent=2, ensure_ascii=False))


if __name__ == "__main__":
    main()
