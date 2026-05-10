---
name: autoopt
description: Autonomous Karpathy-style optimization loop for Universal Spell Check. Runs N iterations in an isolated git worktree on a dedicated branch, each iteration proposing one src/ change, building, running a low-call speed bench, verifying correctness, and committing or reverting based on confirmed request_ms improvement. Use when the user wants to autonomously optimize app speed, run autoopt, run an optimization loop, or improve performance over many iterations.
---

# AutoOpt - Autonomous Speed Optimization Loop

This skill drives an end-to-end autonomous optimization run. State persists to `bench/autoopt/journal.jsonl` for cross-session resume and post-hoc review.

## Step 0 - Parse iteration count

Default `N = 50`. If the user specified a number, use that.

## Step 1 - Setup

Run from the repo root.

1. **Verify clean tree.** `git status --porcelain` must be empty. If not, stop and tell the user to commit/stash first.
2. **Create worktree.** Generate a UTC timestamp `YYYYMMDD-HHMMSS`. Run:
   ```text
   git worktree add -b autoopt/{timestamp} ../universal-spell-check-autoopt main
   ```
   All subsequent work happens inside that worktree path.
3. **Build baseline.** `dotnet build -c Release src/UniversalSpellCheck.csproj`. Abort if it fails.
4. **Run screen baseline.** `dotnet run --project bench/UniversalSpellCheck.Bench.csproj -c Release -- --runs 3 --warmup 1 --variant baseline-autoopt-screen`. Note the output path.
5. **Verify screen baseline correctness.** `python bench/check_correctness.py <screen-baseline.json>`. If it fails, abort.
6. **Run confirmation baseline.** `dotnet run --project bench/UniversalSpellCheck.Bench.csproj -c Release -- --runs 10 --warmup 2 --variant baseline-autoopt-confirm`. Note the output path.
7. **Verify confirmation baseline correctness.** `python bench/check_correctness.py <confirmation-baseline.json>`. If it fails, abort.
8. **Init state files.**
   - `bench/autoopt/baseline.json` -> write the screen baseline result file path.
   - `bench/autoopt/confirmation-baseline.json` -> write the confirmation baseline result file path.
   - `bench/autoopt/journal.jsonl` -> create empty.
9. **API budget rule.** The bench calls the API once per input per measured or warmup trial: `20 * (runs + warmup)`. Default screen cost is 80 calls. Do not raise runs unless the user explicitly asks.
10. **Read `bench/autoopt/program.md`.** This is your law for the rest of the run.

## Step 2 - Iteration loop

For each iteration, follow these steps in order.

### 2.1 Propose

- Read `bench/autoopt/program.md`.
- Read the last 10 entries of `journal.jsonl` to avoid dead ends.
- Read current `bench/autoopt/baseline.json`.
- Pick one change. State the hypothesis in 1-2 sentences and identify files to edit.

### 2.2 Apply

- Edit only the files needed for the single hypothesis.

### 2.3 Build

- `dotnet build -c Release src/UniversalSpellCheck.csproj`
- On failure: revert only the iteration code changes, append journal entry `{iter, hypothesis, result: "build_fail", error: "<short>"}`, continue.

### 2.4 Screen bench

- `dotnet run --project bench/UniversalSpellCheck.Bench.csproj -c Release -- --runs 3 --warmup 1 --variant autoopt-{i}-screen`
- If any input has `success_count < 3` or the run errors: revert only the iteration code changes, journal `{iter, hypothesis, result: "bench_fail", error}`, continue.

### 2.5 Correctness gate

- `python bench/check_correctness.py <screen-results.json>`
- On non-zero exit: revert only the iteration code changes, journal `{iter, hypothesis, result: "correctness_fail", details}`, continue.

### 2.6 Screen compare

- `python bench/compare.py <baseline.json> <screen-results.json>`
- If screen median `request_ms` improves by less than 8%, revert and journal `{iter, hypothesis, result: "reverted", delta, reason: "screen_below_threshold"}`.

### 2.7 Confirmation

Only for screen improvements of at least 8%:

- `dotnet run --project bench/UniversalSpellCheck.Bench.csproj -c Release -- --runs 10 --warmup 2 --variant autoopt-{i}-confirm`
- `python bench/check_correctness.py <confirm-results.json>`
- `python bench/compare.py <confirmation-baseline.json> <confirm-results.json>`

If confirmed median `request_ms` improves by at least 5%:

- `git add -A && git commit -m "autoopt #{i}: {hypothesis} (+X.X%)"`
- Update `bench/autoopt/baseline.json` to the screen result path.
- Update `bench/autoopt/confirmation-baseline.json` to the confirmation result path.
- Journal `{iter, hypothesis, result: "kept", delta, files_changed}`.

Otherwise:

- Revert only the iteration code changes.
- Journal `{iter, hypothesis, result: "reverted", delta, reason: "confirmation_below_threshold"}`.

### 2.8 Stuck check

If the last 5 journal entries are all reverted/failed, write `{iter, result: "stuck", summary}` and try a different category from the search-space hints.

## Step 3 - Finish

Print a summary:

- Total kept commits, total reverted, total build/bench/correctness failures.
- Cumulative speedup vs original confirmation baseline.
- Top 3 individual wins by delta with commit messages.
- Worktree path and branch name.

Tell the user:

- Worktree: `../universal-spell-check-autoopt`
- Branch: `autoopt/{timestamp}`
- Next step: review the git log, cherry-pick or merge wins, then remove the worktree.

## Discipline notes

- Do not optimize the bench harness itself.
- Do not change `correctness.json` or `inputs.json` mid-run.
- Do not skip the correctness gate.
- Do not batch multiple changes in one iteration.
- Do not raise bench runs casually. API budget is part of the optimization target.
