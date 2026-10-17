"""Protect the apples-to-apples gate used for future Rust comparisons."""

import copy
import unittest
import json
import subprocess
import sys
import tempfile
from pathlib import Path

from baseline_compare import validate


class ComparisonTests(unittest.TestCase):
    def setUp(self):
        self.manifest = {
            "schema_version": 2, "cases_sha256": "cases", "replacements_sha256": "rules",
            "fixture_sha256": "fixture", "model": "gpt-4.1", "prompt": "fixed",
            "developer_logging": False, "machine_name": "same-machine", "case_ids": ["short"],
            "hotkey_trigger": "posted_registered_message",
            "options": {"mode": "local", "runs": 20, "warmup": 2},
        }

    def test_matching_runs(self):
        validate(self.manifest, copy.deepcopy(self.manifest))

    def test_reject_changed_contract(self):
        for key in ("cases_sha256", "fixture_sha256", "replacements_sha256", "model", "prompt", "developer_logging", "machine_name", "case_ids", "hotkey_trigger"):
            with self.subTest(key=key):
                other = copy.deepcopy(self.manifest)
                other[key] = "different"
                with self.assertRaises(ValueError):
                    validate(self.manifest, other)

    def test_reject_changed_mode_or_repeats(self):
        for key in ("mode", "runs", "warmup"):
            with self.subTest(key=key):
                other = copy.deepcopy(self.manifest)
                other["options"][key] = "different"
                with self.assertRaises(ValueError):
                    validate(self.manifest, other)

    def test_legacy_schema_rejected(self):
        other = copy.deepcopy(self.manifest)
        other["schema_version"] = 1
        with self.assertRaises(ValueError):
            validate(other, other)

    def test_existing_failures_remain_comparable_and_visible(self):
        manifest = copy.deepcopy(self.manifest)
        manifest["options"]["runs"] = 2
        group = {"attempted": 2, "succeeded": 1, "failed": 1, "quality_matches": 1,
                 "timings": {"total_ms": {"median": 100, "p95": 100}}}
        summary = {"aggregate": group, "warmup": {"failed": 0}, "cases": {"short": group}}
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for name in ("before", "after"):
                folder = root / name
                folder.mkdir()
                (folder / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
                (folder / "summary.json").write_text(json.dumps(summary), encoding="utf-8")
            result = subprocess.run([sys.executable, str(Path(__file__).with_name("baseline_compare.py")),
                                     str(root / "before"), str(root / "after")], capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("1/2 successful", result.stdout)
        self.assertIn("successful attempts only", result.stdout)
        self.assertIn("does not award a passing verdict", result.stdout)


if __name__ == "__main__":
    unittest.main()
