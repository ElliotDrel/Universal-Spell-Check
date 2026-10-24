# Temporary Handoff: Latency Measurement PRs

> Superseded October 24: PR #8 merged and v0.13.3 was published and installed on this desktop. Earlier pending/unreleased/branch instructions below are historical. Continue from [the re-audit](research/2026-10-24-performance-reaudit.md). Current user instructions govern permissions.

Prepared October 22, 2026. Retain this file until the receiving thread has incorporated its context; remove it in a later explicit cleanup commit once superseded.

## Desktop move — October 23, read this first

- Continue from [the current desktop handoff](research/2026-10-23-desktop-latency-handoff.md).
- Use PR #8's branch, not the old main-only research state. Its latest code measures
  the local path and diagnoses fixture setup; no optimization or release has occurred.
- Treat the three-PR batch below as superseded historical proposals.

## Scope update — October 22, receiving session

- Supersede the mandatory three-PR batch below with Elliot's narrower checkpoint:
  minimal local timing instrumentation, noninteractive verification, then this
  machine's baseline and one evidence-backed optimization recommendation.
- Keep the original batch below as historical proposals. Do not implement network
  instrumentation or a new benchmark framework merely to complete that list.
- Keep optimization/behavior changes, main pushes, merges and publication out of scope.
- Require explicit idle-desktop authorization for clipboard/focus measurements.

- Read the [receiving-machine checkpoint](research/2026-10-22-local-latency-checkpoint.md) for current evidence and pending tests.

## Objective and Scope

- Optimize the full interval from physical hotkey activation to complete corrected text pasted into the target editor: first below 1 second, ideally below 500 ms.
- Chase the measured bottleneck; question requirements, delete unnecessary work, simplify, accelerate, then automate.
- Preserve correction quality, protected literals, rich formatting, clipboard recovery, undo behavior, and the established target-selection policy.
- The originating session was research-only; no performance implementation, app/settings change, live benchmark, or release occurred.
- The proposed next implementation batch is three atomic PRs for measurement and comparison, followed by a decision checkpoint.
- Treat PRs 4 onward as conditional proposals, not a mandate to implement every experiment.

## Read First

1. Read root `AGENTS.md` and follow its routing table plus applicable coding and PR lifecycle skills.
2. Read [the comprehensive audit](research/2026-10-22-latency-audit.md), committed in `3c755c7`.
3. Read `docs/architecture.md`, `rust/AGENTS.md`, `docs/bench.md`, `docs/debugging-principles.md`, and `.planning/rust-port-performance-handoff.md`.
4. Read `docs/replacements-and-logging.md` before changing telemetry fields and `docs/model-config.md` before changing request instrumentation.
5. Read the repository log-reader skill before accessing runtime logs; never parse raw runtime log files manually.
6. Read `src/BuildChannel.cs` for channel constants; Rust is the current product and C# is the retained reference.

## Portable Evidence and Machine Boundaries

- The source audit anchor was `a1293076b07a12ad9a5ad7e75699ba467bb29060`; recheck current source before editing.
- The committed `bench/records/2026-10-rust-port/` archive is portable; run its `verify.py` without starting the app.
- The archive verifier passed 17 blocks, 2,000 attempts including warmups, and 22 startup launches on October 22.
- Historical Rust local medians were 87–89 ms; historical live medians were 839–889 ms.
- Those synthetic benchmarks start at posted `WM_HOTKEY`, exclude physical delivery/human modifier release, and used a full developer-evidence store.
- The source machine's October 20–22 logs had 237 production attempts; v0.13.2 had 181 successes in 182 attempts, with 1,597-ms median and 2,869-ms p95 logged total.
- Successful v0.13.2 means were 1,760 ms total, 1,419 ms request, and 257 ms capture.
- Almost all request time was attributed to send, while reported wait was near zero: investigate the boundaries before calling upload or generation the bottleneck.
- All 236 successful observations across both versions had developer logging enabled, one attempt, no cached input tokens, and unique serialized request payloads.
- These are historical observations from another machine, not a new-machine baseline or verified editor-completion results.
- Private logs, credentials, installed binaries, ignored `benchmark_runs/`, and the old absolute Windows paths are NOT transferred by Git.
- Discover the receiving machine's repository, toolchain, installed app, settings and log locations; do not assume its username or directories match the source machine.
- Never copy real credentials into fixtures, print keys, or use an old quarantined benchmark client.
- Offline benchmarks must isolate settings AND key paths and cannot overwrite production data.

## Historical proposal: Three Atomic PRs (superseded)

