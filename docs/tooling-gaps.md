# Tooling Gaps — Debugging Workflow

The three gaps from the `Competitionetition` investigation shipped on 2026-06-01. The two gaps from the 2026-09-28 ChatGPT formatting incident are now addressed: `logs.py --event spellcheck_detail --last 2` searches across midnight and shows the rich-text decision, while `spellcheck_detail` records the final requested `paste_text` and `paste_html` payloads. The log still cannot prove how a destination website rendered a paste. Add further instrumentation only if a real incident shows a remaining gap.

## Development environment verification

The 2026-10-03 environment setup uses Python 3.14.8 and the dependencies pinned in `requirements-dev.txt`. The native app builds in Dev and Release, all three C# executable test suites pass, and published startup/dashboard smoke modes exit 0. Live API correction and installed auto-update flows still require separate acceptance testing.

The full Python run (`.venv/Scripts/python.exe -m pytest tests/ bench/ --continue-on-collection-errors -q`) reports 26 passed, four failed, and one collection error. These repository function mismatches also appear before the Velopack update:

- Three tests in `tests/test_benchmark_spellcheck_models.py` call missing `save_stable_dataset` or `build_versioned_dataset_path` helpers in `benchmark_spellcheck_models.py`.
- One test in `tests/test_export_openai_finetune_dataset.py` calls the same missing `save_stable_dataset` helper.
- `bench/test_compare.py` imports missing `build_delta_rows` and `format_delta_table` functions from `bench/compare.py`.

Resolve whether these APIs are still intended before changing implementations or tests. Do not skip the failing cases or describe the Python suite as passing. `google-genai` also emits an upstream Python 3.14 deprecation warning for `_UnionGenericAlias`; it does not fail the passing tests.

Native compilation retains the documented channel-specific CS0162 warnings and an existing CS8602 nullable warning at `SpellCheckAppContext.cs:49`. The dependency update does not suppress those warnings.
