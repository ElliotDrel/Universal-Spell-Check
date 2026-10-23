import json, pathlib, statistics
root = pathlib.Path(__file__).resolve().parent
for mode in ('diagnostics-off', 'diagnostics-on'):
    rows = [json.loads(x) for x in (root / (mode + '.jsonl')).read_text().splitlines()]
    manifest_path = root / (mode + '-manifest.json') if 'mode' in globals() else root / (label + '.manifest.json')
    manifest = json.loads(manifest_path.read_text())
    for case in manifest['case_ids']:
        for warmup, expected in [(False, manifest['options']['runs']), (True, manifest['options']['warmup'])]:
            if sum(r['case_id'] == case and r['warmup'] == warmup for r in rows) != expected:
                raise ValueError('Incomplete or duplicate case coverage: ' + case)
    if any(r['case_id'] not in manifest['case_ids'] for r in rows):
        raise ValueError('Unknown workload case')
    measured = [x for x in rows if not x['warmup']]
    if not (len(rows) == 40 and len(measured) == 30):
        raise ValueError('Evidence validation failed: len(rows) == 40 and len(measured) == 30')
    if not all((x['success'] for x in rows)):
        raise ValueError("Evidence validation failed: all((x['success'] for x in rows))")
    if not sum((x['quality_match'] for x in measured)) == 27:
        raise ValueError("Evidence validation failed: sum((x['quality_match'] for x in measured)) == 27")
    if not {x['case_id'] for x in measured if not x['quality_match']} == {'rich-list'}:
        raise ValueError("Evidence validation failed: {x['case_id'] for x in measured if not x['quality_match']} == {'rich-list'}")
    if not all((x['coordinator_timings']['paste']['settle_ms'] >= 50 for x in rows)):
        raise ValueError("Evidence validation failed: all((x['coordinator_timings']['paste']['settle_ms'] >= 50 for x in rows))")
    for x in rows:
        observed = x['observed_clipboard']['editor_text_at_finish'].replace('\r\n', '\n')
        if not observed == x['output'].replace('\r\n', '\n'):
            raise ValueError("Evidence validation failed: observed == x['output'].replace('\\r\\n', '\\n')")
    times = sorted((x['timings']['total_ms'] for x in measured))
    p = 0.95 * (len(times) - 1)
    p95 = times[int(p)] + (times[int(p) + 1] - times[int(p)]) * (p - int(p))
    print(f'{mode}: 30/30 deliveries, 27/30 handwritten quality; median={statistics.median(times):.3f}ms p95={p95:.3f}ms; under500={sum((t < 500 for t in times))}/30')
if not len(json.loads((root / 'preflights.json').read_text())) == 3:
    raise ValueError("Evidence validation failed: len(json.loads((root / 'preflights.json').read_text())) == 3")
