"""Replay frozen rich cases through the actual mapper; no browser, clipboard or API."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
from test_editor import Tree


def cf_html(html):
    if '<!--StartFragment-->' in html:
        return html
    prefix = '<html><body><!--StartFragment-->'
    suffix = '<!--EndFragment--></body></html>'
    header = 'Version:0.9\r\nStartHTML:{:010d}\r\nEndHTML:{:010d}\r\nStartFragment:{:010d}\r\nEndFragment:{:010d}\r\n'
    start = len(header.format(0, 0, 0, 0).encode())
    fragment = start + len(prefix.encode())
    end = fragment + len(html.encode())
    return header.format(start, end + len(suffix.encode()), fragment, end) + prefix + html + suffix


def counts(node, tag):
    return int(node.get('tag') == tag) + sum(counts(c, tag) for c in node.get('children', []))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--exe', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    here = Path(__file__).parent
    args.output.mkdir(parents=True, exist_ok=False)
    cases = [c for c in json.loads((here / 'cases-v1.json').read_text(encoding='utf-8')) if c.get('html')]
    rows = [dict(source_html=cf_html(c['html']), input=c['text'], output=c['expected']) for c in cases]
    # Operator-selected local build, fixed CLI mode, fixture data only on stdin; never a shell command.
    result = subprocess.run([str(args.exe.resolve()), '--rich-json'], shell=False, input=''.join(json.dumps(r) + '\n' for r in rows),  # nosemgrep: python.lang.security.audit.dangerous-subprocess-use-audit, python.lang.security.audit.dangerous-subprocess-use-tainted-env-args
                            text=True, encoding='utf-8', stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False)
    (args.output / 'mapper.stdout.jsonl').write_text(result.stdout, encoding='utf-8')
    (args.output / 'mapper.stderr.txt').write_text(result.stderr, encoding='utf-8')
    if result.returncode:
        raise SystemExit(result.returncode)
    outputs = [json.loads(line) for line in result.stdout.splitlines()]
    if len(outputs) != len(cases):
        raise ValueError('Missing or extra native mapper results')
    for case, output in zip(cases, outputs):
        if not output['applied'] or output['text'] != case['expected']:
            raise ValueError('Native mapper mismatch: ' + case['id'])
        if case['id'] in ['rich-list', 'structured']:
            tree = Tree(output['html'].split('<!--StartFragment-->')[1].split('<!--EndFragment-->')[0]).root
            if counts(tree, 'OL') != 1 or counts(tree, 'LI') != 2:
                raise ValueError('Native list structure mismatch: ' + case['id'])
    report = dict(scope='headless mapper diagnosis only; no destination paste or live acceptance',
                  actual_http_requests=0, binary_sha256=hashlib.sha256(args.exe.read_bytes()).hexdigest(),
                  passed=len(cases), cases=[dict(case_id=c['id'], result=r) for c, r in zip(cases, outputs)])
    (args.output / 'report.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(f'{len(cases)}/{len(cases)} frozen rich mapper cases passed; destination behavior untested')


if __name__ == '__main__':
    main()
