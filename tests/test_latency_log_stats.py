"""Null timing stages must not crash stats or become fabricated zero samples."""
import importlib.util
from pathlib import Path
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / ".agents/skills/read-logs/scripts/logs.py"
spec = importlib.util.spec_from_file_location("latency_reader", SCRIPT)
reader = importlib.util.module_from_spec(spec)
spec.loader.exec_module(reader)


class LatencyStatsTests(unittest.TestCase):
    def test_failed_and_legacy_unmeasured_runs_keep_reliability_denominator(self):
        rows = [
            {"status": "success", "timings": {"total_ms": 100, "request_ms": 20}},
            {"status": "run_failed", "timings": {"total_ms": 50, "request_ms": None}},
            {"status": "run_failed"},
        ]
        stats = reader.compute_stats(rows)
        self.assertIn("1/3 (33%)", stats)
        self.assertIn("75ms total / 20ms request", stats)
        self.assertIn("total=2/3, request=1/3", stats)
        self.assertIn("Median latency:  75ms", stats)

    def test_actual_zero_is_retained_and_all_null_is_unavailable(self):
        stats = reader.compute_stats([{"timings": {"total_ms": 0, "request_ms": None}}])
        self.assertIn("0ms total / unavailable request", stats)
        self.assertIn("total=1/1, request=0/1", stats)
        self.assertIn("Avg latency:     unavailable total / unavailable request", reader.compute_stats([{"timings": None}]))

    def test_nonfinite_and_malformed_values_are_not_measurements(self):
        stats = reader.compute_stats([
            {"timings": {"total_ms": float("nan"), "request_ms": "unknown"}},
            {"timings": {"total_ms": True, "request_ms": float("inf")}},
        ])
        self.assertIn("total=0/2, request=0/2", stats)
        self.assertEqual(reader._display_ms(None), "unavailable")
        self.assertEqual(reader._display_ms(10**1000), "unavailable")
        self.assertIn("total=0/1", reader.compute_stats([{"timings":{"total_ms":10**1000}}]))


if __name__ == "__main__":
    unittest.main()
