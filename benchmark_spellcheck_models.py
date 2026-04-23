#!/usr/bin/env python3
"""Benchmark spell-check model regressions against historical gold outputs."""

from __future__ import annotations

import argparse
import concurrent.futures
import copy
import json
import os
import random
import re
import statistics
import threading
import time
from collections import Counter, defaultdict
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Any, Callable

import difflib
import httpx
from dotenv import load_dotenv
from google import genai
from google.genai import types as genai_types


SCRIPT_DIR = Path(__file__).resolve().parent
LOGS_DIR = SCRIPT_DIR / "logs"
BENCHMARKS_DIR = LOGS_DIR / "benchmarks"
DATASET_DIR = SCRIPT_DIR / "benchmark_data"
FINE_TUNE_DATA_DIR = SCRIPT_DIR / "fine_tune_data"
PREVIOUS_BATCHES_DIR = FINE_TUNE_DATA_DIR / "previous_batches"
LATEST_BATCH_DIR = FINE_TUNE_DATA_DIR / "latest_batch"
DEFAULT_DATASET_PATH = DATASET_DIR / "stable_spellcheck_cases.json"
REPLACEMENTS_PATH = SCRIPT_DIR / "replacements.json"
ENV_PATH = SCRIPT_DIR / ".env"
API_URL = "https://api.openai.com/v1/responses"
PROMPT_INSTRUCTION_TEXT = (
    "Fix the grammar and spelling of the text below. Preserve all formatting, "
    "line breaks, and special characters. Do not add or remove any content. "
    "Return only the corrected text."
)
WEEKLY_LOG_RE = re.compile(
    r"^spellcheck-(\d{4}-\d{2}-\d{2})-to-(\d{4}-\d{2}-\d{2})(?:-(\d+))?\.jsonl$"
)
URL_RE = re.compile(r"https?://\S+")
OUTPUT_TEXT_RE = re.compile(
    r'"type"\s*:\s*"output_text"[^}]*"text"\s*:\s*"((?:[^"\\]|\\.)*)"',
    re.DOTALL,
)

DEFAULT_MODELS = ["gpt-4.1", "gemini-2.5-flash-lite", "gemini-2.5-flash"]
DEFAULT_BUCKETS = [
    "unchanged",
    "capitalization_only",
    "punctuation_spacing_only",
    "capitalization_and_punctuation_spacing",
    "tiny_change",
    "small_change",
    "medium_change",
    "large_change",
]
MIN_SOURCE_WORDS = 3


@dataclass
class RequestMetadata:
    prompt_text: str | None
    store: bool | None
    text_config: dict[str, Any] | None
    temperature: float | None


@dataclass
class GoldCase:
    case_id: str
    source_file: str
    source_line: int
    timestamp: str
    bucket: str
    source_text: str
    ai_input_text: str
    gold_raw_output: str
    historical_final_output: str | None
    request_metadata: RequestMetadata
    dataset_name: str = "frozen_benchmark"


@dataclass
class ApiCallResult:
    ok: bool
    status_code: int | None
    api_ms: float
    raw_text: str
    response_json: dict[str, Any] | None
    error: str | None = None
    retry_count: int = 0
    rate_limit_observed: bool = False
    response_headers: dict[str, str] | None = None
    extracted_text: str | None = None
    request_info: dict[str, Any] | None = None


@dataclass
class AhkModelProfile:
    api_model: str
    api_uses_reasoning: bool
    verbosity: str
    temperature: float | None
    reasoning_effort: str
    reasoning_summary: str


def get_provider(model: str) -> str:
    if model.startswith("gemini-"):
        return "gemini"
    return "openai"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Replay historical spell-check cases against multiple models."
    )
    parser.add_argument("--models", nargs="+", default=DEFAULT_MODELS)
    parser.add_argument("--dataset", type=Path, default=None)
    parser.add_argument(
        "--refresh-dataset",
        action="store_true",
        help="Rebuild the stable dataset file from logs before running the benchmark.",
    )
    parser.add_argument(
        "--build-dataset-only",
        action="store_true",
        help="Rebuild the stable dataset file from logs and exit without calling any models.",
    )
    parser.add_argument("--weeks", type=int, default=8)
    parser.add_argument("--per-bucket", type=int, default=30)
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument(
        "--batch-size",
        "--max-concurrency",
        dest="batch_size",
        type=int,
        default=10,
        help=(
            "Number of independent API calls to fire in one batch before waiting for all "
            "of them to finish."
        ),
    )
    parser.add_argument(
        "--max-rate-limit-retries",
        type=int,
        default=2,
        help="Maximum number of retries after a 429 response.",
    )
    parser.add_argument(
        "--progress-every",
        type=int,
        default=10,
        help="Print one progress update after this many completed model calls.",
    )
    parser.add_argument(
        "--buckets",
        nargs="+",
        choices=DEFAULT_BUCKETS,
        default=DEFAULT_BUCKETS,
    )
    parser.add_argument("--output-dir", type=Path, default=None)
    return parser.parse_args()


def get_ahk_model_profile(model: str) -> AhkModelProfile:
    if model in {"gpt-4.1", "gpt-4.1-mini", "gpt-4.1-nano"} or model.startswith("ft:gpt-4.1"):
        return AhkModelProfile(
            api_model=model,
            api_uses_reasoning=False,
            verbosity="medium",
            temperature=0.3,
            reasoning_effort="none",
            reasoning_summary="auto",
        )
    if model == "gpt-5.1":
        return AhkModelProfile(
            api_model=model,
            api_uses_reasoning=True,
            verbosity="low",
            temperature=None,
            reasoning_effort="none",
            reasoning_summary="auto",
        )
    if model in {"gpt-5-mini", "gpt-5-nano"}:
        return AhkModelProfile(
            api_model=model,
            api_uses_reasoning=True,
            verbosity="low",
            temperature=None,
            reasoning_effort="minimal",
            reasoning_summary="auto",
        )
    if model in {"gpt-5.4", "gpt-5.4-mini", "gpt-5.4-nano"}:
        return AhkModelProfile(
            api_model=model,
            api_uses_reasoning=True,
            verbosity="low",
            temperature=None,
            reasoning_effort="none",
            reasoning_summary="auto",
        )
    if model.startswith("gpt-5"):
        return AhkModelProfile(
            api_model=model,
            api_uses_reasoning=True,
            verbosity="low",
            temperature=None,
            reasoning_effort="none",
            reasoning_summary="auto",
        )
    return AhkModelProfile(
        api_model=model,
        api_uses_reasoning=False,
        verbosity="medium",
        temperature=0.3,
        reasoning_effort="none",
        reasoning_summary="auto",
    )


def json_escape_like_ahk(value: str) -> str:
    return json.dumps(value, ensure_ascii=False)[1:-1]


def parse_retry_after_seconds(value: str | None) -> float | None:
    if not value:
        return None
    try:
        delay = float(value.strip())
    except ValueError:
        return None
    return delay if delay >= 0 else None


def extract_rate_limit_headers(headers: httpx.Headers) -> dict[str, str]:
    tracked = {}
    for key in [
        "retry-after",
        "x-ratelimit-limit-requests",
        "x-ratelimit-remaining-requests",
        "x-ratelimit-reset-requests",
        "x-ratelimit-limit-tokens",
        "x-ratelimit-remaining-tokens",
        "x-ratelimit-reset-tokens",
    ]:
        value = headers.get(key)
        if value is not None:
            tracked[key] = value
    return tracked


