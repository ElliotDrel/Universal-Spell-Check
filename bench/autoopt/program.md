# AutoOpt - Constraints & Goal

You are running an autonomous optimization loop on Universal Spell Check, modeled on Karpathy's AutoResearch. Each iteration proposes one change, applies it, builds, benches, verifies correctness, then either commits or reverts based on deterministic metrics.

## Goal

Reduce **median `request_ms`** by at least **5%** per kept change vs the current confirmation baseline. Tiebreaker: p95 `request_ms`.

## API Budget

API calls are part of the cost being optimized. The bench calls OpenAI once per input per measured or warmup trial:

```text
calls = 20 inputs * (runs + warmup)
```

Default autoopt screening must use `--runs 3 --warmup 1` (**80 calls**). Do not use the old `--runs 20 --warmup 3` setting (**460 calls**) for normal iteration. Only run a heavier confirmation bench, `--runs 10 --warmup 2` (**240 calls**), after a screen result shows a likely win.

## Off-Limits - Never Modify

- `src/BuildChannel.cs` - channel identity.
- `bench/inputs.json` and bench harness code - changing them invalidates measurements.
- `src/UpdateService.cs`, `.github/workflows/release.yml` - release pipeline.
- `replacements.json` - fixture data.

Autoopt state files under `bench/autoopt/*.json` and `bench/autoopt/*.jsonl` are writable.

## Behavioral Contracts

Enforced deterministically by `bench/check_correctness.py`, which derives assertions automatically from `bench/inputs.json` + `replacements.json` — no calibration file, no recalibration needed. If any assertion fails, revert.

1. **Protected literal passthrough** - URLs, UUIDs/session IDs, API keys, file paths, and opaque IDs in input must appear byte-identical in output.
2. **Brand replacements** - all canonical variants from `replacements.json` must be applied.
3. **Prompt-leak guard** - if the model echoes `instructions:` or `text input:`, those labels must be stripped before paste.
4. **Channel separation** - Prod and Dev never collide on hotkey, mutex, or app-data folder; values come from `BuildChannel.cs`.
5. **Update flow** - all update entry points go through `UpdateService.CheckAsync`.

You may rewrite the implementation of these contracts as long as the contract still holds.

## Keep / Revert Rule

| Outcome | Action |
|---|---|
| Build fails | Revert. Log `result: build_fail`. |
| Screen bench has any failed trial | Revert. Log `result: bench_fail`. |
| Correctness check fails | Revert. Log `result: correctness_fail`. |
| Screen median `request_ms` regresses or improves less than 8% | Revert. Log `result: reverted`, with delta. |
| Screen median improves at least 8%, but confirmation improves less than 5% | Revert. Log `result: reverted`, with confirmation delta. |
| Confirmed median `request_ms` improves at least 5% | Commit. Update both baselines. Log `result: kept`, with delta. |

## Search-Space Hints

- HTTP/2 keep-alive, connection pooling, `SocketsHttpHandler` tuning.
- Streaming response parsing, early termination.
- Prompt token reduction without changing semantics.
- Pre-warming connections, DNS, TLS handshake.
- Model parameter tuning (`max_tokens`, `temperature`, `reasoning.effort`) but not model swap.

## Discipline

- One change per iteration. Isolate variables.
- Keep API calls low. Use the 80-call screen bench by default.
- Read recent `journal.jsonl` entries before proposing; do not repeat dead ends.
- If 5 consecutive reverts/failures happen, write a `result: "stuck"` entry and reconsider strategy before continuing.
- Commit messages must be: `autoopt #{i}: {hypothesis} (+X.X%)`.
