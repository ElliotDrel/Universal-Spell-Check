# Fine-Tune Cycle Skill — Design

**Date:** 2026-04-23
**Status:** Approved (pending user review of written spec)

## Goal

Systematize the spellcheck fine-tuning workflow into a single agent-driven skill. The user invokes one skill when they want to fine-tune or benchmark; the skill walks through the steps with clear decision gates and writes everything into one dated folder per run.

## Non-goals

- Automated scheduling — runs are user-triggered
- Human curation of training data — all successful log entries, minus existing filters (short text, prompt leaks)
- Automated commit/push — agent edits the AHK file; user reviews and commits manually

## Core principle

**File presence IS the state.** No separate state machine. Each step produces a file in the run folder. To know what's done, look at what exists. To resume, rerun the skill — each script is idempotent.

## Modes

The skill opens by asking which mode:

> "What do you want to do?
> 1. Full fine-tune run (export → fine-tune → benchmark → deploy)
> 2. Benchmark only (compare models against the existing dataset)"

### Mode 1: Full fine-tune run

| # | Step | Decision gate |
|---|------|---------------|
| 1 | Create run folder + export dataset from logs | Show example count + bucket distribution + 3 random pairs + rough cost → upload? |
| 2 | Submit fine-tune job to OpenAI + poll until complete | Automatic (may take 30+ min) |
| 3 | Benchmark: current deployed model, new `ft:` model, `gpt-4.1` baseline, + any extras user adds | Automatic |
| 4 | Read `benchmark.json` and present side-by-side results | Show comparison → deploy? |
| 5 | If deploying: update AHK `modelModule`, bump `scriptVersion` | Agent edits file; user commits manually |

**Current deployed model discovery:** skill reads `modelModule := "<value>"` from `Universal Spell Checker.ahk`.

**Pre-upload gate content:**
- total example count + per-bucket distribution (from `dataset_summary.json`)
- 3 random pairs from `train.jsonl` for an eyeball quality check
- rough cost = `total_chars_in_train / 4 * <per-token price>` — just a number, no model needed

### Mode 2: Benchmark only

1. Ask which models to compare (defaults: current deployed + `gpt-4.1`; user can add extras)
2. Run benchmark against all selected models
3. Write `summary.md` + `benchmark.json` to a dated folder under `benchmark_runs/`

```
benchmark_runs/
  2026-04-23-150000/
    summary.md
    benchmark.json
```

## Directory structure

```
fine_tune_runs/
  2026-04-23-143000/
    summary.md              # user-facing doc, appended after each step
    train.jsonl             # fine-tune training data
    validation.jsonl        # fine-tune validation data
    dataset_summary.json    # written by export script
    finetune_job.json       # written by submit_finetune.py (status, job_id, ft: model)
    benchmark.json          # written by benchmark script

benchmark_runs/
  2026-04-23-150000/        # mode 2 only
    summary.md
    benchmark.json
```

Filenames are fixed and predictable. "Current run" = most recently dated folder in `fine_tune_runs/`. To abandon a run, delete the folder.

## `summary.md` structure

One document, grown as steps complete. Each script appends its section (idempotent: skip if header already present).

```markdown
# Fine-Tune Run 2026-04-23-143000
Base model: gpt-4.1 · Started: 2026-04-23 14:30

## 1. Dataset Export (completed 14:33)
120 examples (104 train / 16 val) · 151 excluded as duplicates of prior runs
[bucket distribution table]

## 2. Fine-Tune Job (completed 15:08)
Job ID: ftjob-abc123 · Model: ft:gpt-4.1-2025-04-14:...
Training duration: 34 min · Final loss: 0.42

## 3. Benchmark (completed 15:10)
Models: ft:gpt-4.1-... · gpt-4.1 (current) · gpt-4.1-2025-04-14 (baseline)
[side-by-side exact match % and median API ms]
[per-bucket matrix per model]

## 4. Decision
Deployed at 15:12
modelModule: gpt-4.1 → ft:gpt-4.1-2025-04-14:...
scriptVersion: 21 → 22
```

## Skill structure

```
.claude/skills/finetune-cycle/
  SKILL.md                    # agent-facing workflow + resume logic + gates
  scripts/
    submit_finetune.py        # NEW: upload + create job + poll
  references/
    openai_finetune_api.md    # API calls, error codes, parameters
```