def compute_rate_limit_delay_seconds(headers: httpx.Headers, retry_index: int) -> float:
    retry_after = parse_retry_after_seconds(headers.get("retry-after"))
    if retry_after is not None:
        return min(max(retry_after, 0.5), 30.0)
    return min(2.0 * (2 ** retry_index), 30.0)


class ResponsesTransport:
    def __init__(self, api_key: str, max_rate_limit_retries: int) -> None:
        self.api_key = api_key
        self.max_rate_limit_retries = max_rate_limit_retries
        self._timeout = httpx.Timeout(connect=10.0, read=60.0, write=10.0, pool=10.0)
        self._local = threading.local()
        self._clients: list[httpx.Client] = []
        self._clients_lock = threading.Lock()

    def _get_client(self) -> httpx.Client:
        client = getattr(self._local, "client", None)
        if client is None:
            client = httpx.Client(timeout=self._timeout)
            self._local.client = client
            with self._clients_lock:
                self._clients.append(client)
        return client

    def send(self, payload_text: str) -> ApiCallResult:
        return send_responses_request_raw(
            self._get_client(),
            self.api_key,
            payload_text,
            max_rate_limit_retries=self.max_rate_limit_retries,
        )

    def close(self) -> None:
        with self._clients_lock:
            clients = list(self._clients)
            self._clients.clear()
        for client in clients:
            client.close()


class MultiProviderTransport:
    def __init__(
        self,
        api_keys: dict[str, str],
        max_rate_limit_retries: int = 2,
    ) -> None:
        self._max_rate_limit_retries = max_rate_limit_retries
        self._openai: ResponsesTransport | None = None
        if "openai" in api_keys:
            self._openai = ResponsesTransport(api_keys["openai"], max_rate_limit_retries)
        self._gemini_client: genai.Client | None = None
        if "gemini" in api_keys:
            self._gemini_client = genai.Client(api_key=api_keys["gemini"])

    def send(self, case: GoldCase, model: str) -> ApiCallResult:
        provider = get_provider(model)
        if provider == "openai":
            return self._send_openai(case, model)
        if provider == "gemini":
            return self._send_gemini(case, model)
        return ApiCallResult(
            False, None, 0, "", None,
            error=f"Unknown provider for model: {model}",
        )

    def _send_openai(self, case: GoldCase, model: str) -> ApiCallResult:
        if not self._openai:
            return ApiCallResult(False, None, 0, "", None, error="OPENAI_API_KEY not configured")
        payload_text = build_ahk_request_payload_string(case, model)
        result = self._openai.send(payload_text)
        result.request_info = {"provider": "openai", "payload": json.loads(payload_text)}
        return result

    def _send_gemini(self, case: GoldCase, model: str) -> ApiCallResult:
        if not self._gemini_client:
            return ApiCallResult(False, None, 0, "", None, error="GEMINI_API_KEY not configured")
        source_text = case.source_text
        config_kwargs: dict[str, Any] = {"temperature": 0.3}
        if "flash-lite" not in model:
            config_kwargs["thinking_config"] = genai_types.ThinkingConfig(thinking_budget=0)
        config = genai_types.GenerateContentConfig(
            system_instruction=PROMPT_INSTRUCTION_TEXT,
            **config_kwargs,
        )
        started = time.perf_counter()
        retry_count = 0
        rate_limit_observed = False
        while True:
            try:
                response = self._gemini_client.models.generate_content(
                    model=model,
                    contents=source_text,
                    config=config,
                )
                api_ms = round((time.perf_counter() - started) * 1000, 1)
                text = response.text or ""
                usage: dict[str, Any] = {}
                if response.usage_metadata:
                    usage = {
                        "input_tokens": response.usage_metadata.prompt_token_count,
                        "output_tokens": response.usage_metadata.candidates_token_count,
                        "total_tokens": response.usage_metadata.total_token_count,
                    }
                return ApiCallResult(
                    ok=True, status_code=200, api_ms=api_ms,
                    raw_text=text, response_json={"usage": usage},
                    extracted_text=text,
                    retry_count=retry_count,
                    rate_limit_observed=rate_limit_observed,
                    request_info={
                        "provider": "gemini", "model": model,
                        "temperature": 0.3,
                        "thinking_disabled": "flash-lite" not in model,
                    },
                )
            except Exception as exc:
                err_str = str(exc)
                is_rate_limit = "429" in err_str or "ResourceExhausted" in err_str
                if is_rate_limit:
                    rate_limit_observed = True
                    if retry_count < self._max_rate_limit_retries:
                        delay = min(2.0 * (2 ** retry_count), 30.0)
                        retry_count += 1
                        time.sleep(delay)
                        continue
                api_ms = round((time.perf_counter() - started) * 1000, 1)
                return ApiCallResult(
                    ok=False, status_code=429 if is_rate_limit else None,
                    api_ms=api_ms,
                    raw_text="", response_json=None, error=err_str,
                    retry_count=retry_count,
                    rate_limit_observed=rate_limit_observed,
                    request_info={"provider": "gemini", "model": model},
                )

    def close(self) -> None:
        if self._openai:
            self._openai.close()


def load_api_keys(models: list[str]) -> dict[str, str]:
    load_dotenv(ENV_PATH)
    keys: dict[str, str] = {}
    providers_needed = {get_provider(m) for m in models}
    if "openai" in providers_needed:
        key = os.environ.get("OPENAI_API_KEY", "").strip()
        if not key:
            raise RuntimeError(f"OPENAI_API_KEY is missing from {ENV_PATH}")
        keys["openai"] = key
    if "gemini" in providers_needed:
        key = os.environ.get("GEMINI_API_KEY", "").strip()
        if not key:
            raise RuntimeError(f"GEMINI_API_KEY is missing from {ENV_PATH}")
        keys["gemini"] = key
    return keys


def load_replacements(path: Path) -> list[tuple[str, str]]:
    if not path.exists():
        return []

    raw_json = path.read_text(encoding="utf-8")
    if raw_json.startswith("\ufeff"):
        raw_json = raw_json[1:]

    payload = json.loads(raw_json)
    pairs: list[tuple[str, str]] = []
    for canonical, variants in payload.items():
        if not isinstance(variants, list):
            continue
        for variant in variants:
            if isinstance(variant, str) and variant and variant != canonical:
                pairs.append((variant, canonical))

    pairs.sort(key=lambda pair: len(pair[0]), reverse=True)
    return pairs


def apply_replacements(text: str, replacements: list[tuple[str, str]]) -> tuple[str, list[str], int]:
    urls: list[str] = []

    def protect_url(match: re.Match[str]) -> str:
        urls.append(match.group(0))
        return f"__URL_{len(urls)}__"

    protected = URL_RE.sub(protect_url, text)
    applied: list[str] = []

    for variant, canonical in replacements:
        count = protected.count(variant)
        if count:
            protected = protected.replace(variant, canonical)
            applied.append(f"{variant} -> {canonical}")

    for index in range(len(urls), 0, -1):
        protected = protected.replace(f"__URL_{index}__", urls[index - 1])

    return protected, applied, len(urls)


