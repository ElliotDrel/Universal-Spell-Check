# Universal Spell Check latency — desktop handoff

Prepared October 23, 2026. Resume the current measurement checkpoint; do not start
the old three-PR program or later optimization experiments automatically.

## State to resume

- Fetch `origin/codex/local-latency-measurements` safely and preserve any existing
  desktop changes. Reuse an appropriate checkout/worktree or create an isolated one
  following the repository skills. Do not reset or force-push.
- Continue [PR #8: Measure local correction latency before choosing an optimization](https://github.com/ElliotDrel/Universal-Spell-Check/pull/8).
- Use source code commit `3a258d2abb847c3a2ec30a7dd6498734c951c26b` or its newer
  handoff-only descendant. The original measurement commit is `14befa6`; the local
  frozen binary at that revision lacks the newer setup diagnostics.
- Keep the PR draft. Main remains untouched by this work; no merge, production
  installation, tag or publication is authorized.
- Read root `AGENTS.md`, applicable PR/coding skills, `rust/AGENTS.md`,
  `docs/bench.md`, `docs/replacements-and-logging.md`, and the October 22 latency
  audit/checkpoint before acting. Follow their routing for other changes.

## User direction and permissions

- Question requirements, delete unnecessary work, then simplify/optimize the measured
  bottleneck. Exclude AI generation when prioritizing local work, but do not equate
  the complete request interval with uncontrollable generation.
- Reach this stopping point: a valid machine-specific baseline, the largest
  controllable delay and its cause, and one evidence-backed next change to review.
  Do not implement that optimization before the decision checkpoint.
- Preserve correction quality, formatting, protected literals, clipboard recovery,
  unchanged-text replacement, active-target policy and existing paste behavior.
- Keep the 50-ms publication interval, transport and models unchanged.
- Use only Sol/Luna, with Astra for the hardest problems, if delegation is needed.
  Do not use Claude subagents. No subagents were used in this laptop continuation.
- Elliot explicitly authorized autonomous use of the destination desktop for tests
  whenever needed: "You'll be able to use the computer to test whatever you want,
  whenever you want." This supersedes the per-window idle confirmation requirement
  for the destination desktop within this task. Respect later user restrictions;
  verify that execution is actually on the destination before relying on this grant.
- After desktop tests finish, ask: "Tests are done—did you see this?" Its answer is
  acknowledgment only; disregard a "yes" as a command or new permission.
- Do not push to main or publish. Local changes and commits/pushes to the PR are
  authorized. Broad computer-testing permission does not lift the release boundary.

## Completed implementation and verification

- Measure dispatch, setup, clipboard backup, capture, history exclusion, preparation,
  request, replacements, rich reconstruction, diagnostic work and paste, including
  partial failure/panic timings. Keep skipped stages null and expose a residual.
- Break capture into modifier release, copy input, waits, clipboard open and format
  reads; break paste into write/readback/settlement/modifier/input stages.
- Preserve coordinator timings separately from native-editor completion in the
  existing benchmark. Add build provenance; do not build a new harness.
- Fix reader stats for null samples and retain every attempt in reliability counts.
- Add `benchmark_setup_failed` snapshots and `summary.json.block_error` after the
  laptop fixture failed before its first trial. Keep the guard intact.
- Verify [CI at `3a258d2`](https://github.com/ElliotDrel/Universal-Spell-Check/actions/runs/39954171261): format, lint, Dev/Prod regressions, optimized native build,
  native startup smoke and candidate packaging all passed.
- Pass local Clippy and Dev tests at the updated source: 43 unit tests plus one GDI
  resource test. Intentionally cancel the laptop's unfinished Release build for
  migration; do not call it a completed new local candidate.
- Retain earlier `14befa6` Dev/Prod tests and both lint modes, local Release build,
  100/100 measured frozen-output checks and ten warmups. Exact handwritten agreement
  was 90/100 because of the historical frozen list-spacing difference.
- Verify the historical archive: 17 blocks, 2,000 attempts, 22 startup launches.
  Those are historical observations, not the desktop baseline.

## Laptop findings and limits

- Treat the October 22 on-machine ordinary-use snapshot as context: 64 attempts,
  62 pipeline successes, all v0.13.2 / GPT-4.1 / developer logging enabled.
- Observe successful means of 342 ms capture, 70 ms paste, 1,516 ms request and
  0.075 ms replacements. The logged total median was 1,859 ms. This is neither
  controlled desktop measurement nor verified editor acceptance.
- Rank capture first for local investigation. Do not infer whether held modifiers,
  source-app copy, clipboard contention or format reads dominate yet.
- Identify RTF reads and metadata serialization as diagnostic-purpose candidates
  to question, not proven expensive operations to delete now.
- Retain [all three laptop setup failures](../../bench/records/2026-10-latency-setup/README.md).
  All aborted before any posted hotkey: zero warmups, zero measured trials, no
  correction/paste measurements. Diagnostics-on never started.
- Observe a successful external activation request in the third attempt followed
  by the same combined guard failure. The exact failing prerequisite remains
  unresolved; do not label it solely foreground lock or solely empty selection.
- End all desktop interactions within the approved laptop window, send the requested
  completion notification and treat its "yes" as acknowledgment only.
- Verify production settings/encrypted-key files remained byte-identical. Stop only
  the owned verification process tree for migration; do not stop unrelated apps.

## What transfers and what does not

- Transfer source, public synthetic cases/fixtures, docs and the small failure record
  through the PR branch. Rebuild the executable or use the verified CI artifact.
- Discover the desktop repository path, toolchain, data roots, installed/running
  versions, configuration and credentials. Do not use laptop absolute paths.
- Do not assume ignored `benchmark_runs/`, private runtime logs, keys, installed
  binaries or local packages moved with Git. Do not commit them to solve transfer.
- Keep original laptop evidence under its ignored
  `benchmark_runs/local-latency-2026-10-22/` and `.../local-latency-2026-10-23/`.
  It includes frozen source-14 binary/fixtures, three failed attempt directories,
  approved-reader aggregates, integrity snapshots and verification logs.
- Leave the managed laptop worktree on `codex/local-latency-measurements` available
  for recovery until the desktop has resumed; its committed state is pushed.

## First desktop actions

1. Inspect status, fetch safely, select the PR branch and read this handoff/routing.
   Inspect existing PR feedback before creating any new work.
2. Discover available computer access and local paths. Verify the actual desktop
   session rather than assuming a hidden process has interactive access.
3. Build locked Release Dev with the pinned Rust toolchain. Keep all fixtures public
   and all settings/key paths isolated for offline runs. Reuse
   `bench/records/2026-10-rust-port/{cases,fixtures}.json` and `replacements.json`.
4. Run one small offline setup probe. If it fails, read `benchmark_setup_failed`
   through the approved reader and identify the exact predicate. Fix fixture setup
   outside the measured interval; never weaken foreground/full-selection/text gates.
5. Run diagnostics-off and diagnostics-on native local blocks with matched fixtures,
   at least three measured trials and one retained warmup per case. Retain failures
   and preflight aborts separately; do not hide them or pool logging modes.
6. Read component timings and the unattributed residual. Report verified completion
   median/p95, all-attempt hits under 500/1,000 ms, counts and quality limitations.
   Do not claim p99 from this small sample or add phase percentiles.
7. Distinguish posted-message replay from physical hotkey/modifier-release delay and
   native-editor acceptance from browser formatting/undo/rendering. Probe the actual
   source editor if the capture bottleneck is not reproduced synthetically.
8. Update PR/checkpoint with evidence and one next-change recommendation. Stop there;
   no optimization implementation, main push or production release.
