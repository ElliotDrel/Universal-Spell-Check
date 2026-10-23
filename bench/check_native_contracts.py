"""Deterministic production-pipeline gate: frozen outputs plus a negative control.

No API, clipboard, GUI or production credential access. Wall-clock timings are
retained for inspection but never used as flaky shared-runner pass thresholds.
"""
import argparse
import json
import os
from pathlib import Path
import subprocess


def check_rows(folder, case_ids, must_succeed):
    rows=[json.loads(line) for line in (folder/'trials.jsonl').read_text().splitlines()]
    if len(rows)!=len(case_ids) or sorted(r['case_id'] for r in rows)!=sorted(case_ids):
        raise ValueError('Missing, extra or duplicate workload cases')
    if any(r['warmup'] for r in rows):
        raise ValueError('Unexpected warmup')
    failures=[r for r in rows if not r['success']]
    if must_succeed and failures:
        raise ValueError('Frozen pipeline mismatch: '+str(failures))
    if not must_succeed and not failures:
        raise ValueError('Negative control passed: correctness gate is ineffective')


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--exe',required=True,type=Path)
    parser.add_argument('--output',required=True,type=Path)
    args=parser.parse_args()
    root=Path(__file__).resolve().parents[1]
    output=args.output.resolve()
    output.mkdir(parents=True,exist_ok=False)
    workload=root/'bench/records/2026-10-rust-port'
    cases=json.loads((workload/'cases.json').read_text())
    fixtures=json.loads((workload/'fixtures.json').read_text())
    bad=output/'mutated-fixtures.json'
    fixtures[0]['output']+=' NEGATIVE CONTROL MUST FAIL'
    bad.write_text(json.dumps(fixtures))
    env=os.environ.copy()
    env['LOCALAPPDATA']=str(output/'isolated-data')
    for name,fixture,positive in [('positive',workload/'fixtures.json',True),('negative',bad,False)]:
        folder=output/name
        command=[str(args.exe.resolve()),'--bench-compute','--cases',str(workload/'cases.json'),
                 '--fixtures',str(fixture),'--replacements',str(root/'replacements.json'),
                 '--runs','1','--warmup','0','--output',str(folder)]
        result=subprocess.run(command,cwd=root,env=env,capture_output=True,timeout=60)
        (output/(name+'.stdout')).write_bytes(result.stdout)
        (output/(name+'.stderr')).write_bytes(result.stderr)
        if (result.returncode==0)!=positive:
            raise ValueError(f'{name}: unexpected exit {result.returncode}')
        check_rows(folder,[c['id'] for c in cases],positive)
    print('Frozen production pipeline passed all cases; mutated-output negative control rejected.')


if __name__=='__main__':
    main()