def strip_prompt_leak(text: str, prompt_text: str) -> tuple[str, dict[str, Any]]:
    details = {
        "triggered": False,
        "occurrences": 0,
        "text_input_removed": False,
        "removed_chars": 0,
        "before_length": len(text),
        "after_length": len(text),
    }
    if not prompt_text:
        return text, details

    leaked_prompt_line = f"instructions: {prompt_text}"
    occurrences = text.count(leaked_prompt_line)
    if not occurrences:
        return text, details

    updated = text.replace(leaked_prompt_line, "")
    updated = updated.lstrip(" \t\r\n")
    label = "text input:"
    if updated.startswith(label):
        updated = updated[len(label) :].lstrip(" \t\r\n")
        details["text_input_removed"] = True

    details["triggered"] = True
    details["occurrences"] = occurrences
    details["after_length"] = len(updated)
    details["removed_chars"] = details["before_length"] - details["after_length"]
    return updated, details


def build_prompt(input_text: str) -> str:
    return f"instructions: {PROMPT_INSTRUCTION_TEXT}\ntext input: {input_text}"


def extract_source_text_from_prompt(prompt_text: str) -> str:
    prefix = f"instructions: {PROMPT_INSTRUCTION_TEXT}\ntext input:"
    if prompt_text.startswith(prefix):
        return prompt_text[len(prefix) :].lstrip(" ")

    match = re.search(r"(?is)^instructions:\s*.*?\ntext input:\s*(.*)$", prompt_text)
    if match:
        return match.group(1)
    return ""


def normalize_line_endings(text: str) -> str:
    return text.replace("\r\n", "\n").replace("\r", "\n")


def collapse_alnum(text: str) -> str:
    return re.sub(r"[^A-Za-z0-9]", "", normalize_line_endings(text)).lower()


def strip_non_alnum_preserve_case(text: str) -> str:
    return re.sub(r"[^A-Za-z0-9]", "", normalize_line_endings(text))


def classify_gold_bucket(input_text: str, output_text: str) -> str:
    normalized_input = normalize_line_endings(input_text)
    normalized_output = normalize_line_endings(output_text)
    if normalized_input == normalized_output:
        return "unchanged"

    input_alnum_case = strip_non_alnum_preserve_case(normalized_input)
    output_alnum_case = strip_non_alnum_preserve_case(normalized_output)
    if input_alnum_case == output_alnum_case:
        return "punctuation_spacing_only"

    if input_alnum_case.lower() == output_alnum_case.lower():
        if normalized_input.lower() == normalized_output.lower():
            return "capitalization_only"
        return "capitalization_and_punctuation_spacing"

    ratio = difflib.SequenceMatcher(a=input_text, b=output_text).ratio()
    if ratio >= 0.98:
        return "tiny_change"
    if ratio >= 0.85:
        return "small_change"
    if ratio >= 0.65:
        return "medium_change"
    return "large_change"


def tokenize_words(text: str) -> list[str]:
    return re.findall(r"[A-Za-z0-9']+", normalize_line_endings(text).lower())


def source_word_count(text: str) -> int:
    return len(tokenize_words(text))


def is_case_eligible(case: GoldCase) -> bool:
    return source_word_count(case.source_text) >= MIN_SOURCE_WORDS


def classify_mismatch(gold_output: str, candidate_output: str, error: str | None = None) -> str:
    if error or not candidate_output:
        return "error_or_no_text"
    if candidate_output == gold_output:
        return "exact"
    if collapse_alnum(candidate_output) == collapse_alnum(gold_output):
        return "punct_or_spacing_only"

    gold_tokens = tokenize_words(gold_output)
    candidate_tokens = tokenize_words(candidate_output)
    token_matcher = difflib.SequenceMatcher(a=gold_tokens, b=candidate_tokens)
    changed_tokens = 0
    for opcode, i1, i2, j1, j2 in token_matcher.get_opcodes():
        if opcode != "equal":
            changed_tokens += max(i2 - i1, j2 - j1)

    total_tokens = max(len(gold_tokens), len(candidate_tokens), 1)
    change_ratio = changed_tokens / total_tokens
    token_similarity = token_matcher.ratio()
    common_tokens = len(set(gold_tokens) & set(candidate_tokens))
    if changed_tokens <= 1 or change_ratio <= 0.10:
        return "minor_wording"
    if token_similarity < 0.30 and common_tokens == 0:
        return "major_difference"
    if changed_tokens <= 6 or change_ratio <= 0.75:
        return "moderate_wording"
    return "major_difference"


def weekly_log_files(log_dir: Path) -> list[Path]:
    grouped: dict[tuple[str, str], list[tuple[int, Path]]] = defaultdict(list)
    for path in log_dir.glob("spellcheck-*.jsonl"):
        match = WEEKLY_LOG_RE.match(path.name)
        if not match:
            continue
        start_stamp, end_stamp, suffix = match.groups()
        suffix_index = int(suffix) if suffix else 0
        grouped[(start_stamp, end_stamp)].append((suffix_index, path))

    files: list[Path] = []
    for _, paths in sorted(grouped.items()):
        for _, path in sorted(paths):
            files.append(path)
    return files


def select_recent_week_groups(log_dir: Path, weeks: int) -> list[Path]:
    all_files = weekly_log_files(log_dir)
    ordered_groups: list[tuple[str, str]] = []
    for path in all_files:
        match = WEEKLY_LOG_RE.match(path.name)
        assert match is not None
        group = (match.group(1), match.group(2))
        if group not in ordered_groups:
            ordered_groups.append(group)

    selected_groups = set(ordered_groups[-weeks:])
    return [path for path in all_files if WEEKLY_LOG_RE.match(path.name).groups()[:2] in selected_groups]


def parse_request_metadata(raw_request: str | None) -> RequestMetadata:
    if not raw_request:
        return RequestMetadata(None, None, None, None)

    try:
        request_json = json.loads(raw_request)
    except json.JSONDecodeError:
        return RequestMetadata(None, None, None, None)

    prompt_text = None
    for item in request_json.get("input", []):
        if not isinstance(item, dict):
            continue
        for content in item.get("content", []):
            if (
                isinstance(content, dict)
                and content.get("type") == "input_text"
                and isinstance(content.get("text"), str)
            ):
                prompt_text = content["text"]
                break
        if prompt_text is not None:
            break

    text_config = request_json.get("text")
    if not isinstance(text_config, dict):
        text_config = None

    temperature = request_json.get("temperature")
    if not isinstance(temperature, (int, float)):
        temperature = None

    store = request_json.get("store")
    if not isinstance(store, bool):
        store = None

    return RequestMetadata(prompt_text, store, text_config, temperature)


def iter_jsonl_entries(paths: list[Path]) -> list[tuple[Path, int, dict[str, Any]]]:
    entries: list[tuple[Path, int, dict[str, Any]]] = []
    for path in paths:
        with path.open(encoding="utf-8", errors="replace") as handle:
            for line_no, raw_line in enumerate(handle, 1):
                line = raw_line.strip()
                if not line:
                    continue
                try:
                    payload = json.loads(line)
                except json.JSONDecodeError:
                    continue
                entries.append((path, line_no, payload))
    return entries


def build_versioned_dataset_path(now: datetime | None = None) -> Path:
    stamp = (now or datetime.now()).strftime("%Y-%m-%d-%H%M%S")
    return DATASET_DIR / f"stable_spellcheck_cases-{stamp}.json"


def resolve_default_dataset_path() -> Path:
    versioned = sorted(DATASET_DIR.glob("stable_spellcheck_cases-*.json"))
    if versioned:
        return versioned[-1]
    if DEFAULT_DATASET_PATH.exists():
        return DEFAULT_DATASET_PATH
    return DEFAULT_DATASET_PATH