No `start_run.py` or `finalize_run.py` — the agent does those steps directly (mkdir + write summary.md).

## Resume logic (in SKILL.md)

When the skill starts Mode 1, check the most recent folder in `fine_tune_runs/`:

| State on disk | Action |
|---|---|
| No folder, or all steps complete and deploy done | Start a new run |
| `train.jsonl` missing | Run export |
| `finetune_job.json` missing | Run `submit_finetune.py` |
| `finetune_job.json` has `status: running` | Rerun `submit_finetune.py` (it resumes polling from the `job_id` inside) |
| `benchmark.json` missing | Run benchmark |
| All files present, no deploy yet | Jump to deploy gate |

That's the whole resume story.

## `submit_finetune.py` — the new script

**Interface:**
```bash
python submit_finetune.py --run-dir fine_tune_runs/2026-04-23-143000
```

**Behavior:**

1. Read `finetune_job.json` if present. If `status == "succeeded"`, exit 0 (idempotent). If `status == "running"` with `job_id`, skip upload/create and resume polling.
2. Upload `train.jsonl` and `validation.jsonl` via `client.files.create(purpose="fine-tune")`. Write file IDs to `finetune_job.json`.
3. Create the job: `client.fine_tuning.jobs.create(model="gpt-4.1-2025-04-14", training_file=..., validation_file=...)`. Write `job_id` and `status: running` to `finetune_job.json`.
4. Poll every 60s via `client.fine_tuning.jobs.retrieve(job_id)`. Update `finetune_job.json` with the latest status each poll. Print one-line progress to console.
5. On `succeeded`: write `fine_tuned_model` to `finetune_job.json`, append step-2 section to `summary.md`, exit 0.
6. On `failed`: write the error to `finetune_job.json` and `summary.md`, exit non-zero.

**Error handling:**
- Network errors during poll → exponential backoff retry
- Ctrl-C during poll → state stays as `running` with `job_id`; next invocation resumes polling without re-upload
- OpenAI validation errors (bad JSONL) → full error written to `finetune_job.json` + `summary.md`

**Dependencies:** `openai` Python SDK, `OPENAI_API_KEY` from existing `.env`.

## Existing script changes

Both additive — existing flags keep working.

**`benchmark_spellcheck_models.py`:** add `--run-dir <path>`. When set:
- Output file is `<run-dir>/benchmark.json`
- Models to run come from `--models` (explicit list passed by skill)
- Appends benchmark section to `<run-dir>/summary.md` (idempotent: skip if header present)
- **Leakage fix:** when benchmarking inside a fine-tune run folder, exclude that run's own `train.jsonl` and `validation.jsonl` pairs from the eval set
- `load_all_eval_datasets()` updated to scan `fine_tune_runs/*/` instead of `fine_tune_data/previous_batches/`
- Old `--output-dir` path still works for ad-hoc runs

**`export_openai_finetune_dataset.py`:** add `--run-dir <path>`. When set:
- Writes `train.jsonl` / `validation.jsonl` / `dataset_summary.json` into run folder
- Appends dataset section to `summary.md`
- Dedup scans `fine_tune_runs/*/train.jsonl` and `validation.jsonl` (all folders that exist — if you deleted one to abandon it, its pairs come back in scope automatically)
- Old `--output-dir` path still works

## Deploy step

After user approves at step 5:
1. Read the line `modelModule := "<value>"` from `Universal Spell Checker.ahk`
2. Replace the value with `fine_tuned_model` from `finetune_job.json`
3. Read `scriptVersion := "<N>"` (integer-as-string, currently `"21"`) and increment by 1
4. Append deploy section to `summary.md` with before/after values
5. Tell user: "Deployed. Review the diff and commit when ready."

Agent does NOT run git commit or push.

## Migration

None needed. The old `fine_tune_data/previous_batches/` and `latest_batch/` folders can be left in place or deleted by hand — the new dedup logic only scans `fine_tune_runs/`. A one-time miss on the April 11 batch is acceptable (solo dev, ~150 duplicate pairs would just get re-exported once).

## Open questions

Implementation plan will address:
- Exact markdown formatting for each `summary.md` section (tables, field choices)
- Whether `submit_finetune.py` needs `--dry-run` for testing without actually submitting
