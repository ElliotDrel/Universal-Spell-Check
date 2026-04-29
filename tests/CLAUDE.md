# tests/ — Python Fine-Tune & Benchmark Tooling

## Grounding

Pytest suites for the **Python fine-tune dataset + benchmark scripts** that live under `.claude/skills/finetune-cycle/scripts/`. These are not tests for the C# product — they protect the dataset/eval tooling that consumes the unified JSONL log corpus. The whole reason logs are unified across Prod and Dev (with per-line `channel`/`app_version` stamps) is to feed this pipeline cleanly.

## Read first

> Before changing anything here, read root `CLAUDE.md` for routing + hard rules, and `docs/replacements-and-logging.md` for the JSONL log schema these scripts consume. The actual scripts under test live in `.claude/skills/finetune-cycle/scripts/` — read the script before changing the test.

## What's here

```text
tests/
|-- test_benchmark_spellcheck_models.py    # Tests for benchmark_spellcheck_models.py
`-- test_export_openai_finetune_dataset.py # Tests for export_openai_finetune_dataset.py
```

The scripts under test:

```text
.claude/skills/finetune-cycle/scripts/
|-- benchmark_spellcheck_models.py   # Compare model variants against a stable dataset
|-- export_openai_finetune_dataset.py # Build OpenAI fine-tune JSONL from log corpus
|-- submit_finetune.py                # Submit fine-tune job
`-- tests/                            # Script-local fixtures
```

## Run

```powershell
python -m pytest tests/ -v
```

Outputs from real (non-test) runs land in `benchmark_runs/` and `fine_tune_runs/` under dated subfolders — never commit those run artifacts.

## Top-of-mind reminders

- **Log schema is the contract.** If you change a JSONL field name in `DiagnosticsLogger.cs`, expect this tooling to break. Update both sides in the same change.
- **Dataset stability matters.** `stable_spellcheck_cases-*` versioned files are inputs for reproducible benchmarks; do not edit them in place — version a new one.
- **Network calls go through transports** (see `MockTransport` in the benchmark tests). Tests must not hit real APIs.

---

## Keeping this file current

When fine-tune scripts move, get renamed, or the JSONL log schema changes, update this file in the same change. **If you spot drift between this doc and the actual scripts under `.claude/skills/finetune-cycle/scripts/` — missing file, renamed function, changed output dir — flag it to the user with a proposed fix.** Don't silently work around it.
