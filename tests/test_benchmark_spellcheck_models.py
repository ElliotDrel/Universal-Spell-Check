import json
import threading
import time
import unittest
from datetime import datetime
from pathlib import Path

import httpx

import benchmark_spellcheck_models as bench


class BenchmarkSpellcheckModelsTests(unittest.TestCase):
    def test_versioned_dataset_path_format(self):
        versioned_path = bench.build_versioned_dataset_path(
            now=datetime(2026, 4, 11, 18, 40, 0)
        )
        self.assertTrue(versioned_path.name.startswith("stable_spellcheck_cases-2026-04-11-184000"))

    def test_stable_dataset_round_trip(self):
        dataset_dir = bench.SCRIPT_DIR / "tests" / "_runtime_dataset"
        dataset_path = dataset_dir / "stable_cases.json"
        case = bench.GoldCase(
            case_id="case-1",
            source_file="x.jsonl",
            source_line=3,
            timestamp="2026-04-11 10:00:00",
            bucket="small_change",
            source_text="teh black cat",
            ai_input_text=bench.build_prompt("teh black cat"),
            gold_raw_output="the black cat",
            historical_final_output="the black cat",
            request_metadata=bench.RequestMetadata(
                prompt_text=bench.build_prompt("teh black cat"),
                store=True,
                text_config={"verbosity": "medium"},
                temperature=0.3,
            ),
        )
        summary = {"selected_by_bucket": {"small_change": 1}, "source": "rebuilt_from_logs"}

        try:
            bench.save_stable_dataset(dataset_path, [case], summary)
            cases, loaded_summary = bench.load_stable_dataset(dataset_path)
            self.assertEqual(len(cases), 1)
            self.assertEqual(cases[0].ai_input_text, case.ai_input_text)
            self.assertEqual(cases[0].gold_raw_output, "the black cat")
            self.assertEqual(loaded_summary["dataset_path"], str(dataset_path))
            self.assertEqual(loaded_summary["selected_by_bucket"]["small_change"], 1)
        finally:
            if dataset_path.exists():
                dataset_path.unlink()
            if dataset_dir.exists():
                dataset_dir.rmdir()

    def test_load_stable_dataset_filters_one_and_two_word_cases(self):
        dataset_dir = bench.SCRIPT_DIR / "tests" / "_runtime_dataset"
        dataset_path = dataset_dir / "stable_cases.json"
        short_case = bench.GoldCase(
            case_id="case-short",
            source_file="x.jsonl",
            source_line=1,
            timestamp="2026-04-11 10:00:00",
            bucket="small_change",
            source_text="teh",
            ai_input_text=bench.build_prompt("teh"),
            gold_raw_output="the",
            historical_final_output="the",
            request_metadata=bench.RequestMetadata(
                prompt_text=bench.build_prompt("teh"),
                store=True,
                text_config={"verbosity": "medium"},
                temperature=0.3,
            ),
        )
        two_word_case = bench.GoldCase(
            case_id="case-two",
            source_file="x.jsonl",
            source_line=2,
            timestamp="2026-04-11 10:00:01",
            bucket="small_change",
            source_text="teh cat",
            ai_input_text=bench.build_prompt("teh cat"),
            gold_raw_output="the cat",
            historical_final_output="the cat",
            request_metadata=bench.RequestMetadata(
                prompt_text=bench.build_prompt("teh cat"),
                store=True,
                text_config={"verbosity": "medium"},
                temperature=0.3,
            ),
        )
        kept_case = bench.GoldCase(
            case_id="case-keep",
            source_file="x.jsonl",
            source_line=3,
            timestamp="2026-04-11 10:00:02",
            bucket="small_change",
            source_text="teh black cat",
            ai_input_text=bench.build_prompt("teh black cat"),
            gold_raw_output="the black cat",
            historical_final_output="the black cat",
            request_metadata=bench.RequestMetadata(
                prompt_text=bench.build_prompt("teh black cat"),
                store=True,
                text_config={"verbosity": "medium"},
                temperature=0.3,
            ),
        )

        summary = {"selected_by_bucket": {"small_change": 3}, "source": "rebuilt_from_logs"}

        try:
            bench.save_stable_dataset(dataset_path, [short_case, two_word_case, kept_case], summary)
            cases, loaded_summary = bench.load_stable_dataset(dataset_path)
            self.assertEqual([case.case_id for case in cases], ["case-keep"])
            self.assertEqual(loaded_summary["excluded_short_input_cases"], 2)
            self.assertEqual(loaded_summary["min_source_words"], 3)
        finally:
            if dataset_path.exists():
                dataset_path.unlink()
            if dataset_dir.exists():
                dataset_dir.rmdir()

    def test_select_cases_from_stable_dataset_is_deterministic(self):
        cases = [
            bench.GoldCase(
                case_id=f"case-{index}",
                source_file="x.jsonl",
                source_line=index,
                timestamp=f"2026-04-11 10:00:{index:02d}",
                bucket="small_change",
                source_text=f"text {index}",
                ai_input_text=bench.build_prompt(f"text {index}"),
                gold_raw_output=f"fixed {index}",
                historical_final_output=f"fixed {index}",
                request_metadata=bench.RequestMetadata(
                    prompt_text=bench.build_prompt(f"text {index}"),
                    store=True,
                    text_config={"verbosity": "medium"},
                    temperature=0.3,
                ),
            )
            for index in range(5)
        ]

        picked_a, summary_a = bench.select_cases_from_stable_dataset(
            cases,
            seed=7,
            per_bucket=2,
            requested_buckets=["small_change"],
        )
        picked_b, summary_b = bench.select_cases_from_stable_dataset(
            cases,
            seed=7,
            per_bucket=2,
            requested_buckets=["small_change"],
        )

        self.assertEqual([case.case_id for case in picked_a], [case.case_id for case in picked_b])
        self.assertEqual(summary_a["selected_by_bucket"]["small_change"], 2)
        self.assertEqual(summary_b["selected_by_bucket"]["small_change"], 2)

    def test_ahk_payload_builder_matches_model_branching(self):
        case = bench.GoldCase(
            case_id="case-1",
            source_file="x.jsonl",
            source_line=1,
            timestamp="2026-04-11 10:00:00",
            bucket="small_change",
            source_text='teh "cat"',
            ai_input_text=bench.build_prompt('teh "cat"'),
            gold_raw_output='the "cat"',
            historical_final_output='the "cat"',
            request_metadata=bench.RequestMetadata(
                prompt_text=bench.build_prompt('teh "cat"'),
                store=True,
                text_config={"verbosity": "medium"},
                temperature=0.3,
            ),
        )

        payload_41 = bench.build_ahk_request_payload_string(case, "gpt-4.1")
        self.assertIn('"model":"gpt-4.1"', payload_41)
        self.assertIn('"store":true', payload_41)
        self.assertIn('"text":{"verbosity":"medium"}', payload_41)
        self.assertIn('"temperature":0.3', payload_41)
        self.assertNotIn('"reasoning"', payload_41)
        self.assertIn(bench.json_escape_like_ahk(case.ai_input_text), payload_41)

        payload_51 = bench.build_ahk_request_payload_string(case, "gpt-5.1")
        self.assertIn('"text":{"verbosity":"low"}', payload_51)
        self.assertIn('"reasoning":{"effort":"none","summary":"auto"}', payload_51)
        self.assertNotIn('"temperature"', payload_51)

        payload_5mini = bench.build_ahk_request_payload_string(case, "gpt-5-mini")
        self.assertIn('"reasoning":{"effort":"minimal","summary":"auto"}', payload_5mini)

        payload_54mini = bench.build_ahk_request_payload_string(case, "gpt-5.4-mini")
        self.assertIn('"text":{"verbosity":"low"}', payload_54mini)
        self.assertIn('"reasoning":{"effort":"none","summary":"auto"}', payload_54mini)
        self.assertNotIn('"temperature"', payload_54mini)

        payload_54nano = bench.build_ahk_request_payload_string(case, "gpt-5.4-nano")
        self.assertIn('"text":{"verbosity":"low"}', payload_54nano)
        self.assertIn('"reasoning":{"effort":"none","summary":"auto"}', payload_54nano)
        self.assertNotIn('"temperature"', payload_54nano)

    def test_send_responses_request_raw_retries_after_429(self):
        class FakeResponse:
            def __init__(self, status_code, payload, headers=None):
                self.status_code = status_code
                self._payload = payload
                self.headers = httpx.Headers(headers or {})
                self.text = json.dumps(payload)

            def json(self):
                return self._payload

        class FakeClient:
            def __init__(self):
                self.calls = 0

            def post(self, *args, **kwargs):
                self.calls += 1
                if self.calls == 1:
                    return FakeResponse(
                        429,
                        {"error": {"message": "rate limited"}},
                        headers={"retry-after": "0"},
                    )
                return FakeResponse(
                    200,
                    {
                        "output": [
                            {
                                "content": [
                                    {"type": "output_text", "text": "the cat"}
                                ]
                            }
                        ]
                    },
                )

        client = FakeClient()
        result = bench.send_responses_request_raw(
            client,
            "test-key",
            '{"model":"gpt-5.4-mini"}',
            max_rate_limit_retries=1,
        )

        self.assertTrue(result.ok)
        self.assertEqual(client.calls, 2)
        self.assertEqual(result.retry_count, 1)
        self.assertTrue(result.rate_limit_observed)

    def test_classify_gold_bucket_variants(self):
        self.assertEqual(bench.classify_gold_bucket("hello", "hello"), "unchanged")
        self.assertEqual(
            bench.classify_gold_bucket("hello world", "Hello world"),
            "capitalization_only",
        )
        self.assertEqual(
            bench.classify_gold_bucket("hello world", "hello, world!"),
            "punctuation_spacing_only",
        )
        self.assertEqual(
            bench.classify_gold_bucket("hello world", "Hello, world!"),
            "capitalization_and_punctuation_spacing",
        )
        self.assertEqual(
            bench.classify_gold_bucket("teh cat", "the cat"),
            "small_change",
        )
        self.assertEqual(
            bench.classify_gold_bucket(
                "i went store yesterday and buy milk",
                "I went to the store yesterday and bought milk.",
            ),
            "medium_change",
        )
        self.assertEqual(
            bench.classify_gold_bucket(
                "milk",
                "Please send the updated document by Friday morning.",
            ),
            "large_change",
        )

    def test_classify_mismatch_severity(self):
        gold = "Hello, world!"
        self.assertEqual(bench.classify_mismatch(gold, gold), "exact")
        self.assertEqual(
            bench.classify_mismatch(gold, "Hello world"),
            "punct_or_spacing_only",
        )
        self.assertEqual(
            bench.classify_mismatch("The quick brown fox jumps.", "The quick brown fox leaps."),
            "minor_wording",
        )
        self.assertEqual(
            bench.classify_mismatch(
                "The meeting starts at three in the afternoon.",
                "The meeting will begin around four later today.",
            ),
            "moderate_wording",
        )
        self.assertEqual(
            bench.classify_mismatch(
                "Keep everything exactly the same.",
                "Completely unrelated response.",
            ),
            "major_difference",
        )
        self.assertEqual(
            bench.classify_mismatch("x", "", error="timeout"),
            "error_or_no_text",
        )

    def test_load_recent_gold_cases_tolerates_mixed_encoding(self):
        logs_dir = bench.SCRIPT_DIR / "tests" / "_runtime_logs"
        path = logs_dir / "spellcheck-2026-04-06-to-2026-04-12.jsonl"
        payload = {
            "timestamp": "2026-04-11 10:00:00",
            "status": "SUCCESS",
            "model": "gpt-4.1",
            "input_text": "teh bullet point",
            "output_text": "the bullet point",
            "raw_ai_output": "the bullet point",
            "raw_request": json.dumps(
                {
                    "model": "gpt-4.1",
                    "input": [
                        {
                            "role": "user",
                            "content": [
                                {"type": "input_text", "text": bench.build_prompt("teh bullet point")}
                            ],
                        }
                    ],
                    "store": True,
                    "text": {"verbosity": "medium"},
                    "temperature": 0.3,
                }
            ),
        }
        valid_line = json.dumps(payload, ensure_ascii=False)
        encoded = valid_line.replace("bullet", "bullet \u2022").encode("cp1252", errors="replace")

        logs_dir.mkdir(exist_ok=True)
        try:
            path.write_bytes(encoded + b"\n")

            cases, dataset_summary = bench.build_gold_cases(
                logs_dir,
                weeks=1,
                seed=1,
                per_bucket=5,
                requested_buckets=bench.DEFAULT_BUCKETS,
            )

            self.assertEqual(len(cases), 1)
            self.assertIn("\ufffd", cases[0].source_text)
            self.assertIn("instructions:", cases[0].ai_input_text)
            self.assertIn("\ufffd", cases[0].gold_raw_output)
            self.assertEqual(dataset_summary["selected_by_bucket"]["small_change"], 1)
        finally:
            if path.exists():
                path.unlink()
            if logs_dir.exists():
                logs_dir.rmdir()

    def test_build_gold_cases_excludes_one_and_two_word_inputs(self):
        logs_dir = bench.SCRIPT_DIR / "tests" / "_runtime_logs"
        path = logs_dir / "spellcheck-2026-04-06-to-2026-04-12.jsonl"
        entries = [
            {
                "timestamp": "2026-04-11 10:00:00",
                "status": "SUCCESS",
                "model": "gpt-4.1",
                "input_text": "teh",
                "output_text": "the",
                "raw_ai_output": "the",
                "raw_request": json.dumps(
                    {
                        "model": "gpt-4.1",
                        "input": [
                            {
                                "role": "user",
                                "content": [
                                    {"type": "input_text", "text": bench.build_prompt("teh")}
                                ],
                            }
                        ],
                        "store": True,
                        "text": {"verbosity": "medium"},
                        "temperature": 0.3,
                    }
                ),
            },
            {
                "timestamp": "2026-04-11 10:00:01",
                "status": "SUCCESS",
                "model": "gpt-4.1",
                "input_text": "teh cat",
                "output_text": "the cat",
                "raw_ai_output": "the cat",
                "raw_request": json.dumps(
                    {
                        "model": "gpt-4.1",
                        "input": [
                            {
                                "role": "user",
                                "content": [
                                    {"type": "input_text", "text": bench.build_prompt("teh cat")}
                                ],
                            }
                        ],
                        "store": True,
                        "text": {"verbosity": "medium"},
                        "temperature": 0.3,
                    }
                ),
            },
            {
                "timestamp": "2026-04-11 10:00:02",
                "status": "SUCCESS",
                "model": "gpt-4.1",
                "input_text": "teh black cat",
                "output_text": "the black cat",
                "raw_ai_output": "the black cat",
                "raw_request": json.dumps(
                    {
                        "model": "gpt-4.1",
                        "input": [
                            {
                                "role": "user",
                                "content": [
                                    {"type": "input_text", "text": bench.build_prompt("teh black cat")}
                                ],
                            }
                        ],
                        "store": True,
                        "text": {"verbosity": "medium"},
                        "temperature": 0.3,
                    }
                ),
            },
        ]

        logs_dir.mkdir(exist_ok=True)
        try:
            path.write_text("\n".join(json.dumps(entry) for entry in entries) + "\n", encoding="utf-8")
            cases, dataset_summary = bench.build_gold_cases(
                logs_dir,
                weeks=1,
                seed=1,
                per_bucket=5,
                requested_buckets=bench.DEFAULT_BUCKETS,
            )

            self.assertEqual(len(cases), 1)
            self.assertEqual(cases[0].source_text, "teh black cat")
            self.assertEqual(dataset_summary["excluded_short_input_cases"], 2)
            self.assertEqual(dataset_summary["min_source_words"], 3)
        finally:
            if path.exists():
                path.unlink()
            if logs_dir.exists():
                logs_dir.rmdir()

    def test_preflight_error_does_not_block_other_models(self):
        call_count = {"good-model": 0, "bad-model": 0}

        def fake_caller(payload_text):
            payload = json.loads(payload_text)
            model = payload["model"]
            call_count[model] += 1
            if model == "bad-model":
                return bench.ApiCallResult(
                    ok=False,
                    status_code=400,
                    api_ms=12.3,
                    raw_text='{"error":{"message":"unknown model"}}',
                    response_json={"error": {"message": "unknown model"}},
                    error="unknown model",
                )
            return bench.ApiCallResult(
                ok=True,
                status_code=200,
                api_ms=9.1,
                raw_text='{"output":[{"content":[{"type":"output_text","text":"the cat"}]}]}',
                response_json={
                    "output": [
                        {
                            "content": [
                                {"type": "output_text", "text": "the cat"}
                            ]
                        }
                    ]
                },
            )

        case = bench.GoldCase(
            case_id="case-1",
            source_file="x.jsonl",
            source_line=1,
            timestamp="2026-04-11 10:00:00",
            bucket="small_change",
            source_text="teh cat",
            ai_input_text=bench.build_prompt("teh cat"),
            gold_raw_output="the cat",
            historical_final_output="the cat",
            request_metadata=bench.RequestMetadata(
                prompt_text=bench.build_prompt("teh cat"),
                store=True,
                text_config={"verbosity": "medium"},
                temperature=0.3,
            ),
        )

        preflight, warmups, records = bench.run_benchmark(
            [case],
            ["good-model", "bad-model"],
            fake_caller,
            [],
        )

        self.assertEqual(preflight["good-model"]["status"], "ok")
        self.assertEqual(preflight["bad-model"]["status"], "error")
        self.assertIn("good-model", warmups)
        self.assertNotIn("bad-model", warmups)
        self.assertEqual(len(records), 1)
        self.assertEqual(records[0]["model"], "good-model")
        self.assertEqual(call_count["good-model"], 3)
        self.assertEqual(call_count["bad-model"], 1)

    def test_run_benchmark_waits_for_each_batch_before_starting_next(self):
        events = {
            "first_batch_ready": threading.Event(),
            "release_first_batch": threading.Event(),
        }
        state = {
            "call_count": 0,
            "main_call_count": 0,
            "started_before_release": 0,
        }
        lock = threading.Lock()

        def fake_caller(payload_text):
            payload = json.loads(payload_text)
            model = payload["model"]
            with lock:
                state["call_count"] += 1
                call_number = state["call_count"]

            if call_number <= 2:
                return bench.ApiCallResult(
                    ok=True,
                    status_code=200,
                    api_ms=5.0,
                    raw_text='{"output":[{"content":[{"type":"output_text","text":"the cat sat"}]}]}',
                    response_json={
                        "output": [{"content": [{"type": "output_text", "text": "the cat sat"}]}]
                    },
                )

            with lock:
                state["main_call_count"] += 1
                main_call_number = state["main_call_count"]
                if main_call_number <= 2:
                    if main_call_number == 2:
                        events["first_batch_ready"].set()
                elif not events["release_first_batch"].is_set():
                    state["started_before_release"] += 1

            if main_call_number <= 2:
                self.assertTrue(events["release_first_batch"].wait(timeout=2))

            return bench.ApiCallResult(
                ok=True,
                status_code=200,
                api_ms=5.0,
                raw_text='{"output":[{"content":[{"type":"output_text","text":"the cat sat"}]}]}',
                response_json={
                    "output": [{"content": [{"type": "output_text", "text": "the cat sat"}]}]
                },
            )

        cases = [
            bench.GoldCase(
                case_id=f"case-{index}",
                source_file="x.jsonl",
                source_line=index,
                timestamp=f"2026-04-11 10:00:0{index}",
                bucket="small_change",
                source_text="teh cat sat",
                ai_input_text=bench.build_prompt("teh cat sat"),
                gold_raw_output="the cat sat",
                historical_final_output="the cat sat",
                request_metadata=bench.RequestMetadata(
                    prompt_text=bench.build_prompt("teh cat sat"),
                    store=True,
                    text_config={"verbosity": "medium"},
                    temperature=0.3,
                ),
            )
            for index in range(4)
        ]

        result_holder = {}

        def run_target():
            result_holder["result"] = bench.run_benchmark(
                cases,
                ["good-model"],
                fake_caller,
                [],
                batch_size=2,
                progress_every=0,
            )

        thread = threading.Thread(target=run_target)
        thread.start()
        self.assertTrue(events["first_batch_ready"].wait(timeout=2))
        time.sleep(0.2)
        self.assertEqual(state["started_before_release"], 0)
        events["release_first_batch"].set()
        thread.join(timeout=5)
        self.assertFalse(thread.is_alive())
        self.assertIn("result", result_holder)


if __name__ == "__main__":
    unittest.main()
