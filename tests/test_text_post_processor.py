"""
Regression tests for the TextPostProcessor replacement logic.

Implemented in Python against the authoritative reimplementation in
.claude/scripts/test-replacements.py, which mirrors TextPostProcessor.ApplyReplacements
exactly (left-to-right single-pass scan, longest-first sorting, URL placeholder protection).

Gap covered: docs/tooling-gaps.md § 2 — no unit tests for TextPostProcessor.
These two tests directly encode the invariant that caught the Competitionetition bug:
  "A variant that is a substring of its canonical must never be loaded as a replacement."
"""
import json
import importlib.util
import sys
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parent.parent
REPLACEMENTS_PATH = REPO_ROOT / "replacements.json"

_script = REPO_ROOT / ".claude" / "scripts" / "test-replacements.py"
_spec = importlib.util.spec_from_file_location("test_replacements", _script)
_mod = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_mod)
apply_replacements = _mod.apply_replacements
load_pairs = _mod.load_pairs


@pytest.fixture(scope="module")
def replacements_data():
    with open(REPLACEMENTS_PATH, encoding="utf-8-sig") as f:
        return json.load(f)


@pytest.fixture(scope="module")
def pairs():
    loaded, _ = load_pairs(REPLACEMENTS_PATH)
    return loaded


def test_no_variant_is_substring_of_its_canonical(replacements_data):
    """Regression: variant 'Burton Morgan Comp' was a substring of canonical
    'Burton Morgan Competition', causing string.Replace to corrupt correct text
    ('Competition' -> 'Competitionetition'). No such pair must ever be loaded."""
    bad = []
    for canonical, variants in replacements_data.items():
        for variant in variants:
            if variant and variant != canonical and variant in canonical:
                bad.append(f"{variant!r} is substring of {canonical!r}")
    assert not bad, (
        "replacements.json contains unsafe variants (variant is substring of canonical):\n"
        + "\n".join(f"  {b}" for b in bad)
    )


def test_correct_text_passes_through_unchanged(replacements_data, pairs):
    """For every canonical in replacements.json, running it through ApplyReplacements
    must return the canonical unchanged. This catches any variant that would corrupt
    already-correct text — including the idempotency failure that caused the bug."""
    corrupted = []
    for canonical in replacements_data:
        output, applied = apply_replacements(canonical, pairs)
        if output != canonical:
            corrupted.append(f"{canonical!r} -> {output!r} (applied: {applied})")
    assert not corrupted, (
        "ApplyReplacements corrupted already-correct canonicals:\n"
        + "\n".join(f"  {c}" for c in corrupted)
    )
