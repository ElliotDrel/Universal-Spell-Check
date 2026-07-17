# tests/ — Regression, Fine-Tune, and Benchmark Tooling

## Grounding

Three kinds of regression suites live here:

1. **Python fine-tune dataset + benchmark scripts** under `.agents/skills/finetune-cycle/scripts/` — protecting the dataset/eval tooling that consumes the unified JSONL log corpus. The whole reason logs are unified across Prod and Dev (with per-line `channel`/`app_version` stamps) is to feed this pipeline cleanly.
2. **Regression tests for C# product logic**, reimplemented in Python to keep the feedback loop fast (`test_text_post_processor.py`). The first of these guards the `TextPostProcessor` replacement algorithm — see `docs/tooling-gaps.md` § 2. We backfill these slowly as bugs surface; we are *not* aiming for blanket coverage of the C# product here.
3. **Package-free C# executable tests** for hot-path logic where matching the production implementation exactly matters (`ProtectedTextTests/` and `TargetFormattingTests/`).

## Read first

> Before changing anything here, read root `CLAUDE.md` for routing + hard rules, and `docs/replacements-and-logging.md` for the JSONL log schema these scripts consume. The actual scripts under test live in `.agents/skills/finetune-cycle/scripts/` — read the script before changing the test.

## What's here

```text
tests/
|-- ProtectedTextTests/                    # Real C# literal extraction/restoration tests
|-- TargetFormattingTests/                 # Rule matching, hooks, identity, literal safety, resolver microbench
|-- test_benchmark_spellcheck_models.py    # Tests for benchmark_spellcheck_models.py
|-- test_export_openai_finetune_dataset.py # Tests for export_openai_finetune_dataset.py
|-- test-replacements.py                   # Replacements dry-run helper
`-- test_text_post_processor.py            # C# TextPostProcessor regression (via tests/test-replacements.py)
```

`test_text_post_processor.py` imports the replacement logic from `tests/test-replacements.py` (a faithful Python port of `TextPostProcessor.ApplyReplacements`) and asserts the invariant that caught the Competitionetition bug: no variant may be a substring of its own canonical, and every canonical must pass through unchanged.

The scripts under test:

```text
.agents/skills/finetune-cycle/scripts/
|-- benchmark_spellcheck_models.py   # Compare model variants against a stable dataset
|-- export_openai_finetune_dataset.py # Build OpenAI fine-tune JSONL from log corpus
|-- submit_finetune.py                # Submit fine-tune job
`-- tests/                            # Script-local fixtures
```

## Run

```powershell
python -m pytest tests/ -v
dotnet run --project tests/ProtectedTextTests/UniversalSpellCheck.ProtectedTextTests.csproj -c Release
dotnet run --project tests/TargetFormattingTests/UniversalSpellCheck.TargetFormattingTests.csproj -c Release
```

Outputs from real (non-test) runs land in `benchmark_runs/` and `fine_tune_runs/` under dated subfolders — never commit those run artifacts.

## Top-of-mind reminders

- **Log schema is the contract.** If you change a JSONL field name in `DiagnosticsLogger.cs`, expect this tooling to break. Update both sides in the same change.
- **Dataset stability matters.** `stable_spellcheck_cases-*` versioned files are inputs for reproducible benchmarks; do not edit them in place — version a new one.
- **Network calls go through transports** (see `MockTransport` in the benchmark tests). Tests must not hit real APIs.

---

## Keeping this file current

When fine-tune scripts move, get renamed, or the JSONL log schema changes, update this file in the same change. **If you spot drift between this doc and the actual scripts under `.agents/skills/finetune-cycle/scripts/` — missing file, renamed function, changed output dir — flag it to the user with a proposed fix.** Don't silently work around it.
