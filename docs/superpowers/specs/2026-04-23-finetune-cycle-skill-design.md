# Fine-Tune Cycle Skill — Design

**Date:** 2026-04-23
**Status:** Approved (pending user review of written spec)

## Goal

Systematize the spellcheck fine-tuning workflow into a single, resumable, agent-driven pipeline. The user invokes one skill when they decide to do a fine-tune run; the skill walks through data collection, benchmarking, fine-tuning, comparison, and deployment with clear decision gates.

Primary outcomes:
- One source of truth per run (a dated folder under `fine_tune_runs/`) containing every artifact
- One human-facing `summary.md` per run that grows as stages complete
- A `run.json` state machine so any stage can be resumed after interruption
- End-to-end automation for the OpenAI fine-tuning API steps (upload, create job, poll, return `ft:` model ID)
- Clear before/after benchmark comparison to drive the deploy decision

## Non-goals

- Automated scheduling — runs are user-triggered, not volume- or time-based
- Human curation of training data — all successful log entries are used, minus existing automatic filters (short text, prompt leaks)
- Automated commit/push of deploy changes — agent edits the AHK file but user reviews and commits manually

## Modes

The skill opens by asking which mode:

> "What do you want to do?
> 1. Full fine-tune run (export data → fine-tune → benchmark → deploy)
> 2. Benchmark only (compare models against the existing dataset)"

### Mode 1: Full fine-tune run

Five stages with decision gates:

| # | Stage | Decision gate |
|---|-------|---------------|
| 1 | Create run folder + export dataset from logs | Show example count + bucket distribution + 3 random sample pairs + est. cost/time → upload? |
| 2 | Submit fine-tune job to OpenAI + poll until complete | Automatic (may take 30+ min) |
| 3 | Benchmark: current deployed model, new `ft:` model, `gpt-4.1` baseline, + any extras user adds | Automatic |
| 4 | Write comparison to summary.md | Show side-by-side results → deploy? |
| 5 | If deploying: update AHK `modelModule`, bump `scriptVersion` | Agent edits file; user commits manually |

At stage 3, before running, the skill surfaces the default model list and asks: "Want to add any additional models (e.g. gpt-5-mini)?"

**Current deployed model discovery:** the skill reads the line `modelModule := "<value>"` from `Universal Spell Checker.ahk` to determine which model is currently deployed. That model is automatically added to `benchmark_models` in `run.json`.

