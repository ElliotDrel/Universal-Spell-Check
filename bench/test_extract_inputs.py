"""Tests for bench/extract_inputs.py — picks 10 length-stratified inputs from JSONL logs."""

import json
import textwrap
from pathlib import Path

import pytest

from extract_inputs import extract_inputs


def make_log_line(input_text: str, status: str = "success") -> str:
    """Build one JSONL log line in the shape DiagnosticsLogger.LogData emits."""
    payload = {
        "status": status,
        "input_text": input_text,
        "input_chars": len(input_text),
    }
    # Format: "<iso-ts> channel=prod app_version=1.0.0 pid=1234 spellcheck_detail <json>"
    return f"2026-04-29T12:00:00.0000000-05:00 channel=prod app_version=1.0.0 pid=1234 spellcheck_detail {json.dumps(payload)}\n"


def test_extracts_only_successful_runs(tmp_path: Path) -> None:
    log = tmp_path / "spellcheck-2026-04-29.jsonl"
    log.write_text(
        make_log_line("hello world", status="success")
        + make_log_line("failed run text", status="capture_failed")
        + make_log_line("another success", status="success"),
        encoding="utf-8",
    )

    inputs = extract_inputs([log], target_count=10)

    assert "hello world" in inputs
    assert "another success" in inputs
    assert "failed run text" not in inputs


def test_dedupes_identical_inputs(tmp_path: Path) -> None:
    log = tmp_path / "spellcheck.jsonl"
    log.write_text(
        make_log_line("duplicate text") + make_log_line("duplicate text") + make_log_line("unique"),
        encoding="utf-8",
    )

    inputs = extract_inputs([log], target_count=10)

    assert inputs.count("duplicate text") == 1
    assert "unique" in inputs


def test_returns_at_most_target_count(tmp_path: Path) -> None:
    log = tmp_path / "spellcheck.jsonl"
    log.write_text("".join(make_log_line(f"input number {i}") for i in range(50)), encoding="utf-8")

    inputs = extract_inputs([log], target_count=10)

    assert len(inputs) == 10


def test_stratifies_by_length(tmp_path: Path) -> None:
    """When 10+ inputs of varying lengths exist, the output spans short/medium/long."""
    log = tmp_path / "spellcheck.jsonl"
    short = ["a" * 20] * 3 + ["b" * 25, "c" * 30]
    medium = ["m" * 200, "n" * 250, "o" * 300]
    long_ = ["l" * 1500, "p" * 2000, "q" * 2500, "r" * 3000]
    lines = [make_log_line(t) for t in short + medium + long_]
    log.write_text("".join(lines), encoding="utf-8")

    inputs = extract_inputs([log], target_count=10)

    lengths = sorted(len(i) for i in inputs)
    assert lengths[0] < 100, "expected at least one short input"
    assert any(100 <= L <= 1000 for L in lengths), "expected at least one medium input"
    assert lengths[-1] > 1000, "expected at least one long input"


def test_handles_malformed_lines(tmp_path: Path) -> None:
    log = tmp_path / "spellcheck.jsonl"
    log.write_text(
        "garbage non-jsonl line\n"
        + make_log_line("good input")
        + "another bad line { not json\n",
        encoding="utf-8",
    )

    inputs = extract_inputs([log], target_count=10)

    assert "good input" in inputs


def test_skips_empty_input_text(tmp_path: Path) -> None:
    log = tmp_path / "spellcheck.jsonl"
    log.write_text(
        make_log_line("") + make_log_line("real text"),
        encoding="utf-8",
    )

    inputs = extract_inputs([log], target_count=10)

    assert "" not in inputs
    assert "real text" in inputs
