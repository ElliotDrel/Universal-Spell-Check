"""Report ordered native benchmark blocks without pooling modes or hiding failures."""
import argparse
import json
import math
from pathlib import Path
import statistics

from baseline_compare import validate


def flatten(value, prefix=''):
    if isinstance(value, dict):
        for key, child in value.items():
            yield from flatten(child, prefix + key + '.')
    elif isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value):
        yield prefix.rstrip('.'), value


def distribution(values):
    ordered = sorted(values)
    position = .95 * (len(ordered) - 1)
    index = int(position)
    return dict(samples=len(ordered), median=statistics.median(ordered),
                p95=ordered[index] + (ordered[min(index + 1, len(ordered)-1)] - ordered[index]) * (position-index),
                mean=statistics.mean(ordered), minimum=ordered[0], maximum=ordered[-1])


def block(name, directory):
    directory = Path(directory)
    manifest = json.loads((directory/'manifest.json').read_text(encoding='utf-8-sig'))
    if not manifest.get('machine_name'):
        raise ValueError('A machine identity is required for a matched comparison')
    path = directory/'trials.jsonl'
    rows = [json.loads(line) for line in path.read_text(encoding='utf-8-sig').splitlines()] if path.exists() else []
    measured = [r for r in rows if not r['warmup']]
    warmups = [r for r in rows if r['warmup']]
    cases = manifest['case_ids']
    complete = all(sum(r['case_id']==case for r in measured)==manifest['options']['runs']
                   and sum(r['case_id']==case for r in warmups)==manifest['options']['warmup'] for case in cases)
    complete = complete and all(r['case_id'] in cases for r in rows)
    samples = {}
    for row in measured:
        if not row['success']:
            continue
        for source in ['timings', 'coordinator_timings']:
            for phase, value in flatten(row.get(source, {})):
                samples.setdefault(source+'.'+phase, []).append(value)
    return manifest, dict(name=name, directory=str(directory), binary_sha256=manifest.get('product_binary_sha256'),
        developer_logging=manifest['developer_logging'], complete=complete,
        measured_attempts=len(measured), measured_successes=sum(r['success'] for r in measured),
        measured_quality_matches=sum(r.get('quality_match',False) for r in measured),
        warmup_attempts=len(warmups), warmup_failures=sum(not r['success'] for r in warmups),
        failures=[dict(case_id=r['case_id'],warmup=r['warmup'],error=r.get('error')) for r in rows if not r['success']],
        phases={key:distribution(values) for key,values in sorted(samples.items())})


def report(specs):
    references = {}
    blocks = []
    for name, directory in specs:
        manifest, result = block(name, directory)
        mode = manifest['developer_logging']
        if mode in references:
            validate(references[mode], manifest)
            for key in ['benchmark_vk','setup_timeout_ms']:
                if references[mode].get(key)!=manifest.get(key):
                    raise ValueError('Incomparable '+key)
        else:
            references[mode] = manifest
        blocks.append(result)
    return dict(blocks=blocks, notes=['Block order is argument order; logging modes remain separate.',
        'Distributions use successful measured attempts only; failures and incomplete coverage are explicit.',
        'Nested intervals overlap parent intervals: do not add them together.',
        'No significance test, automatic winning verdict or original-editor acceptance is inferred.'])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--block',action='append',required=True,metavar='NAME=DIRECTORY')
    parser.add_argument('--output',type=Path,help='Fresh JSON destination; existing files are never replaced')
    args = parser.parse_args()
    specs=[item.split('=',1) for item in args.block]
    if any(len(item)!=2 for item in specs):
        parser.error('--block requires NAME=DIRECTORY')
    result=json.dumps(report(specs),indent=2)
    if args.output:
        with args.output.open('x',encoding='utf-8') as output:
            output.write(result+'\n')
    else:
        print(result)


if __name__=='__main__':
    main()