**Pre-upload gate content:** before stage 2 kicks off, the skill shows:
- total example count + per-bucket distribution
- 3 random pairs from `train.jsonl` (user text → assistant text) so the user can eyeball quality
- rough cost estimate (OpenAI's per-token fine-tune pricing × total tokens in train.jsonl) + rough wall-time estimate (~30–60 min typical)

### Mode 2: Benchmark only

1. Ask which models to compare (defaults to current deployed model + `gpt-4.1`; user can add others)
2. Run benchmark against all selected models
3. Write results to a lightweight dated folder under `benchmark_runs/`

```
benchmark_runs/
  2026-04-23-150000/
    summary.md
    benchmark.json
```

No `run.json` state machine needed — benchmark-only runs are one-shot and not resumable.

## Directory structure

All run artifacts live in one dated folder. No more splitting across `benchmark_data/`, `fine_tune_data/`, and `logs/benchmarks/` for a given run.

```
fine_tune_runs/
  2026-04-23-143000/              # one folder per run, timestamp = run start
    summary.md                    # THE user-facing doc — appended at each stage
    run.json                      # state machine, machine-readable
    train.jsonl                   # fine-tune training data
    validation.jsonl              # fine-tune validation data
    dataset_summary.json          # export metadata
    finetune_job.json             # OpenAI job_id, status, ft: model_id
    benchmark.json                # multi-model results (current, ft:, baseline, extras)

benchmark_runs/
  2026-04-23-150000/              # benchmark-only runs (mode 2)
    summary.md
    benchmark.json
```

Rules:
- Filenames are fixed and predictable — no timestamps inside the run folder
- Scripts that need "the current run" find the most recently dated folder in `fine_tune_runs/`
- Only one run folder is "active" at a time; the skill refuses to start a new run if an in-progress one exists (prompts to resume or abandon)

## `run.json` state machine

```json
{
  "run_id": "2026-04-23-143000",
  "base_model": "gpt-4.1",
  "benchmark_models": ["gpt-4.1", "ft:gpt-4.1-...", "gpt-4.1-2025-04-14"],
  "started_at": "2026-04-23 14:30:00",
  "completed_at": null,
  "deployed_at": null,
  "abandoned_at": null,
  "fine_tuned_model": null,
  "stages": {
    "export_dataset": { "status": "pending|running|completed|failed", "started_at": null, "completed_at": null },
    "finetune_job":   { "status": "...", "started_at": null, "completed_at": null, "job_id": null },
    "benchmark":      { "status": "...", "started_at": null, "completed_at": null },
    "comparison":     { "status": "...", "started_at": null, "completed_at": null }
  }
}
```

Stage status transitions: `pending → running → completed` (happy path) or `running → failed` (error path).

A run is "in progress" if `abandoned_at` is null AND (any stage is `running` OR all stages are `completed` but `deployed_at` is null). On skill invocation, the first thing checked is whether an in-progress run exists.

**Abandoning a run:** sets `abandoned_at` to the current timestamp and writes a note to `summary.md` explaining why. The folder is NOT deleted — it stays as evidence. Abandoned runs are skipped by:
- "in progress" detection (next invocation can start fresh)
- Dedup scans in future dataset exports (their train.jsonl/validation.jsonl pairs become available again)
- "Current run" lookups (most-recent-non-abandoned wins)

## `summary.md` structure

The user's one document for the run. Sections appear as stages complete:

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
Models compared: ft:gpt-4.1-... · gpt-4.1 (current) · gpt-4.1-2025-04-14 (baseline)
[side-by-side exact match % and median API ms per model]
[per-bucket matrix per model]

## 4. Decision
Deployed at 15:12
Updated modelModule: gpt-4.1 → ft:gpt-4.1-2025-04-14:...
Bumped scriptVersion: 1.42 → 1.43
```

## Skill structure

```
.claude/skills/finetune-cycle/
  SKILL.md                      # agent-facing workflow + gates
  scripts/
    start_run.py                # create run folder, init run.json
    submit_finetune.py          # NEW: upload + create job + poll
    finalize_run.py             # write comparison section, archive
  references/
    openai_finetune_api.md      # API calls, error codes, parameters
    state_machine.md            # run.json schema + transitions
    naming_conventions.md       # file names, where things live
```

`SKILL.md` is the entry point. It instructs the agent to:
1. Check for an in-progress run; if found, offer resume/abandon
2. Otherwise call `start_run.py` to create the run folder
3. For each stage, call the appropriate script with `--run-dir`, wait, read the updated `run.json`, summarize the result to the user, and ask the gate question
4. On deploy approval, edit the AHK file and bump the script version
5. Stop before commit/push (user's hard rule from CLAUDE.md)

## `submit_finetune.py` — the new script

**Interface:**
```bash
python submit_finetune.py --run-dir fine_tune_runs/2026-04-23-143000
```

**Behavior:**

1. Read `run.json`. If `finetune_job.status == "completed"`, exit 0 (idempotent). If `status == "running"` with a `job_id`, skip upload/create and resume polling.
2. Upload `train.jsonl` and `validation.jsonl` via `client.files.create(purpose="fine-tune")`. Write file IDs into `finetune_job.json`. Set stage status to `running` with `started_at`.
3. Create the fine-tuning job: `client.fine_tuning.jobs.create(model="gpt-4.1-2025-04-14", training_file=..., validation_file=...)`. Write `job_id` into `finetune_job.json` and `run.json.stages.finetune_job.job_id`.
4. Poll every 60 seconds via `client.fine_tuning.jobs.retrieve(job_id)`. On each poll, update `finetune_job.json` with latest status and any training metrics. Print one-line progress to console.
5. On `succeeded`: write `fine_tuned_model` to `run.json`, append completed section to `summary.md`, set stage to `completed`, exit 0.
6. On `failed`: write error details to `finetune_job.json` and `summary.md`, set stage to `failed`, exit non-zero.

**Error handling:**
- Network errors during poll → exponential backoff retry (pattern from existing benchmark script)
- Ctrl-C during poll → leave state as `running` with `job_id`; next invocation resumes polling without re-upload
- JSONL validation error from OpenAI → write full error to `finetune_job.json` and `summary.md`, fail stage

**Dependencies:**
- `openai` Python SDK (add to requirements if not present)
- Reads `OPENAI_API_KEY` from existing `.env`

## Existing script changes

Both changes are additive — existing flags keep working.

**`benchmark_spellcheck_models.py`:** add `--run-dir <path>`. When set:
- Output file is `<run-dir>/benchmark.json` (instead of timestamped dir under `logs/benchmarks/`)
- Models to run come from `run.json.benchmark_models` (fine-tune mode) or are passed via `--models` (benchmark-only mode)
- Appends the benchmark section to `<run-dir>/summary.md`
- Updates `<run-dir>/run.json` stage status
- Old `--output-dir` path still works for ad-hoc runs

**Fix to prevent train/eval data leakage:** the existing `load_all_eval_datasets()` function pulls eval cases from `fine_tune_data/previous_batches/` and `latest_batch/` into the benchmark. Under the new design this becomes `fine_tune_runs/*/`, and a freshly-created `ft:` model would be evaluated partly on its own training data.

The updated logic:
- `load_all_eval_datasets()` scans `fine_tune_runs/*/` for completed, non-abandoned runs
- When benchmarking inside a run folder, exclude that run's own `train.jsonl` and `validation.jsonl` pairs from the eval set
- The frozen benchmark dataset (`benchmark_data/stable_spellcheck_cases-*.json`) is unaffected and remains the primary eval source

**`export_openai_finetune_dataset.py`:** add `--run-dir <path>`. When set:
- Writes `train.jsonl` / `validation.jsonl` / `dataset_summary.json` into run folder
- Appends dataset section to `summary.md`
- Updates `run.json` stage status
- Old `--output-dir` path still works

**Deduplication scope:** dedup scans `fine_tune_runs/*/train.jsonl` and `fine_tune_runs/*/validation.jsonl`, but **only** from runs where `run.json.deployed_at != null` AND `abandoned_at == null`. Pairs from failed or abandoned runs remain available for future training.

## Deploy step

After user approves at stage 5, the agent:
1. Reads the line `modelModule := "<value>"` from `Universal Spell Checker.ahk`
2. Replaces the value with the `ft:` model ID from `run.json.fine_tuned_model`
3. Reads the line `scriptVersion := "<N>"` (integer-as-string, currently `"21"`) and increments the integer by 1
4. Writes the deploy section to `summary.md` (including before/after values for both fields)
5. Sets `run.json.deployed_at` to current timestamp
6. Tells user: "Deployed. Review the diff and commit when ready."

Agent does NOT run git commit or push — per CLAUDE.md's guidance on hard-to-reverse actions.

## Migration

One-time migration when the skill is first installed:

- `fine_tune_data/previous_batches/2026-04-11-193841/` → copied into `fine_tune_runs/2026-04-11-193841/` with a synthetic `run.json` marking it as a successful historical run. This ensures deduplication in future exports excludes these pairs.
- `benchmark_data/stable_spellcheck_cases-*.json` stays where it is — still used as the frozen benchmark dataset by `benchmark_spellcheck_models.py`.
- `fine_tune_data/latest_batch/` is deleted (its contents were already a staging area that's now replaced by run folders).

## Resumability scenarios

| Situation | What the skill does |
|---|---|
| Fresh invocation, no in-progress run | Ask mode (fine-tune or benchmark-only), start at stage 1 |
| Dataset exported, interrupted before stage 2 | Detect in-progress run, resume at fine-tune job submission |
| Fine-tune job running, user closed terminal | Detect `finetune_job.status == "running"` with `job_id`, resume polling without re-upload |
| Any stage failed | Surface error from `summary.md`, ask whether to retry or abandon |
| All stages completed but `deployed_at == null` | Jump straight to the deploy decision gate |

## Open questions

None at design stage. Implementation plan will address:
- Exact section formatting for `summary.md` (table styles, which fields to surface)
- Whether to support `--dry-run` on `submit_finetune.py` for testing
- How aggressively to validate `run.json` schema on read (strict vs lenient)
