"""Protect the apples-to-apples gate used for future Rust comparisons."""

import copy
import unittest

from baseline_compare import validate


class ComparisonTests(unittest.TestCase):
    def setUp(self):
        self.manifest = {
            "schema_version": 2, "cases_sha256": "cases", "replacements_sha256": "rules",
            "fixture_sha256": "fixture", "model": "gpt-4.1", "prompt": "fixed",
            "developer_logging": False, "machine_name": "same-machine", "case_ids": ["short"],
            "options": {"mode": "local", "runs": 20, "warmup": 2},
        }

    def test_matching_runs(self):
        validate(self.manifest, copy.deepcopy(self.manifest))

    def test_reject_changed_contract(self):
        for key in ("cases_sha256", "fixture_sha256", "replacements_sha256", "model", "prompt", "developer_logging", "machine_name", "case_ids"):
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


if __name__ == "__main__":
    unittest.main()