| PR | Scope | Acceptance |
|---|---|---|
| 1 — Measure the complete local path | Add monotonic timings for dispatch, clipboard backup, held modifiers, copy, clipboard change, format reads, mapping, publication, diagnostic readback, settlement, and input injection; preserve partial timings on failures | Account for local elapsed time with minimal measurement overhead and no correction-behavior changes |
| 2 — Explain network latency | Add supported native request/connection timings, negotiated protocol, idle duration, and warm-request status; investigate existing send/wait labels | Distinguish measured transport phases from unknown server work; unavailable data stays null/unavailable, never fabricated zero |
| 3 — Establish repeatable comparisons | Extend the existing benchmark for idle scenarios, diagnostic modes, workload/target provenance, and editor-completion checks | Produce reproducible baseline reports with failures retained and correct-completion rates under 500/1,000 ms |

- PRs 1 and 2 are logically independent; avoid conflicting shared-schema edits or an unnecessary framework.
- PR 3 consumes the instrumentation; use explicit dependencies if stacked, otherwise base it on the integrated measurement work.
- Keep each PR independently reviewable and revertible, with relevant tests, documentation, and a clear measurement boundary.
- Preserve existing telemetry compatibility where practical; identify schema changes explicitly.
- Separate physical key delivery, app receipt, clipboard publication, `SendInput` completion, editor acceptance, and visible rendering.
- Do not claim a universal editor acknowledgment if only the controlled editor can provide one.
- Retain all attempts and failures; report success-conditioned percentiles alongside failure counts and all-attempt target-hit rates.
- Avoid misleading p99 claims from tiny samples or adding unrelated phase percentiles.
- Complete code and noninteractive checks first; if live validation needs user availability, report precisely what is prepared and what remains unverified.
- Stop after this batch and its baseline assessment; recommend the next optimization based on the new evidence.

## Conditional Next PRs

| PR | Single purpose | Promotion condition |
|---|---|---|
| 4 — Remove the largest measured capture cost | One demonstrated capture improvement, not a bundle | Lower measured capture time without formatting/recovery/modifier regressions |
| 5 — Improve connection warming | Change lifecycle/timing policy based on actual reuse and idle evidence | Better idle/resume latency without extra contention or excessive background traffic |
| 6 — Compare persistent Responses WebSockets | Experimental transport behind the existing interface with full-response validation and reconnect handling | Better final-paste latency than warmed HTTP at matched model/input/settings |

- Split diagnostic work removal into its own measured PR.
- Split clipboard-settlement reduction into its own PR after explaining the prior real-editor failures.
- Separate Chat Completions compatibility from a predicted-output experiment so endpoint and prediction effects are not confounded.
- Reserve edit-only output, faster service tiers, and local grammar models for later independent experiments.

## Existing Optimizations and Safety Constraints

- The app already has a persistent correction worker, app-lifetime WinHTTP session/connection, HTTP/2 enablement, and background logging.
- Warmup already runs at startup and after a 240-second worker wait timeout; unrelated messages can restart that timeout.
- Warmup uses unauthenticated `HEAD /v1/models` with a two-second deadline and discards results; it is not proof of model readiness or transport reuse.
- Clipboard capture already has a format listener and message-aware waits; do not assume its 25-ms argument is an unconditional sleep.
- The 50-ms publication interval was retained after real-editor zero-delay paste failures; do not remove it as an obvious win.
- Original clipboard backup occurs before the existing capture stopwatch; measure it separately.
- Unchanged text still pastes, and eventual paste follows the currently active app under the established contract.
- Do not introduce partial pastes, force modifier releases, skip literal checks, or weaken rich-text behavior to improve timing.
- Require a fresh explicit idle-desktop authorization before focus/clipboard automation, desktop benchmarks, or interactive acceptance on the new machine.
- Do not treat “run this on another machine” as permission to disrupt an active desktop, publish a production release, or merge every future PR.
- Follow current repository PR/deploy instructions; read the deploy skill before pushes and never force-push or reset unrelated work.

## Known Documentation Drift

- `docs/overview.md` still contains stale C# product, Dev launch, and packaging statements; correct relevant drift in the appropriate implementation PR after checking current source.
- Explicitly labeled C# architecture sections remain reference material rather than proof of current Rust behavior.
- Old proxy-era latency estimates are historical assumptions, not current WinHTTP measurements.
- External API sources in the audit were fetched October 22; recheck official docs before implementing endpoint/model-specific options.

## Receiving-Thread Start

- Inspect Git status and fetch upstream without discarding local changes.
- Ensure this handoff and audit are present from `origin/main`; fast-forward only when safe.
- Inspect existing PRs and branches before creating duplicates.
- Continue existing PR #8 and its current measured checkpoint. The old three-PR sequence is superseded; do not implement it automatically.
- Keep a current task list and clearly distinguish completed code, tested behavior, and pending live validation.
- Return the current PR link, test results, measurement limitations, and evidence-ranked next step; stop before conditional optimization PRs.
