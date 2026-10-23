import math

def finite_timings(value, key=''):
    if isinstance(value, dict):
        for name, item in value.items():
            finite_timings(item, name)
    elif isinstance(value, list):
        for item in value:
            finite_timings(item, key)
    elif key.endswith('_ms') and value is not None:
        try:
            valid = isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value) and value >= 0
        except OverflowError:
            valid = False
        if not valid:
            raise ValueError('Non-finite or invalid latency: ' + key)

import json
from pathlib import Path
import statistics
root = Path(__file__).resolve().parent

def stats(v):
    v = sorted(v)
    p = 0.95 * (len(v) - 1)
    i = int(p)
    return (statistics.median(v), v[i] + (v[min(i + 1, len(v) - 1)] - v[i]) * (p - i))
for label in ['off-a1', 'on-a1', 'off-b1', 'on-b1', 'off-b2', 'on-b2', 'off-a2', 'on-a2']:
    rows = [json.loads(s) for s in (root / (label + '.jsonl')).read_text().splitlines()]
    finite_timings(rows)
    manifest_path = root / (label + '.manifest.json')
    manifest = json.loads(manifest_path.read_text())
    for case in manifest['case_ids']:
        for warmup, expected in [(False, manifest['options']['runs']), (True, manifest['options']['warmup'])]:
            if sum(r['case_id'] == case and r['warmup'] == warmup for r in rows) != expected:
                raise ValueError('Incomplete or duplicate case coverage: ' + case)
    if any(r['case_id'] not in manifest['case_ids'] for r in rows):
        raise ValueError('Unknown workload case')
    measured = [r for r in rows if not r['warmup']]
    if not (len(rows) == 60 and len(measured) == 50):
        raise ValueError('Evidence validation failed: len(rows) == 60 and len(measured) == 50')
    if not all((r['success'] for r in rows)):
        raise ValueError("Evidence validation failed: all((r['success'] for r in rows))")
    if not all((r['coordinator_timings']['paste']['settle_ms'] >= 50 for r in rows)):
        raise ValueError("Evidence validation failed: all((r['coordinator_timings']['paste']['settle_ms'] >= 50 for r in rows))")
    if not all((r['observed_clipboard']['editor_text_at_finish'].replace('\r\n', '\n') == r['output'].replace('\r\n', '\n') for r in rows)):
        raise ValueError("Evidence validation failed: all((r['observed_clipboard']['editor_text_at_finish'].replace('\\r\\n', '\\n') == r['output'].replace('\\r\\n', '\\n') for r in rows))")
    if not sum((r['quality_match'] for r in measured)) == 45:
        raise ValueError("Evidence validation failed: sum((r['quality_match'] for r in measured)) == 45")
    if not {r['case_id'] for r in measured if not r['quality_match']} == {'rich-list'}:
        raise ValueError("Evidence validation failed: {r['case_id'] for r in measured if not r['quality_match']} == {'rich-list'}")
    print(label, '50/50 delivery, 45/50 quality; completion median/p95', stats([r['timings']['total_ms'] for r in measured]), 'settlement median/p95', stats([r['coordinator_timings']['paste']['settle_ms'] for r in measured]))
probe = json.loads((root / 'wait-probe.json').read_text())
finite_timings(probe)
if not (len(probe) == 260 and all((r['elapsed_ms'] >= 50 for r in probe))):
    raise ValueError("Evidence validation failed: len(probe) == 260 and all((r['elapsed_ms'] >= 50 for r in probe))")
print('480 native-editor attempts and 260 native wait probes meet the 50-ms minimum.')
live = json.loads((root / 'live-pipeline.json').read_text())
finite_timings(live)
if not len(live) == 7:
    raise ValueError('Evidence validation failed: len(live) == 7')
if not all((r['status'] == 'success' and r['model'] == 'gpt-4.1' and (r['timings']['paste']['settle_ms'] >= 50) for r in live)):
    raise ValueError("Evidence validation failed: all((r['status'] == 'success' and r['model'] == 'gpt-4.1' and (r['timings']['paste']['settle_ms'] >= 50) for r in live))")
browser = json.loads((root / 'browser-results.json').read_text())
if not len(browser) == 8:
    raise ValueError('Evidence validation failed: len(browser) == 8')
baseline = {r['case']: r for r in browser if r['variant'] == 'installed-baseline'}
candidate = {r['case']: r for r in browser if r['variant'] == 'candidate'}
for case in ['plain', 'rich', 'unchanged']:
    if not candidate[case]['status'] == baseline[case]['status']:
        raise ValueError("Evidence validation failed: candidate[case]['status'] == baseline[case]['status']")
if not candidate['rich-repeat']['status'] == baseline['rich']['status']:
    raise ValueError("Evidence validation failed: candidate['rich-repeat']['status'] == baseline['rich']['status']")
if not candidate['rich-unchanged']['status'] == baseline['rich']['status'].replace('Paste events: 1', 'Paste events: 2'):
    raise ValueError("Evidence validation failed: candidate['rich-unchanged']['status'] == baseline['rich']['status'].replace('Paste events: 1', 'Paste events: 2')")
if not (candidate['unchanged']['formatting_match'] is False and baseline['unchanged']['formatting_match'] is False):
    raise ValueError("Evidence validation failed: candidate['unchanged']['formatting_match'] is False and baseline['unchanged']['formatting_match'] is False")
if not json.loads((root / 'notepad-result.json').read_text())['verified'] is True:
    raise ValueError("Evidence validation failed: json.loads((root / 'notepad-result.json').read_text())['verified'] is True")
print('Seven live pipeline waits meet the minimum; browser observations match baseline, including the retained unsupported-rich limitation.')

import hashlib
for label in ['off-a1','on-a1','off-b1','on-b1','off-b2','on-b2','off-a2','on-a2']:
    manifest=json.loads((root/(label+'.manifest.json')).read_text())
    for key,name in [('cases_sha256','cases.json'),('fixture_sha256','fixtures.json'),('replacements_sha256','replacements.json')]:
        if manifest[key]!=hashlib.sha256((root/'workload'/name).read_bytes()).hexdigest():
            raise ValueError('Workload hash mismatch: '+key)
for mode in ['off','on']:
    medians={}
    for variant in ['a1','b1','b2','a2']:
        rows=[json.loads(line) for line in (root/(mode+'-'+variant+'.jsonl')).read_text().splitlines()]
        medians[variant]=statistics.median(r['timings']['total_ms'] for r in rows if not r['warmup'])
    if not (medians['b1']<medians['a1'] and medians['b2']<medians['a2']):
        raise ValueError('Recorded A/B speed claim no longer matches data')
