import json
import sys
import unittest
from datetime import datetime
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent.parent / ".claude/skills/finetune-cycle/scripts"))
import benchmark_spellcheck_models as bench
import export_openai_finetune_dataset as exporter


class ExportOpenAiFineTuneDatasetTests(unittest.TestCase):
    def make_case(
        self,
        case_id: str,
        bucket: str,
        source_text: str,
        output_text: str | None,
        timestamp: str,
    ) -> bench.GoldCase:
        return bench.GoldCase(
            case_id=case_id,
            source_file="x.jsonl",
            source_line=1,
            timestamp=timestamp,
            bucket=bucket,
            source_text=source_text,
            ai_input_text=bench.build_prompt(source_text),
            gold_raw_output=output_text or "",
            historical_final_output=output_text,
            request_metadata=bench.RequestMetadata(
                prompt_text=bench.build_prompt(source_text),
                store=True,
                text_config={"verbosity": "medium"},
                temperature=0.3,
            ),
        )

    def test_build_versioned_output_dir_format(self):
        output_dir = exporter.build_versioned_output_dir(
            now=datetime(2026, 4, 11, 20, 15, 0)
        )
        self.assertEqual(output_dir.parent.name, "fine_tune_data")
        self.assertEqual(output_dir.name, "2026-04-11-201500")

    def test_build_examples_dedupes_identical_pairs(self):
        cases = [
            self.make_case(
                "case-1",
                "small_change",
                "teh black cat",
                "the black cat",
                "2026-04-11 10:00:00",
            ),
            self.make_case(
                "case-2",
                "small_change",
                "teh black cat",
                "the black cat",
                "2026-04-11 10:01:00",
            ),
            self.make_case(
                "case-3",
                "small_change",
                "teh white cat",
                None,
                "2026-04-11 10:02:00",
            ),
        ]

        examples = exporter.build_examples(cases, buckets=["small_change"])
        self.assertEqual(len(examples), 1)
        self.assertEqual(examples[0].case_id, "case-1")
        self.assertEqual(examples[0].assistant_text, "the black cat")

    def test_split_examples_creates_validation_set_without_emptying_train(self):
        examples = [
            exporter.FineTuneExample(
                case_id=f"case-{index}",
                bucket="small_change",
                user_text=f"prompt {index}",
                assistant_text=f"answer {index}",
            )
            for index in range(6)
        ]

        train_examples, validation_examples = exporter.split_examples(
            examples,
            validation_ratio=0.2,
            seed=7,
        )

        self.assertEqual(len(train_examples) + len(validation_examples), 6)
        self.assertEqual(len(validation_examples), 1)
        self.assertEqual(len(train_examples), 5)

    def test_export_fine_tune_dataset_writes_openai_jsonl_files(self):
        runtime_dir = bench.SCRIPT_DIR / "tests" / "_runtime_finetune_export"
        dataset_path = runtime_dir / "stable_cases.json"
        output_dir = runtime_dir / "exported"
        train_path = output_dir / "train.jsonl"
        validation_path = output_dir / "validation.jsonl"
        summary_path = output_dir / "summary.json"
        cases = [
            self.make_case(
                "case-1",
                "small_change",
                "teh black cat",
                "the black cat",
                "2026-04-11 10:00:00",
            ),
            self.make_case(
                "case-2",
                "medium_change",
                "i go store yesterday",
                "I went to the store yesterday.",
                "2026-04-11 10:01:00",
            ),
            self.make_case(
                "case-3",
                "unchanged",
                "Meeting - LLC Formation Discussion - Keel",
                "Meeting - LLC Formation Discussion - Keel",
                "2026-04-11 10:02:00",
            ),
        ]
        summary = {"selected_by_bucket": {"small_change": 1, "medium_change": 1, "unchanged": 1}}

        runtime_dir.mkdir(parents=True, exist_ok=True)
        try:
            bench.save_stable_dataset(dataset_path, cases, summary)
            result = exporter.export_fine_tune_dataset(
                dataset_path=dataset_path,
                output_dir=output_dir,
                buckets=["unchanged", "small_change", "medium_change"],
                validation_ratio=0.34,
                seed=42,
            )

            train_path = Path(result["train_path"])
            validation_path = Path(result["validation_path"])
            summary_path = Path(result["summary_path"])

            self.assertTrue(train_path.exists())
            self.assertTrue(validation_path.exists())
            self.assertTrue(summary_path.exists())

            train_lines = train_path.read_text(encoding="utf-8").strip().splitlines()
            validation_lines = validation_path.read_text(encoding="utf-8").strip().splitlines()
            self.assertEqual(len(train_lines), 2)
            self.assertEqual(len(validation_lines), 1)

            sample_record = json.loads(train_lines[0])
            self.assertEqual(
                sample_record["messages"][0]["role"],
                "user",
            )
            self.assertEqual(
                sample_record["messages"][1]["role"],
                "assistant",
            )

            written_summary = json.loads(summary_path.read_text(encoding="utf-8"))
            self.assertEqual(written_summary["train_count"], 2)
            self.assertEqual(written_summary["validation_count"], 1)
        finally:
            if train_path.exists():
                train_path.unlink()
            if validation_path.exists():
                validation_path.unlink()
            if summary_path.exists():
                summary_path.unlink()
            if output_dir.exists():
                output_dir.rmdir()
            if dataset_path.exists():
                dataset_path.unlink()
            if runtime_dir.exists():
                runtime_dir.rmdir()

    def test_load_cases_from_logs_filters_and_dedupes(self):
        runtime_dir = bench.SCRIPT_DIR / "tests" / "_runtime_log_export"
        log_path = runtime_dir / "spellcheck-2026-04-20-to-2026-04-26.jsonl"
        runtime_dir.mkdir(parents=True, exist_ok=True)
        canonical_prompt = bench.build_prompt("teh black cat")
        bad_prompt = "instructions: older prompt\ntext input: teh black cat"

        entries = [
            {
                "timestamp": "2026-04-20 10:00:00",
                "status": "SUCCESS",
                "model": "gpt-4.1",
                "output_text": "the black cat",
                "raw_request": json.dumps(
                    {
                        "model": "gpt-4.1",
                        "input": [
                            {
                                "role": "user",
                                "content": [{"type": "input_text", "text": canonical_prompt}],
                            }
                        ],
                        "store": True,
                        "text": {"verbosity": "medium"},
                        "temperature": 0.3,
                    }
                ),
                "prompt_leak": {"triggered": False},
            },
            {
                "timestamp": "2026-04-20 10:00:01",
                "status": "SUCCESS",
                "model": "gpt-4.1",
                "output_text": "the black cat",
                "raw_request": json.dumps(
                    {
                        "model": "gpt-4.1",
                        "input": [
                            {
                                "role": "user",
                                "content": [{"type": "input_text", "text": canonical_prompt}],
                            }
                        ],
                        "store": True,
                        "text": {"verbosity": "medium"},
                        "temperature": 0.3,
                    }
                ),
                "prompt_leak": {"triggered": False},
            },
            {
                "timestamp": "2026-04-20 10:00:02",
                "status": "SUCCESS",
                "model": "gpt-4.1",
                "output_text": "the black cat",
                "raw_request": json.dumps(
                    {
                        "model": "gpt-4.1",
                        "input": [
                            {
                                "role": "user",
                                "content": [{"type": "input_text", "text": bad_prompt}],
                            }
                        ],
                        "store": True,
                        "text": {"verbosity": "medium"},
                        "temperature": 0.3,
                    }
                ),
                "prompt_leak": {"triggered": False},
            },
            {
                "timestamp": "2026-04-20 10:00:03",
                "status": "ERROR",
                "model": "gpt-4.1",
                "output_text": "",
                "raw_request": "",
                "prompt_leak": {"triggered": False},
            },
        ]

        log_path.write_text(
            "\n".join(json.dumps(entry) for entry in entries) + "\n",
            encoding="utf-8",
        )
        try:
            cases, summary = exporter.load_cases_from_logs(
                log_dir=runtime_dir,
                weeks=1,
                buckets=["small_change"],
                include_unchanged=True,
            )
            self.assertEqual(len(cases), 1)
            self.assertEqual(cases[0].source_text, "teh black cat")
            self.assertEqual(cases[0].historical_final_output, "the black cat")
            self.assertEqual(summary["skipped_non_canonical_prompt_cases"], 1)
            self.assertEqual(summary["skipped_non_success_cases"], 1)
        finally:
            if log_path.exists():
                log_path.unlink()
            if runtime_dir.exists():
                runtime_dir.rmdir()

    def test_export_fine_tune_dataset_from_logs_writes_openai_jsonl_files(self):
        runtime_dir = bench.SCRIPT_DIR / "tests" / "_runtime_log_export_files"
        output_dir = runtime_dir / "exported"
        log_path = runtime_dir / "spellcheck-2026-04-20-to-2026-04-26.jsonl"
        runtime_dir.mkdir(parents=True, exist_ok=True)

        def make_log_entry(timestamp: str, input_text: str, output_text: str) -> dict[str, object]:
            return {
                "timestamp": timestamp,
                "status": "SUCCESS",
                "model": "gpt-4.1",
                "input_text": input_text,
                "output_text": output_text,
                "raw_request": json.dumps(
                    {
                        "model": "gpt-4.1",
                        "input": [
                            {
                                "role": "user",
                                "content": [{"type": "input_text", "text": bench.build_prompt(input_text)}],
                            }
                        ],
                        "store": True,
                        "text": {"verbosity": "medium"},
                        "temperature": 0.3,
                    }
                ),
                "prompt_leak": {"triggered": False},
            }

        entries = [
            make_log_entry("2026-04-20 10:00:00", "teh black cat", "the black cat"),
            make_log_entry("2026-04-20 10:00:01", "i go store yesterday", "I went to the store yesterday."),
            make_log_entry(
                "2026-04-20 10:00:02",
                "Meeting - LLC Formation Discussion - Keel",
                "Meeting - LLC Formation Discussion - Keel",
            ),
        ]
        log_path.write_text(
            "\n".join(json.dumps(entry) for entry in entries) + "\n",
            encoding="utf-8",
        )

        try:
            result = exporter.export_fine_tune_dataset_from_logs(
                log_dir=runtime_dir,
                weeks=1,
                output_dir=output_dir,
                buckets=["unchanged", "small_change", "medium_change"],
                validation_ratio=0.34,
                seed=42,
            )

            train_path = Path(result["train_path"])
            validation_path = Path(result["validation_path"])
            summary_path = Path(result["summary_path"])

            self.assertTrue(train_path.exists())
            self.assertTrue(validation_path.exists())
            self.assertTrue(summary_path.exists())

            train_lines = train_path.read_text(encoding="utf-8").strip().splitlines()
            validation_lines = validation_path.read_text(encoding="utf-8").strip().splitlines()
            self.assertEqual(len(train_lines), 2)
            self.assertEqual(len(validation_lines), 1)

            written_summary = json.loads(summary_path.read_text(encoding="utf-8"))
            self.assertEqual(written_summary["source"], "weekly_logs")
            self.assertEqual(written_summary["source_dataset_summary"]["case_count"], 3)
        finally:
            for path in [
                output_dir / "train.jsonl",
                output_dir / "validation.jsonl",
                output_dir / "summary.json",
                log_path,
            ]:
                if path.exists():
                    path.unlink()
            if output_dir.exists():
                output_dir.rmdir()
            if runtime_dir.exists():
                runtime_dir.rmdir()

    def test_export_from_logs_excludes_previously_trained_pairs(self):
        runtime_dir = bench.SCRIPT_DIR / "tests" / "_runtime_log_export_exclusions"
        output_dir = runtime_dir / "exported"
        previous_dir = runtime_dir / "previous"
        log_path = runtime_dir / "spellcheck-2026-04-20-to-2026-04-26.jsonl"
        runtime_dir.mkdir(parents=True, exist_ok=True)
        previous_dir.mkdir(parents=True, exist_ok=True)

        prior_record = {
            "messages": [
                {"role": "user", "content": bench.build_prompt("teh black cat")},
                {"role": "assistant", "content": "the black cat"},
            ]
        }
        (previous_dir / "train.jsonl").write_text(
            json.dumps(prior_record, ensure_ascii=False) + "\n",
            encoding="utf-8",
        )

        def make_log_entry(timestamp: str, input_text: str, output_text: str) -> dict[str, object]:
            return {
                "timestamp": timestamp,
                "status": "SUCCESS",
                "model": "gpt-4.1",
                "input_text": input_text,
                "output_text": output_text,
                "raw_request": json.dumps(
                    {
                        "model": "gpt-4.1",
                        "input": [
                            {
                                "role": "user",
                                "content": [{"type": "input_text", "text": bench.build_prompt(input_text)}],
                            }
                        ],
                        "store": True,
                        "text": {"verbosity": "medium"},
                        "temperature": 0.3,
                    }
                ),
                "prompt_leak": {"triggered": False},
            }

        entries = [
            make_log_entry("2026-04-20 10:00:00", "teh black cat", "the black cat"),
            make_log_entry("2026-04-20 10:00:01", "i go store yesterday", "I went to the store yesterday."),
            make_log_entry("2026-04-20 10:00:02", "i cant spel corectly", "I can't spell correctly."),
        ]
        log_path.write_text(
            "\n".join(json.dumps(entry) for entry in entries) + "\n",
            encoding="utf-8",
        )

        try:
            result = exporter.export_fine_tune_dataset_from_logs(
                log_dir=runtime_dir,
                weeks=1,
                output_dir=output_dir,
                buckets=["small_change", "medium_change"],
                validation_ratio=0.0,
                seed=42,
                previous_data_dir=previous_dir,
            )

            train_path = Path(result["train_path"])
            train_lines = train_path.read_text(encoding="utf-8").strip().splitlines()
            self.assertEqual(len(train_lines), 2)
            contents = [json.loads(line)["messages"][1]["content"] for line in train_lines]
            self.assertNotIn("the black cat", contents)
            self.assertEqual(result["summary"]["excluded_previously_trained_pair_count"], 1)
        finally:
            for path in [
                output_dir / "train.jsonl",
                output_dir / "validation.jsonl",
                output_dir / "summary.json",
                previous_dir / "train.jsonl",
                log_path,
            ]:
                if path.exists():
                    path.unlink()
            if output_dir.exists():
                output_dir.rmdir()
            if previous_dir.exists():
                previous_dir.rmdir()
            if runtime_dir.exists():
                runtime_dir.rmdir()


if __name__ == "__main__":
    unittest.main()
