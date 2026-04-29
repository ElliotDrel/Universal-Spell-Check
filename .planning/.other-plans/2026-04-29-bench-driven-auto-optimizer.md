# Bench-Driven Auto-Optimizer — Sketch

> **Status:** Ideas-only outline. Not ready to execute. Depends on plan #1 (`2026-04-29-deterministic-e2e-speed-bench.md`) being implemented first.

## Goal

Autoresearch-style loop that automatically proposes, applies, measures, and accepts-or-reverts speed optimizations to the spellcheck pipeline. Uses plan #1's bench harness as a black-box "did this make it faster?" oracle.

## Core loop

```
1. Read current baseline.json from bench/results/
2. Pick next candidate optimization from a backlog
3. Apply the change (edit src/ files in a worktree)
4. Build → bench/.../variant.json
5. Compare baseline vs variant
6. If median delta on `total_ms` improves >X% with no regression on success_count:
     → keep change, promote variant.json to new baseline, commit
   Else:
     → revert change, log "rejected: <reason>"
7. Goto 2
```

## Where the candidates come from

Two sources:
- **Static backlog** — pre-curated list of known-promising tweaks (HTTP/2 vs HTTP/1.1, prompt-caching headers, smaller system prompt, streaming response parse, paste-via-WM_PASTE-instead-of-SendKeys, prefetch DNS, swap `JsonDocument.Parse` for `Utf8JsonReader`, etc.). Each entry has a one-paragraph description and a target file. Easiest to start with.
- **Agent-proposed** — Claude reads recent bench results + the hot-path code, proposes the next change. Higher upside, higher risk of broken builds or correctness regressions.

Start with the static backlog. Layer in agent proposals later if the backlog runs dry.

## Hard constraints (not optional)

- **Correctness gate before speed gate.** A variant only competes on speed if its outputs still match a reference set. Need a small "does the corrected text look right" check — likely a textual diff against the baseline run's outputs, with tolerance for non-meaningful whitespace/punctuation differences. If the variant changes what gets returned, it's rejected even if it's faster.
- **Worktree isolation.** Each candidate runs in a git worktree so a broken change can't poison main.
- **Build gate.** A variant that fails `dotnet build` is auto-rejected with the build error logged.
- **Statistical gate.** Don't accept <5% improvements as real — that's inside the noise floor of plan #1's harness.
- **Human review before merge.** The loop never pushes to remote and never deletes unrelated branches. It commits to its worktree's branch; a human merges.

## Open decisions (defer until execution)

1. **Single-file vs multi-file changes per iteration?** Single-file is easier to attribute deltas to a specific change and easier to revert. Multi-file allows compound optimizations (HTTP handler tweak + payload tweak together) but makes attribution noisy. Lean single-file.
2. **Reference output set for the correctness gate.** Reuse the bench's 10 inputs? Run the baseline once with `--store-outputs` and snapshot for comparison? How strict is the diff (exact match, normalized whitespace, semantic-equivalence via a second LLM call)?
3. **Where the loop runs.** `/loop` skill, scheduled cron via `/schedule`, or a dedicated long-running console app? `/loop` is simplest.
4. **What "best" means.** Optimize median `total_ms`? p95? Some weighted combo? p95 matters more for user experience but median is more stable to optimize against.
5. **Cost ceiling.** Each loop iteration = N bench runs = ~$X of API spend. Need a session budget cap and an "abort if no improvement after K iterations" stop condition.
6. **What happens on regression.** Hard revert (drop the worktree) vs soft (keep branch, mark rejected, move on)? Soft preserves audit trail.
7. **Optimization scope.** Whole `src/`? Just the hot path (`SpellcheckCoordinator`, `OpenAiSpellcheckService`, `TextPostProcessor`)? Whitelist explicitly so the loop can't touch UI/dashboard/update code.

## What this is NOT

- Not a CI system. Not a benchmark dashboard. Not a fine-tune training loop.
- Not a general-purpose code optimizer. Scoped exclusively to the spellcheck hot path.
- Not autonomous merging or pushing. The loop produces candidate commits; humans review and merge.
- Not a replacement for plan #1's manual workflow — it's a layer on top.

## Rough task shape (for the eventual plan)

1. Reference-output capture mode in the bench (`--store-outputs`)
2. Correctness diff utility (Python or C#)
3. Static backlog as a YAML/JSON file with N candidate entries
4. Loop driver script: pick candidate → apply → build → bench → compare → keep/revert
5. Audit log of every iteration's outcome
6. Session budget + stop conditions
7. (Later) Agent-proposed candidates via Claude API call between iterations

## Pre-flight checklist before starting plan #2

- [ ] Plan #1 is fully implemented and the bench produces stable baselines (variance <5% across consecutive runs)
- [ ] At least 5-10 manual optimization rounds have been done with plan #1's tools so we have a feel for what kinds of changes actually move the needle (informs the static backlog)
- [ ] Decisions above are made
- [ ] OpenAI cost budget for a multi-hour autonomous loop is approved
