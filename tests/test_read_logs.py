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


def _replay_entry():
    return {
        "ts": "2026-10-07T12:00:00", "channel": "prod", "version": "0.9.4", "pid": "1",
        "detail": {"status": "success", "input_text": "teh 😀", "input_chars": 6,
                   "output_text": "the 😀", "output_chars": 6,
                   "clipboard_html": "", "clipboard_html_chars": 0,
                   "paste_text": "the 😀", "paste_html": ""},
    }


def _reader():
    import importlib.util
    spec = importlib.util.spec_from_file_location("formatting_log_reader", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def test_export_replay_preserves_mapper_inputs_and_utf16_lengths(tmp_path):
    case = _reader().make_replay_case(_replay_entry(), tmp_path)
    assert case["detail"]["input_text"] == "teh 😀"
    assert case["detail"]["input_chars"] == 6
    assert case["detail"]["output_text"] == "the 😀"
    assert "raw_ai_output" not in case["detail"]
    assert case["source"]["evidence_origin"] == "inline_log"


def test_export_rejects_incomplete_mapper_inputs(tmp_path):
    import pytest
    for field in ("input_text", "output_text", "clipboard_html"):
        entry = _replay_entry()
        entry["detail"][field + "_truncated"] = True
        with pytest.raises(ValueError, match="truncated"):
            _reader().make_replay_case(entry, tmp_path)
        entry["detail"].pop(field)
        with pytest.raises(ValueError):
            _reader().make_replay_case(entry, tmp_path)


def _developer_entry(tmp_path):
    import hashlib
    entry = _replay_entry()
    folder = tmp_path / "developer-evidence" / "test-run"
    folder.mkdir(parents=True)
    payloads = {}
    for field in ("input_text", "output_text", "clipboard_html", "paste_text", "paste_html"):
        value = entry["detail"][field]
        raw = value.encode("utf-8")
        (folder / (field + ".txt")).write_bytes(raw)
        payloads[field] = {"path": field + ".txt", "sha256": hashlib.sha256(raw).hexdigest(),
                           "bytes": len(raw), "chars": len(value.encode("utf-16-le")) // 2,
                           "complete": True, "status": "ok"}
    manifest = {"schema_version": 1, "run_id": "test-run", "payloads": payloads}
    manifest_path = folder / "manifest.json"
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
    entry["detail"].update({"run_id": "test-run", "developer_evidence": {
        "schema_version": 1, "manifest_path": "developer-evidence/test-run/manifest.json",
        "status": "pending"}})
    # A clipped inline field must be hydrated from complete sidecar evidence.
    entry["detail"]["clipboard_html_truncated"] = True
    return entry, manifest_path, manifest


def test_export_hydrates_complete_developer_evidence(tmp_path):
    entry, path, manifest = _developer_entry(tmp_path)
    case = _reader().make_replay_case(entry, tmp_path)
    assert case["detail"]["output_text"] == "the 😀"
    assert case["detail"]["clipboard_html_truncated"] is False
    assert case["source"]["evidence_origin"] == "developer_manifest"
    (path.parent / "input_text.txt").write_text("corrupted", encoding="utf-8")
    import pytest
    with pytest.raises(ValueError, match="integrity"):
        _reader().make_replay_case(entry, tmp_path)


def test_export_rejects_missing_mismatched_and_escaped_evidence(tmp_path):
    import pytest
    entry, path, manifest = _developer_entry(tmp_path)
    manifest["run_id"] = "another-run"
    path.write_text(json.dumps(manifest), encoding="utf-8")
    with pytest.raises(ValueError, match="identity"):
        _reader().make_replay_case(entry, tmp_path)
    manifest["run_id"] = "test-run"
    manifest["payloads"]["input_text"]["path"] = "../../outside.txt"
    path.write_text(json.dumps(manifest), encoding="utf-8")
    with pytest.raises(ValueError, match="escapes"):
        _reader().make_replay_case(entry, tmp_path)
    path.unlink()
    with pytest.raises(ValueError, match="retry"):
        _reader().make_replay_case(entry, tmp_path)


def test_export_cli_requires_one_run_and_never_overwrites(tmp_path):
    entry = _replay_entry()
    day = date.today()
    log = tmp_path / f"spellcheck-{day}.jsonl"
    line = f"{day}T12:00:00 channel=prod app_version=0.9.4 pid=1 spellcheck_detail {json.dumps(entry['detail'])}\n"
    log.write_text(line + line, encoding="utf-8")
    output = tmp_path / "case.json"
    command = [sys.executable, str(SCRIPT), "--log-dir", str(tmp_path),
               "--save-replay-case", str(output)]
    result = subprocess.run(command, capture_output=True, text=True)
    assert result.returncode == 2 and "exactly one" in result.stderr
    subprocess.run(command + ["--last", "1"], capture_output=True, text=True, check=True)
    saved = output.read_bytes()
    result = subprocess.run(command + ["--last", "1"], capture_output=True, text=True)
    assert result.returncode == 2 and output.read_bytes() == saved
    assert log.read_text(encoding="utf-8") == line + line


def test_export_complete_inline_survives_developer_storage_failure(tmp_path):
    import pytest
    entry = _replay_entry()
    entry["detail"]["developer_evidence"] = {
        "schema_version": 1, "status": "storage_limit", "manifest_path": "missing.json"}
    case = _reader().make_replay_case(entry, tmp_path)
    assert case["source"]["evidence_origin"] == "inline_log"
    assert "save failed" in case["source"]["evidence_warning"]
    entry["detail"]["clipboard_html_truncated"] = True
    with pytest.raises(ValueError, match="truncated"):
        _reader().make_replay_case(entry, tmp_path)


def test_export_malformed_manifest_fails_explicitly(tmp_path):
    import pytest
    entry, path, manifest = _developer_entry(tmp_path)
    manifest["payloads"] = []
    path.write_text(json.dumps(manifest), encoding="utf-8")
    with pytest.raises(ValueError, match="object"):
        _reader().make_replay_case(entry, tmp_path)


def test_busy_storage_fallback_rejects_offered_but_missing_html(tmp_path):
    import pytest
    entry = _replay_entry()
    entry["detail"]["developer_evidence"] = {
        "schema_version": 1, "status": "storage_busy", "manifest_path": "missing.json"}
    entry["detail"]["clipboard_formats"] = "UnicodeText, HTML Format, Rich Text Format"
    with pytest.raises(ValueError, match="offered"):
        _reader().make_replay_case(entry, tmp_path)
    entry["detail"]["clipboard_formats"] = "UnicodeText"
    case = _reader().make_replay_case(entry, tmp_path)
    assert case["detail"]["clipboard_html"] == ""
    assert "save failed" in case["source"]["evidence_warning"]
    entry["detail"].pop("clipboard_formats")
    case = _reader().make_replay_case(entry, tmp_path)
    assert "availability is unknown" in case["source"]["evidence_warning"]


def test_capture_admission_busy_uses_complete_inline_inputs(tmp_path):
    entry = _replay_entry()
    entry["detail"]["developer_evidence"] = {
        "schema_version": 1, "status": "busy", "manifest_path": ""}
    case = _reader().make_replay_case(entry, tmp_path)
    assert case["source"]["evidence_origin"] == "inline_log"
    assert "save failed" in case["source"]["evidence_warning"]
    assert case["detail"]["output_text"] == "the 😀"
