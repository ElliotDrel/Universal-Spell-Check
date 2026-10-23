"""Evidence checks must reject corrupt workloads even under optimized Python."""
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT=Path(__file__).resolve().parents[1]/'bench/records/2026-10-desktop-latency'


class EvidenceGateTests(unittest.TestCase):
    def test_duplicate_case_is_rejected_under_optimized_python(self):
        for source, filename in [(ROOT,'diagnostics-off.jsonl'),(ROOT/'settlement','off-a1.jsonl')]:
            with self.subTest(source=source), tempfile.TemporaryDirectory() as directory:
                target=Path(directory)/'record'
                shutil.copytree(source,target)
                path=target/filename
                rows=[json.loads(line) for line in path.read_text(encoding='utf-8').splitlines()]
                rows[-1]['case_id']=rows[0]['case_id']
                path.write_text(''.join(json.dumps(row)+'\n' for row in rows),encoding='utf-8')
                result=subprocess.run([sys.executable,'-O',str(target/'verify.py')],capture_output=True)
                self.assertNotEqual(result.returncode,0,'Corrupted case coverage was accepted')


if __name__=='__main__':unittest.main()
