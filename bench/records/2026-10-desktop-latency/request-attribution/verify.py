"""Verify retained attribution facts, not live-service latency thresholds."""
import json
from pathlib import Path

root=Path(__file__).resolve().parent
rows=json.loads((root/'samples.json').read_text())
if len(rows)!=7:raise ValueError('Unexpected request sample count')
for block,count in [('cold-then-reused',3),('prewarmed',3),('verified-probe',1)]:
    selected=[r for r in rows if r['block']==block]
    if len(selected)!=count or sorted(r['index'] for r in selected)!=list(range(count)):
        raise ValueError('Missing/duplicate block attempts')
    manifest=json.loads((root/(block+'.manifest.json')).read_text())
    if manifest['prewarm'] is not (block != 'cold-then-reused'):
        raise ValueError('Warmup provenance mismatch')
    if manifest['model']!='gpt-4.1' or manifest['fixture']!='This is a tset.' or manifest['runs']!=count:
        raise ValueError('Workload/model mismatch')
for row in rows:
    if not row['success'] or row['status']!=200 or row['attempts']!=1 or row['output']!='This is a test.':
        raise ValueError('Failed or incorrect request')
    if not row['send_api_ms']<5 or not row['request_write_ms']<1:
        raise ValueError('Saved API/write-cost claim does not match the data')
    if not row['request_finished_to_headers_ms']>.9*row['total_ms']:
        raise ValueError('Saved response-wait attribution is not supported')
print('7/7 fixed synthetic requests; retained native intervals support response-wait attribution. No speedup inferred.')
