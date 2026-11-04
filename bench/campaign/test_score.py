import unittest
from score import score


class ScoreTests(unittest.TestCase):
    def test_missing_coverage_cannot_pass(self):
        result = score([{'case_id': 'short', 'warmup': False}], [])
        self.assertFalse(result['goal_verified'])
        self.assertIsNone(result['mean_of_case_medians_ms'])

    def test_recorded_api_or_same_process_cannot_pass(self):
        schedule = [{'case_id': 'short', 'repetition': 0, 'warmup': False}]
        row = dict(case_id='short', repetition=0, warmup=False, success=True, live_api=False,
                   text_verified=True, format_verified=True, observer_pid=1,
                   coordinator_pid=2, verified_insertion_ms=1)
        self.assertFalse(score(schedule, [row])['complete'])
        row['live_api'] = True
        row['coordinator_pid'] = 1
        self.assertFalse(score(schedule, [row])['complete'])

    def test_case_medians_receive_equal_weight(self):
        rows = [dict(case_id=c, warmup=False, success=True, live_api=True,
                     repetition=i,
                     text_verified=True, format_verified=True, observer_pid=1,
                     coordinator_pid=2, verified_insertion_ms=ms)
                for i, (c, ms) in enumerate([('a', 100), ('a', 100), ('b', 2100)])]
        schedule = [{'case_id': r['case_id'], 'repetition':r['repetition'], 'warmup': False} for r in rows]
        result = score(schedule, rows)
        self.assertEqual(result['mean_of_case_medians_ms'], 1100)
        self.assertFalse(result['goal_verified'])

    def test_duplicate_repetitions_and_boolean_pids_fail(self):
        schedule = [dict(case_id='a',repetition=i,warmup=False) for i in range(2)]
        rows = [dict(case_id='a',repetition=0,warmup=False,live_api=True,success=True,
                     text_verified=True,format_verified=True,observer_pid=1,
                     coordinator_pid=2,verified_insertion_ms=1)]*2
        self.assertFalse(score(schedule,rows)['complete'])
        rows=[dict(rows[0]),dict(rows[1],repetition=1,observer_pid=True)]
        self.assertFalse(score(schedule,rows)['complete'])

    def test_seeded_order_and_explicit_warmup_are_required(self):
        schedule=[dict(case_id='a',repetition=i,warmup=False) for i in range(2)]
        rows=[dict(case_id='a',repetition=i,warmup=False,live_api=True,success=True,
                   text_verified=True,format_verified=True,observer_pid=1,
                   coordinator_pid=2,verified_insertion_ms=1) for i in range(2)]
        self.assertFalse(score(schedule,list(reversed(rows)))['complete'])
        rows[0].pop('warmup')
        self.assertFalse(score(schedule,rows)['complete'])


if __name__ == '__main__':
    unittest.main()
