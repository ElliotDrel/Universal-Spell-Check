"""Matched headless mapper parity/scaling diagnosis, never live/editor acceptance."""
import argparse
import hashlib
import json
from pathlib import Path
import statistics
import subprocess
import time
from check_fixture_replay import cf_html


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--control', required=True, type=Path)
    parser.add_argument('--candidate', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    here = Path(__file__).parent
    cases = [dict(id=c['id'], source_html=cf_html(c['html']), input=c['text'], output=c['expected'])
             for c in json.loads((here / 'cases-v1.json').read_text(encoding='utf-8')) if c.get('html')]
    for count in [100, 1000]:
        words = [f'node{i:04d} teh café🙂 ' for i in range(count)]
        text = ''.join(words)
        cases.append(dict(id=f'scaling-{count}-spans', source_html=cf_html('<p data-pm-slice="0 0 []">' +
                          ''.join(f'<span>{w}</span>' for w in words) + '</p>'), input=text, output=text.replace('teh', 'the', 1)))
    # Paragraph gaps, repeated/omitted nodes and Unicode exercise the full reconstruction too.
    for index, (html, source, target) in enumerate([
        ('<p>omitted</p><p>🙂 teh word</p>', '🙂 teh word', '🙂 the word'),
        ('<p>teh</p><p>teh longer</p>', 'teh longer\n\nteh', 'the longer\n\nthe'),
        ('<p>a</p><p>abab</p><p>abX</p>', 'abababX', 'abababY'),
        ('<p>🙂 teh</p><p>café teh</p>', '🙂 teh\n\ncafé teh', '🙂 the\n\ncafé the'),
    ]):
        cases.append(dict(id=f'edge-{index}', source_html=cf_html('<div data-pm-slice="1 1 []">'+html+'</div>'), input=source, output=target))
    (args.output / 'cases.json').write_text(json.dumps(cases, indent=2), encoding='utf-8')
    report = dict(scope='headless process-inclusive mapper diagnostic; no clipboard/editor/API/live score',
                  actual_http_requests=0, executable_sha256={name:hashlib.sha256(path.read_bytes()).hexdigest()
                  for name, path in [('control', args.control), ('candidate', args.candidate)]}, cases=[])
    for case in cases:
        samples = dict(control=[], candidate=[])
        outputs = []
        payload = json.dumps(case) + '\n'
        for ordinal, name in enumerate(['control', 'candidate', 'candidate', 'control'] * 2):
            exe = args.control if name == 'control' else args.candidate
            start = time.perf_counter()
            # Only operator-selected local builds; fixture content is stdin, never command text.
            result = subprocess.run([str(exe.resolve()), '--rich-json'], shell=False, input=payload, text=True, encoding='utf-8',  # nosemgrep: python.lang.security.audit.dangerous-subprocess-use-audit, python.lang.security.audit.dangerous-subprocess-use-tainted-env-args
                                    stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=True)
            elapsed = (time.perf_counter() - start) * 1000
            value = json.loads(result.stdout)
            outputs.append(value)
            samples[name].append(elapsed)
            with (args.output / 'attempts.jsonl').open('a', encoding='utf-8') as file:
                file.write(json.dumps(dict(case_id=case['id'], ordinal=ordinal, executable=name, elapsed_ms=elapsed,
                                           result=value), ensure_ascii=False) + '\n')
        parity = all(value == outputs[0] for value in outputs)
        expected = outputs[0]['applied'] and outputs[0]['text'] == case['output'] if not case['id'].startswith('edge-') else None
        row = dict(case_id=case['id'], exact_output_parity=parity, expected_text_and_applied=expected,
                   process_ms=samples, median_process_ms={name:statistics.median(values) for name, values in samples.items()})
        report['cases'].append(row)
        (args.output / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
        print(case['id'], 'parity', parity, flush=True)
        if not parity or expected is False:
            raise SystemExit('Mapper parity/correctness diagnostic failed; artifacts retained')


if __name__ == '__main__':
    main()
