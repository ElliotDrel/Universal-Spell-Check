"""Headless replay coverage and markup-shape gates, independent of pass flags."""
import copy
import json
from pathlib import Path
import unittest
from check_fixture_replay import markup_shape, rich_cases
from test_editor import Tree


class FixtureReplayTests(unittest.TestCase):
    def test_missing_duplicate_empty_or_extra_rich_case_is_rejected(self):
        cases = json.loads((Path(__file__).parent / 'cases-v1.json').read_text(encoding='utf-8'))
        self.assertEqual(len(rich_cases(cases)), 3)
        for invalid in [cases[:-1], cases + [cases[-1]], cases + [dict(id='extra', html='<p>x</p>')]]:
            with self.assertRaises(ValueError):
                rich_cases(invalid)
        invalid = copy.deepcopy(cases)
        invalid[-1]['html'] = ' '
        with self.assertRaises(ValueError):
            rich_cases(invalid)

    def test_all_frozen_markup_fields_are_checked(self):
        references = json.loads((Path(__file__).parent / 'formats-v1.json').read_text(encoding='utf-8'))
        html = '<div><p>Please <strong>check</strong> these items:</p><ol><li><p>First document.</p></li><li><p>Use <code>C:\\sample\\notes.txt</code> and the <a href="https://example.com/guide">guide</a>.</p></li></ol></div>'
        self.assertEqual(markup_shape(Tree(html).root), references['structured']['expected'])
        for changed in [html.replace('check</strong>', 'wrong</strong>'), html.replace('>guide</a>', '>wrong</a>'),
                        html.replace('https://example.com/guide', 'https://example.com/wrong'),
                        html.replace('notes.txt</code>', 'wrong.txt</code>'), html.replace('<ol>', '<ol start="2">'),
                        html.replace('<li><p>First', '<li style="list-style-type: none"><p>First'),
                        html.replace('<ol>', '<ol><li><ol>').replace('</ol>', '</ol></li></ol>')]:
            self.assertNotEqual(markup_shape(Tree(changed).root), references['structured']['expected'])


if __name__ == '__main__':
    unittest.main()
