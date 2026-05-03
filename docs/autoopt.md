# AutoOpt — Autonomous Speed Optimization Loop

Karpathy-style auto-research loop for the speed bench. Runs N iterations in an isolated git worktree, each iteration proposing one `src/` change, building, running the headless bench, verifying correctness, and committing or reverting based on a deterministic metric.

## Invocation

```
/autoopt 50
```

Default N = 50. Drives the full loop in one conversation turn — accumulating context across iterations is the point (the agent learns from dead ends).

## Files

| File | Purpose |
|---|---|
| `.claude/skills/autoopt/skill.md` | The skill body — full step-by-step loop instructions |
| `bench/autoopt/program.md` | Constraints, goal, off-limits, behavioral contracts (read each iteration) |
| `bench/autoopt/journal.jsonl` | One line per iteration: hypothesis, files, build/bench/correctness/delta result. Created at run start, persists for cross-session resume and post-hoc review. |
| `bench/autoopt/baseline.json` | Path to current baseline bench result (updated when a change is kept) |
| `bench/correctness.json` | 20 hand-written assertions per input (see `docs/bench.md`) |
| `bench/check_correctness.py` | Deterministic correctness gate, exits 0 / 1 |

## Loop (per iteration)

1. **Propose** one change (1–2 sentence hypothesis), informed by recent journal entries to avoid dead ends.
2. **Apply** the edit.
3. **Build** Release. On failure → revert, log `build_fail`, continue.
4. **Bench** with `--runs 20 --warmup 3 --variant autoopt-{i}`. On any failed trial → revert, log `bench_fail`, continue.
5. **Correctness gate** via `check_correctness.py`. On failure → revert, log `correctness_fail` with which assertion, continue.
6. **Compare** median `request_ms` vs current baseline.
7. **Decide**: ≥+5% improvement → commit and update baseline. Otherwise → revert.
8. **Stuck check**: 5 consecutive reverts → write `stuck` journal entry and reconsider strategy in the next proposal.

## Goal & metric

Reduce **median `request_ms`** (headless bench) by **≥5%** per kept change vs current baseline. Tiebreaker: p95 `request_ms`. Threshold is enforced by `bench/compare.py` (see `docs/bench.md`).

When a change is kept, the baseline updates to the NEW result — subsequent comparisons are vs the latest kept baseline, not the original. Cumulative speedup = `original_baseline_median / current_baseline_median`.

## Behavioral contracts (preserved every iteration)

Enforced deterministically by `check_correctness.py`:

1. **URL protection** — `https?://...` URLs in input must appear byte-identical in output.
2. **Brand replacements** — canonical→variants pairs from `replacements.json` must be applied.
3. **Prompt-leak guard** — `instructions:` and `text input:` echoes stripped before paste.
4. **Channel separation** — Prod/Dev never collide on hotkey/mutex/app-data folder; values from `BuildChannel.cs`.
5. **Update flow** — all paths through `UpdateService.CheckAsync`.

The agent may rewrite the *implementation* of any of these (e.g., refactor `TextPostProcessor`) as long as the contract holds.

## Off-limits

- `src/BuildChannel.cs` — channel identity
- `bench/**` — invalidates measurements (includes `correctness.json`, `inputs.json`)
- `src/UpdateService.cs`, `.github/workflows/release.yml` — release pipeline
- `replacements.json` — fixture data (the agent may change *how* replacements are applied, not which ones)

## Isolation

Runs in a git worktree at `../universal-spell-check-autoopt` on a fresh branch `autoopt/{utc-timestamp}`. Each kept improvement is its own commit with message `autoopt #{i}: {hypothesis} (+X.X%)`. After the run, review the git log and cherry-pick or merge the wins, then `git worktree remove ../universal-spell-check-autoopt`.

## Calibrating correctness.json

If model behavior drifts or `inputs.json` changes, the baseline correctness check may fail. Recalibration:

1. Run a 1-trial bench: `dotnet run --project bench/UniversalSpellCheck.Bench.csproj -c Release -- --runs 1 --warmup 0 --variant calibrate`
2. Inspect `sample_output` per input in the result JSON.
3. Update `bench/correctness.json` so all assertions pass against the new baseline outputs.
4. Run `python bench/check_correctness.py <result>.json` until it returns PASS.

The goal of the gate is detecting *regressions from the current baseline*, not enforcing absolute correctness — the assertions are calibrated, not aspirational.
