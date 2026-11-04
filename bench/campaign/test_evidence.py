import copy
import hashlib
import json
import tempfile
import unittest
from pathlib import Path
from evidence import EMPTY_SHAPE, destination_matches, verify_pilot, has_live_evidence, reservation_count


class EvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name)
        self.case = dict(id='short', text='This is a tset.', expected='This is a test.')
        self.cases = {'short': self.case}
        binary = b'unit-test snapshot only; never benchmark acceptance'
        (self.path / 'candidate.exe').write_bytes(binary)
        self.save('manifest.json', dict(pilot=True, binary_sha256=hashlib.sha256(binary).hexdigest()))
        self.integrity = dict(production_unchanged=True, startup_unchanged=True, worker_stopped=True, clipboard_verified=True)
        self.save('integrity.json', self.integrity)
        self.row = dict(case_id='short', repetition=0, warmup=False, live_api=False,
                        observer_pid=101, coordinator_pid=102, collector_pid=103, editor_window_pid=101,
                        detail=dict(status='success', request_mode='offline_replay', input_text=self.case['text']),
                        observed=dict(text=self.case['expected'], paste_received=True, inputType='insertFromPaste', selection_collapsed=True, shape=copy.deepcopy(EMPTY_SHAPE)))

    def save(self, name, value):
        (self.path / name).write_text(json.dumps(value), encoding='utf-8')

    def samples(self, rows):
        (self.path / 'attempts.jsonl').write_text('\n'.join(json.dumps(row) for row in rows), encoding='utf-8')

    def test_pass_flags_cannot_replace_observations(self):
        self.save('pilot.json', dict(passed=True, live_api=False))
        self.row.update(success=True, text_verified=True, format_verified=True)
        self.row['observed']['text'] = 'Wrong insertion'
        self.samples([self.row])
        self.assertFalse(verify_pilot(self.path, self.cases, {}))
        self.row['observed']['text'] = self.case['expected']
        self.samples([self.row])
        self.assertTrue(verify_pilot(self.path, self.cases, {}))

    def test_restoration_process_identity_and_coverage_are_required(self):
        self.samples([self.row])
        self.integrity['clipboard_verified'] = False
        self.save('integrity.json', self.integrity)
        self.assertFalse(verify_pilot(self.path, self.cases, {}))
        self.integrity['clipboard_verified'] = True
        self.save('integrity.json', self.integrity)
        self.row['observer_pid'] = self.row['coordinator_pid']
        self.row['editor_window_pid'] = self.row['observer_pid']
        self.samples([self.row])
        self.assertFalse(verify_pilot(self.path, self.cases, {}))
        self.samples([])
        self.assertFalse(verify_pilot(self.path, self.cases, {}))

    def test_wrong_output_control_is_recomputed(self):
        self.samples([self.row])
        self.assertFalse(verify_pilot(self.path, self.cases, {}, negative=True))
        self.row['observed']['text'] += ' WRONG'
        self.samples([self.row])
        self.assertTrue(verify_pilot(self.path, self.cases, {}, negative=True))
        self.assertFalse(verify_pilot(self.path, self.cases, {}))

    def test_complete_format_content_and_nesting_are_required(self):
        references = json.loads((Path(__file__).parent / 'formats-v1.json').read_text(encoding='utf-8'))
        cases = json.loads((Path(__file__).parent / 'cases-v1.json').read_text(encoding='utf-8'))
        case = next(case for case in cases if case['id'] == 'structured')
        observed = dict(text=case['expected'], paste_received=True, inputType='insertFromPaste', selection_collapsed=True, shape=copy.deepcopy(references['structured']['expected']))
        self.assertTrue(destination_matches(observed, case, references))
        for key, bad in [('bold', ['wrong word']), ('links', [['wrong label', 'https://example.com/guide']]), ('list_structure', [['ol', 1, 2]]), ('code', ['wrong path']), ('lists', 0), ('items', 1)]:
            changed = copy.deepcopy(observed)
            changed['shape'][key] = bad
            self.assertFalse(destination_matches(changed, case, references))

    def test_replay_and_missing_native_requests_cannot_claim_live(self):
        detail = dict(request_mode='live', status_code=200, tokens=dict(input=10, output=5),
                      request_attempts=1, request_native=dict(send_api_ms=0.0))
        self.assertTrue(has_live_evidence(detail, 1))
        self.assertFalse(has_live_evidence(detail, 0))
        self.assertFalse(has_live_evidence(detail, 2))
        self.assertFalse(has_live_evidence(detail, True))
        self.assertFalse(has_live_evidence(dict(detail, request_mode='offline_replay'), 1))
        self.assertFalse(has_live_evidence(dict(detail, tokens=dict(input=None, output=None)), 1))
        path = self.path / 'reservations.jsonl'
        path.write_text('{"reservation":1,"limit":100}\n', encoding='utf-8')
        self.assertEqual(reservation_count(path), 1)
        path.write_text('{"reservation":2,"limit":100}\n', encoding='utf-8')
        with self.assertRaises(ValueError):
            reservation_count(path)


if __name__ == '__main__':
    unittest.main()
