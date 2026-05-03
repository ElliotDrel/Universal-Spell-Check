# AutoOpt — Constraints & Goal

You are running an autonomous optimization loop on Universal Spell Check, modeled on Karpathy's AutoResearch. Each iteration: propose ONE change, apply, build, bench, verify correctness, then either commit or revert based on a deterministic metric.

## Goal

Reduce **median `request_ms`** (headless bench) by **≥5%** per kept change vs current baseline. Tiebreaker: p95 `request_ms`.

## Off-limits — never modify

- `src/BuildChannel.cs` — channel identity (hotkey, mutex, app-data folder)
- `bench/**` — would invalidate measurements (this includes `correctness.json` and `inputs.json`)
- `src/UpdateService.cs`, `.github/workflows/release.yml` — release pipeline
- `replacements.json` — fixture data (you may change *how* replacements are applied, not which ones)

## Behavioral contracts (must be preserved)

These are enforced deterministically by `bench/check_correctness.py` reading `bench/correctness.json`. If any assertion fails → revert.

1. **URL protection** — any `https?://...` URL in input must appear byte-identical in output.
2. **Brand replacements** — all canonical→variants pairs from `replacements.json` must be applied; case-sensitive, ordinal, longest-variant-wins.
3. **Prompt-leak guard** — if model echoes `instructions: ...` or `text input:` label, both must be stripped before paste.
4. **Channel separation** — Prod and Dev never collide on hotkey, mutex, or app-data folder; values come from `BuildChannel.cs`.
5. **Update flow** — all update entry points go through `UpdateService.CheckAsync`.

You may rewrite the implementation of any of these (e.g., refactor `TextPostProcessor`) as long as the contract still holds.

## Keep / revert rule (deterministic)

After build + bench + correctness:

| Outcome | Action |
|---|---|
| Build fails | Revert. Log `result: build_fail`. |
| Bench has any failed trial | Revert. Log `result: bench_fail`. |
| Correctness check fails | Revert. Log `result: correctness_fail` with which assertion. |
| Median `request_ms` regresses or improves <5% | Revert. Log `result: reverted`, with delta. |
| Median `request_ms` improves ≥5% | **Commit.** Update baseline to new bench. Log `result: kept`, with delta. |

## Search-space hints (not requirements)

- HTTP/2 keep-alive, connection pooling, `SocketsHttpHandler` tuning
- Streaming response parsing, early termination
- Prompt token reduction (without changing semantics)
- Pre-warming connections, DNS, TLS handshake
- Model parameter tuning (`max_tokens`, `temperature`, `reasoning.effort`) — but not model swap

## Discipline

- **One change per iteration.** Isolate variables.
- Read recent `journal.jsonl` entries before proposing — don't repeat dead ends.
- If 5 consecutive reverts: pause, write a `result: stuck` entry summarizing what's not working, and reconsider strategy before continuing.
- Commit messages must be: `autoopt #{i}: {hypothesis} ({+X.X%})`
