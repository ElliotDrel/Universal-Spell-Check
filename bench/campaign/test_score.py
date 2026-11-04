import unittest
from score import score


class ScoreTests(unittest.TestCase):
    def test_missing_coverage_cannot_pass(self):
        result = score([{'case_id': 'short', 'warmup': False}], [])
        self.assertFalse(result['goal_verified'])
        self.assertIsNone(result['mean_of_case_medians_ms'])

    def test_recorded_api_or_same_process_cannot_pass(self):
        schedule = [{'case_id': 'short', 'warmup': False}]
        row = dict(case_id='short', warmup=False, success=True, live_api=False,
                   text_verified=True, format_verified=True, observer_pid=1,
                   coordinator_pid=1, verified_insertion_ms=1)
        self.assertFalse(score(schedule, [row])['complete'])
        row['live_api'] = True
        self.assertFalse(score(schedule, [row])['complete'])

    def test_case_medians_receive_equal_weight(self):
        rows = [dict(case_id=c, warmup=False, success=True, live_api=True,
                     text_verified=True, format_verified=True, observer_pid=1,
                     coordinator_pid=2, verified_insertion_ms=ms)
                for c, ms in [('a', 100), ('a', 100), ('b', 2100)]]
        schedule = [{'case_id': r['case_id'], 'warmup': False} for r in rows]
        result = score(schedule, rows)
        self.assertEqual(result['mean_of_case_medians_ms'], 1100)
        self.assertFalse(result['goal_verified'])


if __name__ == '__main__':
    unittest.main()
