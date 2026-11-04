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


def rich_cases(cases):
    ids = ['rich-link', 'rich-list', 'structured']
    selected = [c for c in cases if c['id'] in ids]
    if sorted(c['id'] for c in selected) != sorted(ids) or any(not isinstance(c.get('html'), str) or not c['html'].strip() for c in selected):
        raise ValueError('All three distinct frozen rich cases require nonempty HTML')
    if any(c.get('html') and c['id'] not in ids for c in cases):
        raise ValueError('Unexpected rich fixture outside frozen coverage')
    return selected


def markup_shape(root):
    """Known fixture markup only; does not claim browser CSS/layout verification."""
    shape = dict(links=[], bold=[], lists=0, items=0, code=[], list_structure=[])
    bold_run = []

    def plain(node):
        return node.get('text', '') + ''.join(plain(c) for c in node.get('children', []))

    def style(node, property_name, default):
        declarations = dict(part.split(':', 1) for part in node.get('attrs', {}).get('style', '').split(';') if ':' in part)
        return {k.strip().lower():v.strip().lower() for k, v in declarations.items()}.get(property_name, default)

    def visit(node, bold=False, depth=0):
        tag = node.get('tag')
        attrs = node.get('attrs', {})
        weight = style(node, 'font-weight', 'bold' if tag in ['STRONG', 'B'] or bold else 'normal')
        bold = weight in ['bold', 'bolder'] or (weight.isdigit() and int(weight) >= 600)
        if 'text' in node:
            if bold:
                bold_run.append(node['text'])
            elif bold_run:
                shape['bold'].append(''.join(bold_run));bold_run.clear()
        if tag == 'A':
            shape['links'].append([plain(node), attrs.get('href')])
        if tag == 'CODE':
            shape['code'].append(plain(node))
        if tag == 'LI':
            shape['items'] += 1
        if tag in ['OL', 'UL']:
            shape['lists'] += 1
            items = [c for c in node['children'] if c.get('tag') == 'LI']
            value = int(attrs.get('start', len(items) if 'reversed' in attrs else 1))
            ordinals = []
            for item in items:
                value = int(item.get('attrs', {}).get('value', value));ordinals.append(value)
                value += -1 if 'reversed' in attrs else 1
            default = style(node, 'list-style-type', 'decimal' if tag == 'OL' else 'disc')
            shape['list_structure'].append([tag.lower(), depth, len(items), ordinals if tag == 'OL' else None,
                                            [style(item, 'list-style-type', default) for item in items]])
            depth += 1
        for child in node.get('children', []):
            visit(child, bold, depth)

    visit(root)
    if bold_run:
        shape['bold'].append(''.join(bold_run))
    return shape


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--exe', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    here = Path(__file__).parent
    args.output.mkdir(parents=True, exist_ok=False)
    cases = rich_cases(json.loads((here / 'cases-v1.json').read_text(encoding='utf-8')))
    references = json.loads((here / 'formats-v1.json').read_text(encoding='utf-8'))
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
        tree = Tree(output['html'].split('<!--StartFragment-->')[1].split('<!--EndFragment-->')[0]).root
        if markup_shape(tree) != references[case['id']]['expected']:
            raise ValueError('Frozen markup formatting mismatch: ' + case['id'])
    report = dict(scope='headless mapper diagnosis only; no destination paste or live acceptance',
                  actual_http_requests=0, binary_sha256=hashlib.sha256(args.exe.read_bytes()).hexdigest(),
                  passed=len(cases), cases=[dict(case_id=c['id'], result=r) for c, r in zip(cases, outputs)])
    (args.output / 'report.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(f'{len(cases)}/{len(cases)} frozen rich mapper cases passed; destination behavior untested')


if __name__ == '__main__':
    main()
