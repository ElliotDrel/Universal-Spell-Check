import json
import unittest
from datetime import datetime
from pathlib import Path

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


if __name__ == "__main__":
    unittest.main()
