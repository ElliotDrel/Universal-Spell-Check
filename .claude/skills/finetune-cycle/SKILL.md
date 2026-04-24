---
name: finetune-cycle
description: Orchestrates the full fine-tune workflow for the Universal Spell Checker — export dataset → submit fine-tune job → benchmark → deploy. Also supports benchmark-only runs. Use this skill whenever the user says anything about fine-tuning, benchmarking models, exporting training data, deploying a new model, or running a fine-tune cycle. Invoke it immediately without asking the user to clarify mode first — the skill asks that question itself.
---

# Fine-Tune Cycle Skill

This skill drives two workflows: a full fine-tune run (export → fine-tune → benchmark → deploy) and a standalone benchmark. Both write results to a dated run folder. **File presence IS the state** — each step is idempotent, so resuming is the same as starting.

## Step 0 — Ask which mode

> "What do you want to do?
> 1. Full fine-tune run (export → fine-tune → benchmark → deploy)
> 2. Benchmark only (compare models against the existing dataset)"

---

## Mode 1: Full Fine-Tune Run

### Directory layout

```
fine_tune_runs/
  YYYY-MM-DD-HHMMSS/
    summary.md              # appended after each step
    train.jsonl
    validation.jsonl
    dataset_summary.json
    finetune_job.json
    benchmark.json
```

### Resume logic — run at the START of every Mode 1 session

Find the most recent folder under `fine_tune_runs/` (by name — folders are date-stamped). Inspect what files exist:

| State on disk | Action |
|---|---|
| No folder, or all steps complete and deploy done | Start a new run — `mkdir fine_tune_runs/<timestamp>`, create blank `summary.md` |
| `train.jsonl` missing | Go to Step 1 (export) |
| `finetune_job.json` missing | Go to Step 2 (submit) |
| `finetune_job.json` has `status: running` | Go to Step 2 (resume polling) |
| `benchmark.json` missing | Go to Step 3 (benchmark) |
| All files present, no "## 4. Decision" in `summary.md` | Go to Step 4 (deploy gate) |

"All steps complete and deploy done" = `summary.md` contains `## 4. Decision`.

### Step 1 — Export dataset

Initialize `summary.md` in the run folder with a header before running the script:
```
# Fine-Tune Run <timestamp>
Base model: gpt-4.1-2025-04-14 · Started: YYYY-MM-DD HH:MM
```

**Run:**
```
python export_openai_finetune_dataset.py --run-dir <run-dir> --source logs
```

This writes `train.jsonl`, `validation.jsonl`, `dataset_summary.json`, and appends `## 1. Dataset Export` to `summary.md`. The `--source logs` flag ensures training data comes from runtime logs only — not from the frozen benchmark eval set.

**Pre-upload gate — show the user before proceeding to Step 2:**

Read `dataset_summary.json` and `train.jsonl`, then show:
1. Total example count + per-bucket distribution (from `dataset_summary.json`)
2. 3 random pairs from `train.jsonl` for an eyeball quality check (pick 3 at random, show `input` and `output` fields)
3. Rough cost estimate: `total_chars_in_train / 4 * 0.000025` (dollars) — compute this from the actual file size

Then ask: **"Upload and start fine-tune? (yes/no)"**

- If no: tell the user they can delete the run folder to abandon, or fix the data and rerun the skill.
- If yes: proceed to Step 2.

### Step 2 — Submit and poll fine-tune job

**Run:**
```
python .claude/skills/finetune-cycle/scripts/submit_finetune.py --run-dir <run-dir>
```

This uploads the files, creates the job, polls every 60s until complete, and appends `## 2. Fine-Tune Job` to `summary.md`. It may take 30–90 minutes. Print status to the user as the script logs it.

On success: the `fine_tuned_model` field in `finetune_job.json` has the new model ID.
On failure: the `error` field explains why. Surface the error to the user and stop.

### Step 3 — Benchmark

Discover the currently deployed model by reading `Universal Spell Checker.ahk`:
```
grep 'modelModule :=' "Universal Spell Checker.ahk"
```
Extract the value between the quotes.

**Run:**
```
python benchmark_spellcheck_models.py \
  --run-dir <run-dir> \
  --models <fine_tuned_model> <current_deployed_model> gpt-4.1
```

This writes `benchmark.json` and appends `## 3. Benchmark` to `summary.md`. Wait for it to finish — it runs live API calls.

### Step 4 — Deploy gate

Read `benchmark.json` and present a side-by-side comparison table:

| Model | Exact Match % | Median API ms |
|---|---|---|
| ft:... (new) | … | … |
| gpt-4.1 (current) | … | … |
| gpt-4.1 (baseline) | … | … |

Also show per-bucket accuracy if available.

Ask: **"Deploy the new model? (yes/no)"**

- If no: append `## 4. Decision\nNot deployed.` to `summary.md` and stop.
- If yes: proceed to Step 5.

### Step 5 — Deploy

1. Read `modelModule := "<value>"` from `Universal Spell Checker.ahk`
2. Replace the value with `fine_tuned_model` from `finetune_job.json`
3. Read `scriptVersion := "<N>"` and increment by 1
4. Append to `summary.md`:
   ```
   ## 4. Decision
   Deployed at HH:MM
   modelModule: <old> → <new>
   scriptVersion: <old> → <new>
   ```
5. Tell the user: "Deployed. Review the diff and commit when ready."

Do NOT run git commit or push.

---

## Mode 2: Benchmark Only

```
benchmark_runs/
  YYYY-MM-DD-HHMMSS/
    summary.md
    benchmark.json
```

1. Ask which models to compare. Defaults: current deployed model (from `modelModule` in the AHK file) + `gpt-4.1`. User can add extras.
2. Create the run folder: `mkdir benchmark_runs/<timestamp>`
3. Run:
   ```
   python benchmark_spellcheck_models.py \
     --run-dir benchmark_runs/<timestamp> \
     --models <model1> <model2> ...
   ```
4. Read `benchmark.json` and present the comparison table (same format as Mode 1 Step 4).
5. Write `summary.md` with the results (the script appends `## 3. Benchmark`; add a header `# Benchmark Run <timestamp>` at the top if `summary.md` is new).

---

## Reference

See `references/openai_finetune_api.md` for OpenAI fine-tuning API details, status codes, and error patterns — load it if you need to interpret `finetune_job.json` or troubleshoot a failed job.