def gold_case_to_record(case: GoldCase) -> dict[str, Any]:
    return {
        "case_id": case.case_id,
        "dataset_name": case.dataset_name,
        "source_file": case.source_file,
        "source_line": case.source_line,
        "timestamp": case.timestamp,
        "bucket": case.bucket,
        "source_text": case.source_text,
        "ai_input_text": case.ai_input_text,
        "gold_raw_output": case.gold_raw_output,
        "historical_final_output": case.historical_final_output,
        "request_metadata": {
            "prompt_text": case.request_metadata.prompt_text,
            "store": case.request_metadata.store,
            "text_config": case.request_metadata.text_config,
            "temperature": case.request_metadata.temperature,
        },
    }


def gold_case_from_record(record: dict[str, Any]) -> GoldCase:
    request_metadata = record.get("request_metadata") or {}
    source_text = str(record.get("source_text", ""))
    gold_raw_output = str(record.get("gold_raw_output", ""))
    return GoldCase(
        case_id=str(record.get("case_id", "")),
        dataset_name=str(record.get("dataset_name", "frozen_benchmark")),
        source_file=str(record.get("source_file", "")),
        source_line=int(record.get("source_line", 0)),
        timestamp=str(record.get("timestamp", "")),
        bucket=classify_gold_bucket(source_text, gold_raw_output),
        source_text=source_text,
        ai_input_text=str(record.get("ai_input_text", "")),
        gold_raw_output=gold_raw_output,
        historical_final_output=(
            str(record["historical_final_output"])
            if record.get("historical_final_output") is not None
            else None
        ),
        request_metadata=RequestMetadata(
            prompt_text=request_metadata.get("prompt_text"),
            store=request_metadata.get("store"),
            text_config=request_metadata.get("text_config"),
            temperature=request_metadata.get("temperature"),
        ),
    )


def save_stable_dataset(
    dataset_path: Path,
    cases: list[GoldCase],
    dataset_summary: dict[str, Any],
) -> None:
    dataset_path.parent.mkdir(parents=True, exist_ok=True)
    payload = {
        "dataset_path": str(dataset_path),
        "case_count": len(cases),
        "dataset_summary": dataset_summary,
        "created_at": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
        "cases": [gold_case_to_record(case) for case in cases],
    }
    dataset_path.write_text(json.dumps(payload, indent=2, ensure_ascii=False), encoding="utf-8")


def load_stable_dataset(dataset_path: Path) -> tuple[list[GoldCase], dict[str, Any]]:
    if not dataset_path.exists():
        raise RuntimeError(f"Stable dataset file not found: {dataset_path}")

    payload = json.loads(dataset_path.read_text(encoding="utf-8", errors="replace"))
    raw_cases = payload.get("cases") or []
    all_cases = [gold_case_from_record(record) for record in raw_cases]
    cases = [case for case in all_cases if is_case_eligible(case)]
    dataset_summary: dict[str, Any] = {
        "dataset_path": str(dataset_path),
        "selected_by_bucket": dict(Counter(case.bucket for case in cases)),
        "case_count": len(cases),
        "source": "stable_dataset",
        "excluded_short_input_cases": len(all_cases) - len(cases),
        "min_source_words": MIN_SOURCE_WORDS,
    }
    if isinstance(payload, dict):
        dataset_summary.update(payload.get("dataset_summary") or {})
        dataset_summary["dataset_created_at"] = payload.get("created_at")
    dataset_summary["dataset_path"] = str(dataset_path)
    dataset_summary["source"] = "stable_dataset"
    dataset_summary["case_count"] = len(cases)
    dataset_summary["selected_by_bucket"] = dict(Counter(case.bucket for case in cases))
    dataset_summary["excluded_short_input_cases"] = len(all_cases) - len(cases)
    dataset_summary["min_source_words"] = MIN_SOURCE_WORDS
    return cases, dataset_summary


def load_finetune_jsonl_pairs(path: Path) -> list[tuple[str, str]]:
    pairs: list[tuple[str, str]] = []
    if not path.exists():
        return pairs

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
            user = messages[0].get("content") if isinstance(messages[0], dict) else None
            assistant = messages[1].get("content") if isinstance(messages[1], dict) else None
            if isinstance(user, str) and isinstance(assistant, str):
                pairs.append((user, assistant))
    return pairs


def load_finetune_eval_cases(dataset_dir: Path, dataset_name: str) -> tuple[list[GoldCase], dict[str, Any]]:
    seen_pairs: set[tuple[str, str]] = set()
    cases: list[GoldCase] = []
    excluded_short_input_cases = 0
    files_used = 0

    for file_name in ("train.jsonl", "validation.jsonl"):
        path = dataset_dir / file_name
        if not path.exists():
            continue
        files_used += 1
        for prompt_text, assistant_text in load_finetune_jsonl_pairs(path):
            pair = (prompt_text, assistant_text)
            if pair in seen_pairs:
                continue
            seen_pairs.add(pair)
            source_text = extract_source_text_from_prompt(prompt_text)
            if not source_text:
                continue
            if source_word_count(source_text) < MIN_SOURCE_WORDS:
                excluded_short_input_cases += 1
                continue
            cases.append(
                GoldCase(
                    case_id=f"{dataset_name}:{len(cases) + 1}",
                    source_file=path.name,
                    source_line=len(cases) + 1,
                    timestamp="",
                    bucket=classify_gold_bucket(source_text, assistant_text),
                    source_text=source_text,
                    ai_input_text=prompt_text,
                    gold_raw_output=assistant_text,
                    historical_final_output=assistant_text,
                    request_metadata=RequestMetadata(
                        prompt_text=prompt_text,
                        store=True,
                        text_config={"verbosity": "medium"},
                        temperature=0.3,
                    ),
                    dataset_name=dataset_name,
                )
            )

    summary = {
        "dataset_name": dataset_name,
        "source": str(dataset_dir),
        "files_used": files_used,
        "case_count": len(cases),
        "selected_by_bucket": dict(Counter(case.bucket for case in cases)),
        "excluded_short_input_cases": excluded_short_input_cases,
    }
    return cases, summary


def load_all_eval_datasets() -> tuple[list[GoldCase], dict[str, dict[str, Any]]]:
    cases: list[GoldCase] = []
    summaries: dict[str, dict[str, Any]] = {}

    if PREVIOUS_BATCHES_DIR.exists():
        for dataset_dir in sorted(path for path in PREVIOUS_BATCHES_DIR.iterdir() if path.is_dir()):
            dataset_name = f"previous_batches/{dataset_dir.name}"
            dataset_cases, dataset_summary = load_finetune_eval_cases(dataset_dir, dataset_name)
            if dataset_cases:
                cases.extend(dataset_cases)
                summaries[dataset_name] = dataset_summary

    if LATEST_BATCH_DIR.exists():
        dataset_cases, dataset_summary = load_finetune_eval_cases(LATEST_BATCH_DIR, "latest_batch")
        if dataset_cases:
            cases.extend(dataset_cases)
            summaries["latest_batch"] = dataset_summary

    return cases, summaries


