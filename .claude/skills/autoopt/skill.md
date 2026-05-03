---
name: autoopt
description: Autonomous Karpathy-style optimization loop for Universal Spell Check. Runs N iterations in an isolated git worktree on a dedicated branch, each iteration proposing one src/ change, building, running the speed bench, verifying correctness, and committing or reverting based on a ≥5% median request_ms improvement threshold. Use when the user wants to autonomously optimize app speed, run autoopt, run an optimization loop, or improve performance over many iterations.
---

# AutoOpt — Autonomous Speed Optimization Loop

This skill drives an end-to-end autonomous optimization run. The full loop runs in a single conversation turn — accumulating context across iterations is the point (the agent learns from dead ends). State persists to `bench/autoopt/journal.jsonl` for cross-session resume and post-hoc review.

## Step 0 — Parse iteration count

Default `N = 50`. If user specified a number (e.g., `/autoopt 25`), use that.

## Step 1 — Setup (once)

Run from the repo root.

1. **Verify clean tree.** `git status --porcelain` must be empty. If not, stop and tell the user to commit/stash first.
2. **Create worktree.** Generate a UTC timestamp `YYYYMMDD-HHMMSS`. Run:
   ```
   git worktree add -b autoopt/{timestamp} ../universal-spell-check-autoopt main
   ```
   All subsequent work happens inside that worktree path.
3. **Build baseline.** `dotnet build -c Release src/UniversalSpellCheck.csproj`. Abort if it fails.
4. **Run baseline bench.** `dotnet run --project bench/UniversalSpellCheck.Bench.csproj -c Release -- --runs 20 --warmup 3 --variant baseline-autoopt`. Note the output path printed.
5. **Verify baseline correctness.** `python bench/check_correctness.py <baseline.json>`. If it fails, abort — baseline is broken; do not proceed.
6. **Init state files.**
   - `bench/autoopt/baseline.json` → write the baseline result file path.
   - `bench/autoopt/journal.jsonl` → create empty.
7. **Read `bench/autoopt/program.md`.** This is your law for the rest of the run.

## Step 2 — Iteration loop (i = 1 to N)

For each iteration, follow these steps in order. Treat each step as a TaskCreate task so progress is visible.

### 2.1 Propose
- Read program.md (constraints/goal/hints).
- Read last 10 entries of `journal.jsonl` to avoid dead ends.
- Read current baseline metric.
- Pick ONE change. State hypothesis in 1–2 sentences. Identify the file(s) to edit.

### 2.2 Apply
- Edit the file(s). Single concern per iteration.

### 2.3 Build
- `dotnet build -c Release src/UniversalSpellCheck.csproj`
- On failure: `git checkout -- .`, append journal entry `{iter, hypothesis, result: "build_fail", error: "<short>"}`, continue to next iteration.

### 2.4 Bench
- `dotnet run --project bench/UniversalSpellCheck.Bench.csproj -c Release -- --runs 20 --warmup 3 --variant autoopt-{i}`
- Note output JSON path.
- If any input has `success_count < 20` or run errored: `git checkout -- .`, journal `{iter, hypothesis, result: "bench_fail", error}`, continue.

### 2.5 Correctness gate
- `python bench/check_correctness.py <new-results.json>`
- On non-zero exit: `git checkout -- .`, journal `{iter, hypothesis, result: "correctness_fail", details}`, continue.

### 2.6 Compare
- `python bench/compare.py <baseline.json> <new-results.json>`
- Parse the median `request_ms` delta from the output.

### 2.7 Decide
- If delta ≥ +5% improvement:
  - `git add -A && git commit -m "autoopt #{i}: {hypothesis} ({+X.X%})"`
  - Update `bench/autoopt/baseline.json` to point at the new results file.
  - Journal `{iter, hypothesis, result: "kept", delta, files_changed}`.
- Otherwise:
  - `git checkout -- .`
  - Journal `{iter, hypothesis, result: "reverted", delta}`.

### 2.8 Stuck check
- If last 5 journal entries are all reverted/failed, write a `{iter, result: "stuck", summary}` entry and explicitly reconsider strategy in your next proposal (try a different category from the search-space hints).

## Step 3 — Finish

After N iterations or all 50 budget exhausted, print a summary:

- Total kept commits, total reverted, total build/bench/correctness failures.
- Cumulative speedup vs original baseline (current_baseline_median / original_baseline_median).
- Top 3 individual wins by delta with their commit messages.
- Path to the worktree and branch name so the user can review and merge.

Tell the user:
- Worktree: `../universal-spell-check-autoopt`
- Branch: `autoopt/{timestamp}`
- Next step (theirs): review the git log, cherry-pick or merge the wins, then `git worktree remove ../universal-spell-check-autoopt`.

## Discipline notes

- **Don't optimize the bench harness itself** — `bench/**` is off-limits in program.md.
- **Don't change `correctness.json` or `inputs.json`** mid-run — that invalidates comparisons.
- **Don't skip the correctness gate** to "save time" — it's the only thing catching silent regressions.
- **Don't batch multiple changes** in one iteration — single-variable isolation is the entire premise.
- **Don't trust the baseline drift** — when a change is kept, the baseline updates to the NEW result. Subsequent comparisons are vs the latest kept baseline, not the original.
