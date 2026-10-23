import json
from pathlib import Path
import tempfile
import unittest
from check_native_contracts import check_rows


class GateTests(unittest.TestCase):
    def check(self,rows,positive=True):
        with tempfile.TemporaryDirectory() as folder:
            path=Path(folder)
            (path/'trials.jsonl').write_text(''.join(json.dumps(r)+'\n' for r in rows))
            check_rows(path,['one','two'],positive)

    def test_positive_and_negative_controls(self):
        rows=[dict(case_id=c,warmup=False,success=True) for c in ['one','two']]
        self.check(rows)
        with self.assertRaises(ValueError):self.check(rows,False)
        rows[0]['success']=False
        self.check(rows,False)
        with self.assertRaises(ValueError):self.check(rows)

    def test_duplicate_cannot_replace_missing_case(self):
        rows=[dict(case_id='one',warmup=False,success=True)]*2
        with self.assertRaises(ValueError):self.check(rows)


if __name__=='__main__':unittest.main()