def select_cases_from_stable_dataset(
    cases: list[GoldCase],
    seed: int,
    per_bucket: int,
    requested_buckets: list[str],
    dataset_summary: dict[str, Any] | None = None,
) -> tuple[list[GoldCase], dict[str, Any]]:
    bucket_pool = {bucket: [] for bucket in DEFAULT_BUCKETS}
    for case in cases:
        if case.bucket in bucket_pool:
            bucket_pool[case.bucket].append(case)

    rng = random.Random(seed)
    selected_cases: list[GoldCase] = []
    bucket_availability = {bucket: len(items) for bucket, items in bucket_pool.items()}

    for bucket in requested_buckets:
        items = list(bucket_pool[bucket])
        rng.shuffle(items)
        selected_cases.extend(items[:per_bucket])

    selected_cases.sort(key=lambda case: (DEFAULT_BUCKETS.index(case.bucket), case.timestamp, case.case_id))
    summary = dict(dataset_summary or {})
    summary["available_by_bucket"] = bucket_availability
    summary["requested_buckets"] = requested_buckets
    summary["selected_by_bucket"] = dict(Counter(case.bucket for case in selected_cases))
    summary["seed"] = seed
    summary["per_bucket"] = per_bucket
    summary["selected_bucket_shortfalls"] = {
        bucket: max(0, per_bucket - bucket_availability.get(bucket, 0))
        for bucket in requested_buckets
        if bucket_availability.get(bucket, 0) < per_bucket
    }
    return selected_cases, summary


def build_gold_cases(
    log_dir: Path,
    weeks: int,
    seed: int,
    per_bucket: int,
    requested_buckets: list[str],
) -> tuple[list[GoldCase], dict[str, Any]]:
    selected_files = select_recent_week_groups(log_dir, weeks)
    if not selected_files:
        raise RuntimeError(f"No weekly spellcheck logs found in {log_dir}")

    entries = iter_jsonl_entries(selected_files)
    deduped: dict[tuple[str, str], GoldCase] = {}
    bucket_pool = {bucket: [] for bucket in DEFAULT_BUCKETS}
    newest_first = sorted(entries, key=lambda item: item[2].get("timestamp", ""), reverse=True)
    excluded_short_input_cases = 0

    for path, line_no, entry in newest_first:
        if entry.get("status") != "SUCCESS":
            continue
        if entry.get("model") != "gpt-4.1":
            continue
        raw_ai_output = entry.get("raw_ai_output")
        historical_final_output = entry.get("output_text")
        if not isinstance(raw_ai_output, str):
            continue

        metadata = parse_request_metadata(entry.get("raw_request"))
        if not metadata.prompt_text:
            continue
        source_text = extract_source_text_from_prompt(metadata.prompt_text)
        if not source_text:
            fallback_input = entry.get("input_text")
            if isinstance(fallback_input, str):
                source_text = fallback_input
        if not source_text:
            continue
        if source_word_count(source_text) < MIN_SOURCE_WORDS:
            excluded_short_input_cases += 1
            continue

        key = (metadata.prompt_text, raw_ai_output)
        if key in deduped:
            continue

        bucket = classify_gold_bucket(source_text, raw_ai_output)
        case = GoldCase(
            case_id=f"{path.stem}:{line_no}",
            source_file=path.name,
            source_line=line_no,
            timestamp=str(entry.get("timestamp", "")),
            bucket=bucket,
            source_text=source_text,
            ai_input_text=metadata.prompt_text,
            gold_raw_output=raw_ai_output,
            historical_final_output=historical_final_output if isinstance(historical_final_output, str) else None,
            request_metadata=metadata,
            dataset_name="frozen_benchmark",
        )
        deduped[key] = case
        bucket_pool[bucket].append(case)

    rng = random.Random(seed)
    selected_cases: list[GoldCase] = []
    requested_set = set(requested_buckets)
    bucket_availability = {bucket: len(cases) for bucket, cases in bucket_pool.items()}

    for bucket in requested_buckets:
        cases = list(bucket_pool[bucket])
        rng.shuffle(cases)
        selected_cases.extend(cases[:per_bucket])

    selected_cases.sort(key=lambda case: (DEFAULT_BUCKETS.index(case.bucket), case.timestamp, case.case_id))
    if not selected_cases:
        raise RuntimeError("No benchmark cases selected from the requested buckets")

    dataset_summary = {
        "selected_files": [path.name for path in selected_files],
        "selected_group_count": len(
            {
                WEEKLY_LOG_RE.match(path.name).groups()[:2]
                for path in selected_files
                if WEEKLY_LOG_RE.match(path.name)
            }
        ),
        "deduped_case_count": len(deduped),
        "requested_buckets": requested_buckets,
        "available_by_bucket": bucket_availability,
        "selected_by_bucket": dict(Counter(case.bucket for case in selected_cases)),
        "seed": seed,
        "per_bucket": per_bucket,
        "filtered_models": ["gpt-4.1"],
        "weeks": weeks,
        "excluded_short_input_cases": excluded_short_input_cases,
        "min_source_words": MIN_SOURCE_WORDS,
    }
    dataset_summary["selected_bucket_shortfalls"] = {
        bucket: max(0, per_bucket - bucket_availability.get(bucket, 0))
        for bucket in requested_set
        if bucket_availability.get(bucket, 0) < per_bucket
    }
    return selected_cases, dataset_summary


def build_ahk_request_payload_string(case: GoldCase, model: str) -> str:
    profile = get_ahk_model_profile(model)
    prompt_text = case.ai_input_text or build_prompt(case.source_text)
    escaped_prompt = json_escape_like_ahk(prompt_text)
    payload = (
        '{"model":"' + profile.api_model
        + '","input":[{"role":"user","content":[{"type":"input_text","text":"'
        + escaped_prompt
        + '"}]}],"store":true,"text":{"verbosity":"'
        + profile.verbosity
        + '"}'
    )
    if profile.api_uses_reasoning:
        payload += (
            ',"reasoning":{"effort":"'
            + profile.reasoning_effort
            + '","summary":"'
            + profile.reasoning_summary
            + '"}}'
        )
    else:
        payload += ',"temperature":' + str(profile.temperature) + "}"
    return payload


def build_probe_case() -> GoldCase:
    return GoldCase(
        case_id="preflight-probe",
        source_file="",
        source_line=0,
        timestamp="",
        bucket="tiny_change",
        source_text="teh cat sat on teh mat",
        ai_input_text=build_prompt("teh cat sat on teh mat"),
        gold_raw_output="the cat sat on the mat",
        historical_final_output="the cat sat on the mat",
        request_metadata=RequestMetadata(None, True, {"verbosity": "medium"}, 0.3),
    )


def unescape_response_text(text: str) -> str:
    return json.loads(f'"{text}"')


def extract_output_text(response_json: dict[str, Any] | None, raw_text: str) -> str:
    if isinstance(response_json, dict):
        queue: list[Any] = [response_json]
        while queue:
            current = queue.pop(0)
            if isinstance(current, dict):
                if current.get("type") == "output_text" and isinstance(current.get("text"), str):
                    return current["text"]
                queue.extend(current.values())
            elif isinstance(current, list):
                queue.extend(current)

    match = OUTPUT_TEXT_RE.search(raw_text)
    if match:
        return unescape_response_text(match.group(1))
    return ""


