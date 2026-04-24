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

## User-facing flow

User invokes the `finetune-cycle` skill. The agent walks through six stages, pausing at decision gates:

| # | Stage | Decision gate |
|---|-------|---------------|
| 1 | Create run folder + benchmark baseline model | Show baseline scores → continue? |
| 2 | Export fine-tune dataset from logs | Show example count + bucket distribution → upload? |
| 3 | Submit fine-tune job to OpenAI + poll until complete | Automatic (may take 30+ min) |
| 4 | Benchmark the fine-tuned model | Automatic |
| 5 | Write before/after comparison to summary.md | Show delta → deploy? |
| 6 | If deploying: update AHK `modelModule`, bump `scriptVersion` | Agent edits file; user commits manually |

## Directory structure

All run artifacts live in one dated folder. No more splitting across `benchmark_data/`, `fine_tune_data/`, and `logs/benchmarks/` for a given run.

```
fine_tune_runs/
  2026-04-23-143000/              # one folder per run, timestamp = run start
    summary.md                    # THE user-facing doc — appended at each stage
    run.json                      # state machine, machine-readable
    pre_benchmark.json            # raw benchmark results (pre)
    train.jsonl                   # fine-tune training data
    validation.jsonl              # fine-tune validation data
    dataset_summary.json          # export metadata
    finetune_job.json             # OpenAI job_id, status, ft: model_id
    post_benchmark.json           # raw benchmark results (post)
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
  "started_at": "2026-04-23 14:30:00",
  "completed_at": null,
  "deployed_at": null,
  "fine_tuned_model": null,
  "stages": {
    "pre_benchmark":  { "status": "pending|running|completed|failed", "started_at": null, "completed_at": null },
    "export_dataset": { "status": "...", "started_at": null, "completed_at": null },
    "finetune_job":   { "status": "...", "started_at": null, "completed_at": null, "job_id": null },
    "post_benchmark": { "status": "...", "started_at": null, "completed_at": null },
    "comparison":     { "status": "...", "started_at": null, "completed_at": null }
  }
}
```

Stage status transitions: `pending → running → completed` (happy path) or `running → failed` (error path).

A run is "in progress" if any stage is `running` or if all stages are `completed` but `deployed_at` is null. On skill invocation, the first thing checked is whether an in-progress run exists.

## `summary.md` structure

The user's one document for the run. Sections appear as stages complete:

```markdown
# Fine-Tune Run 2026-04-23-143000
Base model: gpt-4.1 · Started: 2026-04-23 14:30

## 1. Pre-Benchmark (completed 14:32)
Exact match rate: 67.3% · Median API: 420ms
[bucket matrix table]

## 2. Dataset Export (completed 14:33)
120 examples (104 train / 16 val) · 151 excluded as duplicates of prior runs

## 3. Fine-Tune Job (completed 15:08)
Job ID: ftjob-abc123 · Model: ft:gpt-4.1-2025-04-14:...
Training duration: 34 min · Final loss: 0.42

## 4. Post-Benchmark (completed 15:10)
Exact match rate: 71.8% (+4.5) · Median API: 410ms (-10)
[bucket matrix table]

## 5. Comparison & Decision
[side-by-side bucket matrix diff]
Decision: deployed at 15:12

## 6. Deploy
Updated modelModule in Universal Spell Checker.ahk:
  gpt-4.1 → ft:gpt-4.1-2025-04-14:...
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

**`benchmark_spellcheck_models.py`:** add `--run-dir <path>` and `--stage <pre_benchmark|post_benchmark>`. When set:
- Output file is `<run-dir>/<stage>.json` (instead of timestamped dir under `logs/benchmarks/`)
- Appends the corresponding section to `<run-dir>/summary.md`
- Updates `<run-dir>/run.json` stage status
- Old `--output-dir` path still works for ad-hoc runs

**`export_openai_finetune_dataset.py`:** add `--run-dir <path>`. When set:
- Writes `train.jsonl` / `validation.jsonl` / `dataset_summary.json` into run folder
- Appends dataset section to `summary.md`
- Updates `run.json` stage status
- Deduplication scans `fine_tune_runs/*/train.jsonl` AND `fine_tune_runs/*/validation.jsonl` for previously-used pairs
- Old `--output-dir` path still works

## Deploy step

After user approves at stage 5, the agent:
1. Reads current `modelModule` from `Universal Spell Checker.ahk`
2. Replaces it with the `ft:` model ID from `run.json.fine_tuned_model`
3. Reads `scriptVersion` and bumps the minor version
4. Writes the deploy section to `summary.md`
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
| Fresh invocation, no in-progress run | Start a new run at stage 1 |
| Pre-benchmark succeeded, user interrupted before stage 2 | Detect in-progress run, resume at stage 2 |
| Fine-tune job running, user closed terminal | Detect `finetune_job.status == "running"` with `job_id`, resume polling |
| Any stage failed (status == "failed") | Surface error from `summary.md`, ask user whether to retry the stage or abandon the run |
| All stages completed but `deployed_at == null` | Jump to the deploy decision gate |

## Open questions

None at design stage. Implementation plan will address:
- Exact section formatting for `summary.md` (table styles, which fields to surface)
- Whether to support `--dry-run` on `submit_finetune.py` for testing
- How aggressively to validate `run.json` schema on read (strict vs lenient)
