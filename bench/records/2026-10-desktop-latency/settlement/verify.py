import json
from pathlib import Path
import statistics
root=Path(__file__).resolve().parent
def stats(v):
    v=sorted(v); p=.95*(len(v)-1); i=int(p)
    return statistics.median(v),v[i]+(v[min(i+1,len(v)-1)]-v[i])*(p-i)
for label in ['off-a1','on-a1','off-b1','on-b1','off-b2','on-b2','off-a2','on-a2']:
    rows=[json.loads(s) for s in (root/(label+'.jsonl')).read_text().splitlines()]
    measured=[r for r in rows if not r['warmup']]
    assert len(rows)==60 and len(measured)==50
    assert all(r['success'] for r in rows)
    assert all(r['coordinator_timings']['paste']['settle_ms']>=50 for r in rows)
    assert all(r['observed_clipboard']['editor_text_at_finish'].replace('\r\n','\n')==r['output'].replace('\r\n','\n') for r in rows)
    assert sum(r['quality_match'] for r in measured)==45
    assert {r['case_id'] for r in measured if not r['quality_match']}=={'rich-list'}
    print(label,'50/50 delivery, 45/50 quality; completion median/p95',stats([r['timings']['total_ms'] for r in measured]),'settlement median/p95',stats([r['coordinator_timings']['paste']['settle_ms'] for r in measured]))
probe=json.loads((root/'wait-probe.json').read_text())
assert len(probe)==260 and all(r['elapsed_ms']>=50 for r in probe)
print('480 native-editor attempts and 260 native wait probes meet the 50-ms minimum.')
live=json.loads((root/'live-pipeline.json').read_text())
assert len(live)==7
assert all(r['status']=='success' and r['model']=='gpt-4.1' and r['timings']['paste']['settle_ms']>=50 for r in live)
browser=json.loads((root/'browser-results.json').read_text())
assert len(browser)==8
baseline={r['case']:r for r in browser if r['variant']=='installed-baseline'}
candidate={r['case']:r for r in browser if r['variant']=='candidate'}
for case in ['plain','rich','unchanged']:
    assert candidate[case]['status']==baseline[case]['status']
assert candidate['rich-repeat']['status']==baseline['rich']['status']
assert candidate['rich-unchanged']['status']==baseline['rich']['status'].replace('Paste events: 1','Paste events: 2')
assert candidate['unchanged']['formatting_match'] is False and baseline['unchanged']['formatting_match'] is False
assert json.loads((root/'notepad-result.json').read_text())['verified'] is True
print('Seven live pipeline waits meet the minimum; browser observations match baseline, including the retained unsupported-rich limitation.')
