"""Regression for finding recent spell checks across a date boundary."""

import json
import subprocess
import sys
from datetime import date, timedelta
from pathlib import Path


SCRIPT = Path(__file__).parents[1] / ".agents/skills/read-logs/scripts/logs.py"


def test_last_spellchecks_cross_midnight(tmp_path):
    today = date.today()
    yesterday = today - timedelta(days=1)
    detail = {
        "status": "success",
        "model": "test-model",
        "active_exe": "chrome",
        "active_app": "ChatGPT",
        "input_chars": 4,
        "output_chars": 4,
        "paste_text": "test!",
        "paste_text_chars": 5,
        "paste_html": "<b>test!</b>",
        "paste_html_chars": 12,
        "rich_text": {
            "mode": "aligned_html",
            "reason": "",
            "clipboard_html_verification": "exact_match",
        },
    }
    for day, time in ((yesterday, "23:59:00"), (today, "00:01:00")):
        line = (
            f"{day}T{time} channel=dev app_version=0.9.4-dev pid=1 "
            f"spellcheck_detail {json.dumps(detail)}\n"
        )
        (tmp_path / f"spellcheck-{day}.jsonl").write_text(line, encoding="utf-8")

    command = [sys.executable, str(SCRIPT), "--log-dir", str(tmp_path),
               "--event", "spellcheck_detail", "--last", "2"]
    result = subprocess.run(command, capture_output=True, text=True, check=True)
    assert str(yesterday) in result.stdout
    assert str(today) in result.stdout
    assert "window: ChatGPT" in result.stdout
    assert "mode=aligned_html" in result.stdout
    assert "paste_text=5 chars" in result.stdout
    assert "html_verification=exact_match" in result.stdout

    today_only = subprocess.run(command + ["--today"], capture_output=True, text=True, check=True)
    assert str(yesterday) not in today_only.stdout
    assert str(today) in today_only.stdout