def send_responses_request_raw(
    client: httpx.Client,
    api_key: str,
    payload_text: str,
    max_rate_limit_retries: int = 2,
) -> ApiCallResult:
    started = time.perf_counter()
    retry_count = 0
    rate_limit_observed = False
    last_headers: dict[str, str] | None = None

    while True:
        try:
            response = client.post(
                API_URL,
                headers={
                    "authorization": f"Bearer {api_key}",
                    "content-type": "application/json; charset=utf-8",
                },
                content=payload_text.encode("utf-8"),
            )
            api_ms = round((time.perf_counter() - started) * 1000, 1)
            raw_text = response.text
            response_json = None
            try:
                response_json = response.json()
            except json.JSONDecodeError:
                response_json = None

            last_headers = extract_rate_limit_headers(response.headers)
            if response.status_code == 429:
                rate_limit_observed = True
                if retry_count < max_rate_limit_retries:
                    delay = compute_rate_limit_delay_seconds(response.headers, retry_count)
                    retry_count += 1
                    time.sleep(delay)
                    continue

            if response.status_code != 200:
                error = None
                if isinstance(response_json, dict):
                    err = response_json.get("error")
                    if isinstance(err, dict):
                        error = err.get("message") or err.get("code")
                    elif isinstance(err, str):
                        error = err
                return ApiCallResult(
                    False,
                    response.status_code,
                    api_ms,
                    raw_text,
                    response_json,
                    error,
                    retry_count=retry_count,
                    rate_limit_observed=rate_limit_observed,
                    response_headers=last_headers,
                )

            return ApiCallResult(
                True,
                response.status_code,
                api_ms,
                raw_text,
                response_json,
                None,
                retry_count=retry_count,
                rate_limit_observed=rate_limit_observed,
                response_headers=last_headers,
            )
        except httpx.HTTPError as exc:
            api_ms = round((time.perf_counter() - started) * 1000, 1)
            return ApiCallResult(
                False,
                None,
                api_ms,
                "",
                None,
                str(exc),
                retry_count=retry_count,
                rate_limit_observed=rate_limit_observed,
                response_headers=last_headers,
            )


def preflight_models(
    models: list[str],
    transport: MultiProviderTransport,
) -> dict[str, dict[str, Any]]:
    statuses: dict[str, dict[str, Any]] = {}
    probe_case = build_probe_case()

    for model in models:
        result = transport.send(probe_case, model)
        statuses[model] = {
            "status": "ok" if result.ok else "error",
            "status_code": result.status_code,
            "api_ms": result.api_ms,
            "error": result.error,
        }
    return statuses


def finalize_output(raw_text: str, replacements: list[tuple[str, str]]) -> tuple[str, dict[str, Any], list[str], int]:
    replaced_text, applied_replacements, url_count = apply_replacements(raw_text, replacements)
    final_text, prompt_guard = strip_prompt_leak(replaced_text, PROMPT_INSTRUCTION_TEXT)
    return final_text, prompt_guard, applied_replacements, url_count


def run_single_case(
    case: GoldCase,
    model: str,
    transport: MultiProviderTransport,
    replacements: list[tuple[str, str]],
) -> dict[str, Any]:
    started = time.perf_counter()
    result = transport.send(case, model)
    payload = result.request_info or {}
    payload_text = json.dumps(payload, ensure_ascii=False)
    raw_output = ""
    postprocessed_output = ""
    normalized_output = ""
    prompt_guard = {
        "triggered": False,
        "occurrences": 0,
        "text_input_removed": False,
        "removed_chars": 0,
        "before_length": 0,
        "after_length": 0,
    }
    applied_replacements: list[str] = []
    urls_protected = 0

    if result.ok:
        if result.extracted_text is not None:
            raw_output = result.extracted_text
        else:
            raw_output = extract_output_text(result.response_json, result.raw_text)
        postprocessed_output, prompt_guard, applied_replacements, urls_protected = finalize_output(
            raw_output,
            replacements,
        )
        normalized_output = normalize_line_endings(raw_output)

    total_ms = round((time.perf_counter() - started) * 1000, 1)
    mismatch_bucket = classify_mismatch(case.gold_raw_output, raw_output, result.error)
    postprocessed_mismatch_bucket = None
    postprocessed_exact_match = None
    if case.historical_final_output is not None:
        postprocessed_mismatch_bucket = classify_mismatch(
            case.historical_final_output,
            postprocessed_output,
            result.error,
        )
        postprocessed_exact_match = postprocessed_mismatch_bucket == "exact"

    usage = {}
    if isinstance(result.response_json, dict):
        usage_block = result.response_json.get("usage")
        if isinstance(usage_block, dict):
            usage = usage_block

    return {
        "case_id": case.case_id,
        "dataset_name": case.dataset_name,
        "model": model,
        "gold_bucket": case.bucket,
        "source_file": case.source_file,
        "source_line": case.source_line,
        "timestamp": case.timestamp,
        "source_text": case.source_text,
        "ai_input_text": case.ai_input_text,
        "input_text": case.source_text,
        "gold_raw_output": case.gold_raw_output,
        "gold_output": case.gold_raw_output,
        "historical_final_output": case.historical_final_output,
        "raw_output": raw_output,
        "final_output": postprocessed_output,
        "postprocessed_output": postprocessed_output,
        "normalized_output": normalized_output,
        "exact_match": mismatch_bucket == "exact",
        "mismatch_bucket": mismatch_bucket,
        "postprocessed_exact_match": postprocessed_exact_match,
        "postprocessed_mismatch_bucket": postprocessed_mismatch_bucket,
        "api_ms": result.api_ms,
        "total_ms": total_ms,
        "status_code": result.status_code,
        "error": result.error,
        "retry_count": result.retry_count,
        "rate_limit_observed": result.rate_limit_observed,
        "rate_limit_headers": result.response_headers or {},
        "usage": usage,
        "replacements_applied": applied_replacements,
        "urls_protected": urls_protected,
        "prompt_leak_guard": prompt_guard,
        "payload": payload,
        "payload_text": payload_text,
        "raw_response": result.raw_text,
    }


def percentile(values: list[float], pct: float) -> float | None:
    if not values:
        return None
    if len(values) == 1:
        return values[0]
    ordered = sorted(values)
    index = (len(ordered) - 1) * pct
    lower = int(index)
    upper = min(lower + 1, len(ordered) - 1)
    if lower == upper:
        return ordered[lower]
    fraction = index - lower
    return round(ordered[lower] + ((ordered[upper] - ordered[lower]) * fraction), 1)


def summarize_records(records: list[dict[str, Any]]) -> dict[str, Any]:
    summary: dict[str, Any] = {}
    grouped: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for record in records:
        grouped[record["model"]].append(record)

    for model, rows in grouped.items():
        api_values = [row["api_ms"] for row in rows if row["api_ms"] is not None]
        total_values = [row["total_ms"] for row in rows if row["total_ms"] is not None]
        mismatches = Counter(row["mismatch_bucket"] for row in rows)
        gold_bucket_matrix: dict[str, dict[str, int]] = {}
        by_gold_bucket: dict[str, list[dict[str, Any]]] = defaultdict(list)
        for row in rows:
            by_gold_bucket[row["gold_bucket"]].append(row)
        for gold_bucket, bucket_rows in by_gold_bucket.items():
            gold_bucket_matrix[gold_bucket] = dict(
                Counter(item["mismatch_bucket"] for item in bucket_rows)
            )

        summary[model] = {
            "cases": len(rows),
            "exact_matches": mismatches.get("exact", 0),
            "exact_match_rate": round(mismatches.get("exact", 0) / len(rows) * 100, 1) if rows else 0.0,
            "mismatch_counts": dict(mismatches),
            "rate_limit_observed_calls": sum(1 for row in rows if row.get("rate_limit_observed")),
            "rate_limit_error_calls": sum(1 for row in rows if row.get("status_code") == 429),
            "retry_count_total": sum(int(row.get("retry_count") or 0) for row in rows),
            "api_ms": {
                "median": round(statistics.median(api_values), 1) if api_values else None,
                "p90": percentile(api_values, 0.90),
            },
            "total_ms": {
                "median": round(statistics.median(total_values), 1) if total_values else None,
                "p90": percentile(total_values, 0.90),
            },
            "gold_bucket_matrix": gold_bucket_matrix,
        }
    return summary


