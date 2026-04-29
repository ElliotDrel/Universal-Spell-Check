"""Tests for bench/compare.py — diffs two bench result JSONs into a delta table."""

import json
from pathlib import Path

import pytest

from compare import build_delta_rows, format_delta_table


def make_results(name: str, total_median: float, request_median: float) -> dict:
    return {
        "options": {"Variant": "test", "Model": "gpt-4.1-nano", "Runs": 10, "Warmup": 2},
        "inputs": [
            {
                "name": name,
                "input_chars": 100,
                "trial_count": 10,
                "success_count": 10,
                "total_ms": {"count": 10, "mean": total_median, "median": total_median, "p95": total_median + 50, "min": 0, "max": 0, "stddev": 0},
                "coordinator_total_ms": {"count": 10, "mean": total_median, "median": total_median, "p95": total_median, "min": 0, "max": 0, "stddev": 0},
                "capture_ms": {"count": 10, "mean": 5, "median": 5, "p95": 8, "min": 0, "max": 0, "stddev": 0},
                "request_ms": {"count": 10, "mean": request_median, "median": request_median, "p95": request_median + 30, "min": 0, "max": 0, "stddev": 0},
                "post_process_ms": {"count": 10, "mean": 1, "median": 1, "p95": 2, "min": 0, "max": 0, "stddev": 0},
                "paste_ms": {"count": 10, "mean": 60, "median": 60, "p95": 70, "min": 0, "max": 0, "stddev": 0},
                "tokens": {"input_avg": 50, "output_avg": 30, "cached_avg": 0},
            }
        ],
    }


def test_delta_shows_improvement(tmp_path: Path) -> None:
    before = tmp_path / "before.json"
    after = tmp_path / "after.json"
    before.write_text(json.dumps(make_results("01", 1000, 800)), encoding="utf-8")
    after.write_text(json.dumps(make_results("01", 700, 500)), encoding="utf-8")

    rows = build_delta_rows(before, after)

    total_row = next(r for r in rows if r["input"] == "01" and r["phase"] == "total_ms")
    assert total_row["before_median"] == 1000
    assert total_row["after_median"] == 700
    assert total_row["delta_ms"] == -300
    assert total_row["delta_pct"] == pytest.approx(-30.0)


def test_delta_shows_regression(tmp_path: Path) -> None:
    before = tmp_path / "before.json"
    after = tmp_path / "after.json"
    before.write_text(json.dumps(make_results("01", 800, 500)), encoding="utf-8")
    after.write_text(json.dumps(make_results("01", 1000, 700)), encoding="utf-8")

    rows = build_delta_rows(before, after)

    total_row = next(r for r in rows if r["input"] == "01" and r["phase"] == "total_ms")
    assert total_row["delta_ms"] == 200
    assert total_row["delta_pct"] == pytest.approx(25.0)


def test_format_delta_table_renders_string(tmp_path: Path) -> None:
    before = tmp_path / "before.json"
    after = tmp_path / "after.json"
    before.write_text(json.dumps(make_results("01", 1000, 800)), encoding="utf-8")
    after.write_text(json.dumps(make_results("01", 700, 500)), encoding="utf-8")

    rendered = format_delta_table(build_delta_rows(before, after))

    assert "01" in rendered
    assert "total_ms" in rendered
    assert "-300" in rendered or "-30.0" in rendered
