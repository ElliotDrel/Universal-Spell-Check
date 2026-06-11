# AutoOpt - Autonomous Speed Optimization Loop

Karpathy-style auto-research loop for the speed bench. Runs N iterations in an isolated git worktree, each iteration proposing one `src/` change, building, running the headless bench, verifying correctness, and committing or reverting based on deterministic metrics.

## Invocation

```text
/autoopt 50
```

Default N = 50. Drives the full loop in one conversation turn. Accumulating context across iterations is the point: the agent learns from dead ends.

## Files

| File | Purpose |
|---|---|
| `.agents/skills/autoopt/skill.md` | The skill body with step-by-step loop instructions. |
| `bench/autoopt/program.md` | Constraints, goal, off-limits, API budget, behavioral contracts. Read each iteration. |
| `bench/autoopt/journal.jsonl` | One line per iteration: hypothesis, files, build/bench/correctness/delta result. Created at run start, persists for resume and review. |
| `bench/autoopt/baseline.json` | Path to current 80-call screen baseline result. Updated when a change is kept. |
| `bench/autoopt/confirmation-baseline.json` | Path to current 240-call confirmation baseline result. Updated when a change is kept. |
| `bench/check_correctness.py` | Deterministic correctness gate, exits 0 / 1. |

## API Call Budget

The bench makes one OpenAI API call per input per measured or warmup trial:

```text
calls = 20 inputs * (runs + warmup)
```

The old `--runs 20 --warmup 3` setting cost **460 API calls per bench**, which is too expensive for exploratory autoopt.

The new default is a two-tier loop:

| Bench | Command flags | Calls | When used |
|---|---:|---:|---|
| Screen | `--runs 3 --warmup 1` | 80 | Baseline and every iteration. |
| Confirm | `--runs 10 --warmup 2` | 240 | Only after the screen result shows a likely win. |

Setup costs **320 calls**: 80 for the screen baseline and 240 for the confirmation baseline. A normal rejected iteration costs **80 calls**, not 460.

## Loop

1. **Propose** one change, informed by recent journal entries to avoid dead ends.
2. **Apply** the edit.
3. **Build** Release. On failure: revert, log `build_fail`, continue.
4. **Screen bench** with `--runs 3 --warmup 1 --variant autoopt-{i}-screen`. On any failed trial: revert, log `bench_fail`, continue.
5. **Correctness gate** via `check_correctness.py`. On failure: revert, log `correctness_fail`, continue.
6. **Compare** screen median `request_ms` vs `baseline.json`.
7. **Decide**: if screen improvement is less than 8%, revert. If screen improvement is at least 8%, run confirmation.
8. **Confirm promising changes** with `--runs 10 --warmup 2 --variant autoopt-{i}-confirm`, compare against `confirmation-baseline.json`, and keep only if confirmed median `request_ms` improves by at least 5%.
9. **Stuck check**: 5 consecutive reverts/failures means write a `stuck` journal entry and reconsider strategy.

## Goal & Metric

Reduce **median `request_ms`** by at least **5%** per kept change vs the current confirmation baseline. Tiebreaker: p95 `request_ms`.

The screen threshold is intentionally higher, at **8%**, because the 80-call screen bench is noisier. The screen bench decides what is worth confirming; the confirmation bench decides what is worth committing.

When a change is kept, both baselines update to the new result. Subsequent comparisons are vs the latest kept baseline, not the original.

## Behavioral Contracts

Enforced deterministically by `check_correctness.py`:

1. **Protected literal passthrough**: URLs, UUIDs/session IDs, API keys, file paths, and opaque IDs in input must appear byte-identical in output.
2. **Brand replacements**: canonical variants from `replacements.json` must be applied.
3. **Prompt-leak guard**: `instructions:` and `text input:` echoes must be stripped before paste.
4. **Channel separation**: Prod/Dev never collide on hotkey, mutex, or app-data folder; values come from `BuildChannel.cs`.
5. **Update flow**: all paths go through `UpdateService.CheckAsync`.

The agent may rewrite the implementation of these contracts as long as the contracts still hold.

## Off-Limits

- `src/BuildChannel.cs` - channel identity.
- `bench/inputs.json` and bench harness code - changing them invalidates measurement.
- `src/UpdateService.cs`, `.github/workflows/release.yml` - release pipeline.
- `replacements.json` - fixture data.

Autoopt state files under `bench/autoopt/*.json` and `bench/autoopt/*.jsonl` are writable by the loop.

## Isolation

Runs in a git worktree at `../universal-spell-check-autoopt` on a fresh branch `autoopt/{utc-timestamp}`. Each kept improvement is its own commit with message `autoopt #{i}: {hypothesis} (+X.X%)`. After the run, review the git log and cherry-pick or merge the wins, then remove the worktree.

## Correctness Gate

`check_correctness.py` derives contracts automatically from `bench/inputs.json` + `replacements.json` — no calibration file, no recalibration needed. Contracts are:

- **Protected literal passthrough**: URLs, UUIDs/session IDs, API keys, file paths, and opaque IDs in the original input must appear byte-identical in `sample_output`.
- **Brand replacements**: every `replacements.json` variant present outside protected literals must appear as its canonical form in `sample_output`.
- Inputs with neither protected literals nor matched variants are silently skipped.

Because contracts are derived from source-of-truth files that are off-limits to the autoopt loop, the gate is always valid.