def summarize_records_by_dataset(records: list[dict[str, Any]]) -> dict[str, dict[str, Any]]:
    grouped: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for record in records:
        grouped[record["dataset_name"]].append(record)
    return {dataset_name: summarize_records(rows) for dataset_name, rows in grouped.items()}


def render_summary_markdown(
    run_started_at: str,
    config: dict[str, Any],
    frozen_dataset_summary: dict[str, Any],
    eval_dataset_summaries: dict[str, dict[str, Any]],
    preflight: dict[str, Any],
    warmups: dict[str, Any],
    model_summary: dict[str, Any],
    dataset_model_summaries: dict[str, dict[str, Any]],
) -> str:
    lines = [
        "# Spellcheck Benchmark Summary",
        "",
        f"- Run started: `{run_started_at}`",
        f"- Models: `{', '.join(config['models'])}`",
        f"- Frozen benchmark dataset: `{config['dataset_path']}`",
        f"- Frozen dataset refreshed this run: `{config['dataset_refreshed']}`",
        f"- Weeks: `{config['weeks']}`",
        f"- Per bucket target: `{config['per_bucket']}`",
        f"- Seed: `{config['seed']}`",
        f"- Batch size: `{config['batch_size']}`",
        "",
        "## Evaluation Sets",
        "",
        "### frozen_benchmark",
        "",
        f"- Gold files: `{len(frozen_dataset_summary['selected_files'])}`",
        f"- Deduped cases considered: `{frozen_dataset_summary['deduped_case_count']}`",
        f"- Minimum source words: `{frozen_dataset_summary.get('min_source_words', MIN_SOURCE_WORDS)}`",
        f"- Excluded short-input cases: `{frozen_dataset_summary.get('excluded_short_input_cases', 0)}`",
        f"- Selected cases: `{sum(frozen_dataset_summary['selected_by_bucket'].values())}`",
        "",
        "| Gold bucket | Available | Selected |",
        "| --- | ---: | ---: |",
    ]
    for bucket in config["buckets"]:
        lines.append(
            f"| {bucket} | {frozen_dataset_summary['available_by_bucket'].get(bucket, 0)} | "
            f"{frozen_dataset_summary['selected_by_bucket'].get(bucket, 0)} |"
        )

    if eval_dataset_summaries:
        lines.extend(
            [
                "",
                "### Trained Datasets",
                "",
                "| Dataset | Cases | Excluded short | Files |",
                "| --- | ---: | ---: | ---: |",
            ]
        )
        for dataset_name, info in eval_dataset_summaries.items():
            lines.append(
                f"| {dataset_name} | {info.get('case_count', 0)} | "
                f"{info.get('excluded_short_input_cases', 0)} | {info.get('files_used', 0)} |"
            )

    lines.extend(
        [
            "",
            "## Preflight",
            "",
            "| Model | Status | API ms | Detail |",
            "| --- | --- | ---: | --- |",
        ]
    )
    for model in config["models"]:
        info = preflight.get(model, {})
        detail = info.get("error") or ""
        lines.append(
            f"| {model} | {info.get('status', '')} | {info.get('api_ms', '')} | {detail} |"
        )

    lines.extend(
        [
            "",
            "## Warmups",
            "",
            "| Model | Status | API ms | Total ms |",
            "| --- | --- | ---: | ---: |",
        ]
    )
    for model in config["models"]:
        info = warmups.get(model)
        if not info:
            lines.append(f"| {model} | skipped |  |  |")
            continue
        status = "ok" if not info.get("error") else "error"
        lines.append(
            f"| {model} | {status} | {info.get('api_ms', '')} | {info.get('total_ms', '')} |"
        )

    lines.extend(
        [
            "",
            "## Results",
            "",
            "| Model | Cases | Raw exact | Raw exact % | RL seen | Retries | Median API ms | P90 API ms | Median total ms | P90 total ms |",
            "| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |",
        ]
    )
    for model in config["models"]:
        info = model_summary.get(model)
        if not info:
            lines.append(f"| {model} | 0 | 0 | 0.0 |  |  |  |  |")
            continue
        lines.append(
            f"| {model} | {info['cases']} | {info['exact_matches']} | {info['exact_match_rate']} | "
            f"{info['rate_limit_observed_calls']} | {info['retry_count_total']} | "
            f"{info['api_ms']['median']} | {info['api_ms']['p90']} | "
            f"{info['total_ms']['median']} | {info['total_ms']['p90']} |"
        )

    for dataset_name, per_model_summary in dataset_model_summaries.items():
        lines.extend(
            [
                "",
                f"## Dataset Results: {dataset_name}",
                "",
                "| Model | Cases | Raw exact | Raw exact % | RL seen | Retries | Median API ms | P90 API ms | Median total ms | P90 total ms |",
                "| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |",
            ]
        )
        for model in config["models"]:
            info = per_model_summary.get(model)
            if not info:
                lines.append(f"| {model} | 0 | 0 | 0.0 | 0 | 0 |  |  |  |  |")
                continue
            lines.append(
                f"| {model} | {info['cases']} | {info['exact_matches']} | {info['exact_match_rate']} | "
                f"{info['rate_limit_observed_calls']} | {info['retry_count_total']} | "
                f"{info['api_ms']['median']} | {info['api_ms']['p90']} | "
                f"{info['total_ms']['median']} | {info['total_ms']['p90']} |"
            )

    for model in config["models"]:
        info = model_summary.get(model)
        if not info:
            continue
        lines.extend(
            [
                "",
                f"### {model}",
                "",
                "| Gold bucket | exact | punct_or_spacing_only | minor_wording | moderate_wording | major_difference | error_or_no_text |",
                "| --- | ---: | ---: | ---: | ---: | ---: | ---: |",
            ]
        )
        for bucket in config["buckets"]:
            counts = info["gold_bucket_matrix"].get(bucket, {})
            lines.append(
                f"| {bucket} | {counts.get('exact', 0)} | {counts.get('punct_or_spacing_only', 0)} | "
                f"{counts.get('minor_wording', 0)} | {counts.get('moderate_wording', 0)} | "
                f"{counts.get('major_difference', 0)} | {counts.get('error_or_no_text', 0)} |"
            )

    for dataset_name, per_model_summary in dataset_model_summaries.items():
        for model in config["models"]:
            info = per_model_summary.get(model)
            if not info:
                continue
            lines.extend(
                [
                    "",
                    f"### {dataset_name} :: {model}",
                    "",
                    "| Gold bucket | exact | punct_or_spacing_only | minor_wording | moderate_wording | major_difference | error_or_no_text |",
                    "| --- | ---: | ---: | ---: | ---: | ---: | ---: |",
                ]
            )
            for bucket in config["buckets"]:
                counts = info["gold_bucket_matrix"].get(bucket, {})
                lines.append(
                    f"| {bucket} | {counts.get('exact', 0)} | {counts.get('punct_or_spacing_only', 0)} | "
                    f"{counts.get('minor_wording', 0)} | {counts.get('moderate_wording', 0)} | "
                    f"{counts.get('major_difference', 0)} | {counts.get('error_or_no_text', 0)} |"
                )

    return "\n".join(lines) + "\n"


