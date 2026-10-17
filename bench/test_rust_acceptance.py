"""Regression guards for changes occurring between asynchronous updater actions."""
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("rust_update_acceptance", Path(__file__).with_name("rust-update-acceptance.py"))
helper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helper)


class ProtectedDataTests(unittest.TestCase):
    def setUp(self):
        self.fixture = {"settings.json":"fixture-settings", "apikey.dat":"fixture-key"}
        self.production = {"production/settings.json":"real-settings", "production/apikey.dat":"real-key", "production_login_startup":"real-command"}
        self.snapshot = {"fixture/settings.json":"fixture-settings", "fixture/apikey.dat":"fixture-key", **self.production}

    def test_unchanged_run_passes(self):
        self.assertTrue(helper.protected_matches(self.snapshot,self.fixture,self.production))

    def test_async_fixture_change_cannot_become_a_new_baseline(self):
        next_invocation = dict(self.snapshot, **{"fixture/apikey.dat":"changed-between-actions"})
        self.assertFalse(helper.protected_matches(next_invocation,self.fixture,self.production))

    def test_async_production_change_is_detected(self):
        for field in self.production:
            with self.subTest(field=field):
                changed = dict(self.snapshot)
                changed[field] = "changed-between-actions"
                self.assertFalse(helper.protected_matches(changed,self.fixture,self.production))

    def test_deleted_fixture_is_detected(self):
        changed = dict(self.snapshot)
        changed.pop("fixture/settings.json")
        self.assertFalse(helper.protected_matches(changed,self.fixture,self.production))


if __name__ == "__main__":
    unittest.main()
