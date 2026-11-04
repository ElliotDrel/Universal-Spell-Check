"""Exercise the real text observer on retained synthetic DOM trees, without a browser.

The small DOM adapter models only selectors/innerText used by text(); it does not
simulate Chrome editing, layout, clipboard or paste behavior.
"""
import json
import re
import subprocess
import unittest
from html.parser import HTMLParser
from pathlib import Path


HERE = Path(__file__).parent


class Tree(HTMLParser):
    def __init__(self, html):
        super().__init__()
        self.root = dict(tag='DIV', children=[])
        self.stack = [self.root]
        self.feed(html)

    def handle_starttag(self, tag, attrs):
        node = dict(tag=tag.upper(), children=[], attrs=dict(attrs))
        self.stack[-1]['children'].append(node)
        if tag not in ['br', 'img', 'meta', 'hr', 'input', 'link']:
            self.stack.append(node)

    def handle_endtag(self, tag):
        if len(self.stack) > 1:
            self.stack.pop()

    def handle_data(self, data):
        self.stack[-1]['children'].append(dict(text=data))


class EditorTextTests(unittest.TestCase):
    def observe(self, html):
        script = (HERE / 'editor.html').read_text(encoding='utf-8')
        functions = '\n'.join(re.findall(r'^function (?:ordinals|text)\(.*$', script, re.M))
        adapter = r'''
const fs=require('node:fs');
function node(raw,parent=null){
 const n={tagName:raw.tag,parentElement:parent,children:[],attrs:raw.attrs||{}};
 n.children=(raw.children||[]).filter(c=>c.tag).map(c=>node(c,n));
 n.innerText=(raw.children||[]).map(c=>c.text===undefined?node(c).innerText:c.text).join(raw.tag==='OL'||raw.tag==='LI'?'\n':'');
 n.matches=s=>s.split(',').includes(n.tagName.toLowerCase());
 n.closest=s=>n.matches(s)?n:parent?.closest(s);
 n.querySelectorAll=s=>n.children.flatMap(c=>[...(c.matches(s)?[c]:[]),...c.querySelectorAll(s)]);
 n.querySelector=s=>n.querySelectorAll(s)[0];
 n.hasAttribute=s=>Object.hasOwn(n.attrs,s);n.start=Number(n.attrs.start||1);n.reversed=Object.hasOwn(n.attrs,'reversed');n.value=Number(n.attrs.value);
 return n;
}
const editor=node(JSON.parse(fs.readFileSync(0,'utf8')));
'''
        result = subprocess.run(['node', '-e', adapter + functions + '\nprocess.stdout.write(text());'],
                                input=json.dumps(Tree(html).root), text=True, encoding='utf-8',
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=True)
        return result.stdout

    def test_wrapped_paragraph_is_observed_without_losing_lists_or_literals(self):
        record = json.loads((HERE / 'records/fixture-pilot.json').read_text(encoding='utf-8'))
        row = next(r for r in record['pilot_attempts'] if r['case_id'] == 'structured')
        case = next(c for c in json.loads((HERE / 'cases-v1.json').read_text(encoding='utf-8')) if c['id'] == 'structured')
        self.assertNotEqual(row['observed']['text'], case['expected'])
        self.assertEqual(self.observe(row['observed']['html']), case['expected'])

    def test_nested_list_failure_still_fails_the_frozen_text_expectation(self):
        record = json.loads((HERE / 'records/fixture-pilot.json').read_text(encoding='utf-8'))
        row = next(r for r in record['pilot_attempts'] if r['case_id'] == 'rich-list')
        case = next(c for c in json.loads((HERE / 'cases-v1.json').read_text(encoding='utf-8')) if c['id'] == 'rich-list')
        self.assertNotEqual(self.observe(row['observed']['html']), case['expected'])
        corrected_flat = '<ol><li><p>First item.</p></li><li><p>Second item.</p></li></ol>'
        self.assertEqual(self.observe(corrected_flat), case['expected'])


if __name__ == '__main__':
    unittest.main()
