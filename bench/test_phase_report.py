import copy
import json
from pathlib import Path
import tempfile
import unittest

from phase_report import distribution, report


class ReportTests(unittest.TestCase):
    def fixture(self, root, name, success=True):
        path=Path(root)/name
        path.mkdir()
        manifest=dict(schema_version=2,machine_name='test',case_ids=['short'],developer_logging=False,
                      cases_sha256='same',fixture_sha256='same',replacements_sha256='same',model='same',
                      prompt='same',hotkey_trigger='posted_registered_message',options=dict(mode='local',runs=1,warmup=0))
        row=dict(case_id='short',warmup=False,success=success,quality_match=success,error=None if success else 'paste failed',
                 timings=dict(total_ms=70,request_ms=None),coordinator_timings=dict(paste=dict(settle_ms=50.3)))
        (path/'manifest.json').write_text(json.dumps(manifest))
        (path/'trials.jsonl').write_text(json.dumps(row)+'\n')
        return path, manifest

    def test_nested_samples_and_missing_values(self):
        with tempfile.TemporaryDirectory() as root:
            path,_=self.fixture(root,'one')
            block=report([('one',path)])['blocks'][0]
            self.assertTrue(block['complete'])
            self.assertNotIn('timings.request_ms',block['phases'])
            self.assertEqual(block['phases']['coordinator_timings.paste.settle_ms']['median'],50.3)

    def test_failed_and_missing_attempts_are_visible(self):
        with tempfile.TemporaryDirectory() as root:
            path,_=self.fixture(root,'failure',False)
            block=report([('failed',path)])['blocks'][0]
            self.assertEqual(block['measured_attempts'],1)
            self.assertEqual(len(block['failures']),1)
            self.assertEqual(block['phases'],{})
            (path/'trials.jsonl').write_text('')
            self.assertFalse(report([('aborted',path)])['blocks'][0]['complete'])

    def test_workload_and_benchmark_gate_mismatch_rejected(self):
        for key in ['model','cases_sha256','benchmark_vk','setup_timeout_ms']:
            with self.subTest(key=key), tempfile.TemporaryDirectory() as root:
                before,_=self.fixture(root,'before')
                after,manifest=self.fixture(root,'after')
                manifest[key]='changed'
                (after/'manifest.json').write_text(json.dumps(manifest))
                with self.assertRaises(ValueError):
                    report([('before',before),('after',after)])

    def test_percentiles_keep_zero(self):
        self.assertEqual(distribution([0,10,20])['p95'],19)


if __name__=='__main__':
    unittest.main()