def write_artifacts(
    output_dir: Path,
    config: dict[str, Any],
    frozen_dataset_summary: dict[str, Any],
    eval_dataset_summaries: dict[str, dict[str, Any]],
    preflight: dict[str, Any],
    warmups: dict[str, Any],
    model_summary: dict[str, Any],
    dataset_model_summaries: dict[str, dict[str, Any]],
) -> None:
    output_dir.mkdir(parents=True, exist_ok=True)

    summary_md = render_summary_markdown(
        config["run_started_at"],
        config,
        frozen_dataset_summary,
        eval_dataset_summaries,
        preflight,
        warmups,
        model_summary,
        dataset_model_summaries,
    )
    (output_dir / "summary.md").write_text(summary_md, encoding="utf-8")

    results_payload = {
        "config": config,
        "frozen_dataset_summary": frozen_dataset_summary,
        "eval_dataset_summaries": eval_dataset_summaries,
        "preflight": preflight,
        "warmups": warmups,
        "model_summary": model_summary,
        "dataset_model_summaries": dataset_model_summaries,
    }
    (output_dir / "results.json").write_text(
        json.dumps(results_payload, indent=2, ensure_ascii=False),
        encoding="utf-8",
    )


def format_elapsed_seconds(started_at: float) -> str:
    return f"{round(time.perf_counter() - started_at, 1)}s"


def run_benchmark(
    cases: list[GoldCase],
    models: list[str],
    transport: MultiProviderTransport,
    replacements: list[tuple[str, str]],
    batch_size: int = 10,
    progress_every: int = 10,
) -> tuple[dict[str, Any], dict[str, Any], list[dict[str, Any]]]:
    benchmark_started = time.perf_counter()
    print(
        f"Benchmark starting: {len(cases)} cases x {len(models)} models "
        f"({len(cases) * len(models)} planned calls)"
    )
    print("Preflight: validating model availability")
    preflight = preflight_models(models, transport)
    warmups: dict[str, Any] = {}
    runnable_models = [model for model, info in preflight.items() if info["status"] == "ok"]
    skipped_models = [model for model, info in preflight.items() if info["status"] != "ok"]
    if runnable_models:
        print(f"Preflight complete: runnable models = {', '.join(runnable_models)}")
    if skipped_models:
        print(f"Preflight skipped: {', '.join(skipped_models)}")
    if not runnable_models:
        return preflight, warmups, []

    warmup_case = cases[0]
    print(f"Warmup: 1 case per runnable model ({len(runnable_models)} total)")
    for model in runnable_models:
        warmups[model] = run_single_case(warmup_case, model, transport, replacements)

    total_calls = len(cases) * len(runnable_models)
    effective_batch_size = max(1, batch_size)
    print(
        f"Main run: {total_calls} calls across {len(runnable_models)} models "
        f"with batch size {effective_batch_size}"
    )
    records: list[dict[str, Any] | None] = [None] * total_calls
    jobs: list[tuple[int, GoldCase, str]] = []
    job_index = 0
    for case in cases:
        for model in runnable_models:
            jobs.append((job_index, case, model))
            job_index += 1

    completed_calls = 0
    with concurrent.futures.ThreadPoolExecutor(max_workers=effective_batch_size) as executor:
        for batch_start in range(0, len(jobs), effective_batch_size):
            batch_jobs = jobs[batch_start : batch_start + effective_batch_size]
            futures = {
                executor.submit(run_single_case, case, model, transport, replacements): job_index
                for job_index, case, model in batch_jobs
            }
            for future in concurrent.futures.as_completed(futures):
                job_index = futures[future]
                records[job_index] = future.result()
                completed_calls += 1
                if progress_every > 0 and (
                    completed_calls % progress_every == 0 or completed_calls == total_calls
                ):
                    latest = records[job_index]
                    print(
                        f"Progress: {completed_calls}/{total_calls} calls complete "
                        f"({format_elapsed_seconds(benchmark_started)} elapsed). "
                        f"Latest: {latest['model']} on {latest['gold_bucket']} -> {latest['mismatch_bucket']}"
                    )
    return preflight, warmups, [record for record in records if record is not None]


def print_console_summary(output_dir: Path, model_summary: dict[str, Any]) -> None:
    print(f"Wrote benchmark artifacts to {output_dir}")
    for model, info in model_summary.items():
        print(
            f"{model}: raw exact={info['exact_matches']}/{info['cases']} "
            f"({info['exact_match_rate']}%), rate-limit seen={info['rate_limit_observed_calls']}, "
            f"retries={info['retry_count_total']}, api median={info['api_ms']['median']} ms, "
            f"total median={info['total_ms']['median']} ms"
        )


def main() -> int:
    args = parse_args()
    run_started_at = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    output_dir = args.output_dir or (BENCHMARKS_DIR / datetime.now().strftime("%Y-%m-%d-%H%M%S"))

    replacements = load_replacements(REPLACEMENTS_PATH)
    dataset_path = args.dataset.resolve() if args.dataset else resolve_default_dataset_path().resolve()
    dataset_refreshed = False
    if args.refresh_dataset:
        if args.dataset:
            dataset_path = args.dataset.resolve()
        else:
            dataset_path = build_versioned_dataset_path()
    if args.refresh_dataset or not dataset_path.exists():
        cases, dataset_summary = build_gold_cases(
            LOGS_DIR,
            weeks=args.weeks,
            seed=args.seed,
            per_bucket=args.per_bucket,
            requested_buckets=args.buckets,
        )
        dataset_summary["source"] = "rebuilt_from_logs"
        save_stable_dataset(dataset_path, cases, dataset_summary)
        dataset_refreshed = True
    else:
        cases, dataset_summary = load_stable_dataset(dataset_path)

    frozen_cases, dataset_summary = select_cases_from_stable_dataset(
        cases,
        seed=args.seed,
        per_bucket=args.per_bucket,
        requested_buckets=args.buckets,
        dataset_summary=dataset_summary,
    )
    for case in frozen_cases:
        case.dataset_name = "frozen_benchmark"

    eval_cases, eval_dataset_summaries = load_all_eval_datasets()
    all_cases = frozen_cases + eval_cases

    if args.build_dataset_only:
        print(f"Wrote stable dataset to {dataset_path}")
        print(f"Cases: {len(frozen_cases)}")
        return 0

    api_keys = load_api_keys(args.models)

    transport = MultiProviderTransport(api_keys, max_rate_limit_retries=args.max_rate_limit_retries)
    try:
        preflight, warmups, records = run_benchmark(
            all_cases,
            args.models,
            transport,
            replacements,
            batch_size=args.batch_size,
            progress_every=args.progress_every,
        )
    finally:
        transport.close()

    config = {
        "run_started_at": run_started_at,
        "models": args.models,
        "dataset_path": str(dataset_path),
        "dataset_refreshed": dataset_refreshed,
        "weeks": args.weeks,
        "per_bucket": args.per_bucket,
        "seed": args.seed,
        "batch_size": args.batch_size,
        "max_rate_limit_retries": args.max_rate_limit_retries,
        "buckets": args.buckets,
        "output_dir": str(output_dir),
        "api_url": API_URL,
        "prompt_instruction_text": PROMPT_INSTRUCTION_TEXT,
        "replacements_path": str(REPLACEMENTS_PATH),
        "evaluation_sets": ["frozen_benchmark", *list(eval_dataset_summaries.keys())],
    }
    model_summary = summarize_records(records)
    dataset_model_summaries = summarize_records_by_dataset(records)
    write_artifacts(
        output_dir,
        config,
        dataset_summary,
        eval_dataset_summaries,
        preflight,
        warmups,
        model_summary,
        dataset_model_summaries,
    )
    print_console_summary(output_dir, model_summary)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
