# Request attribution — October 23, 2026

No additional request-speed optimization is retained. The roughly 1.1-second
bucket is predominantly the interval after sending the request and before
response headers arrive. It is not a second of Rust computation or upload work.
The observations do not separate server processing from remote network/proxy wait.

## Independent assessment and controlled probe

At Elliot's request, Claude Opus 5.5 reviewed the approved source/docs through
`claude -p --model claude-opus-5-5 --effort medium --permission-mode auto` with Read
as its only tool. No fallback model was used. Its hypotheses about remote-response
wait, connection warmth and callback ordering were tested rather than accepted
as measured conclusions. The raw consultation remains private local evidence.

The production WinHTTP client already retains its session/connection and supports
HTTP/2. Its legacy `send_ms` includes both the WinHttpSendRequest API call and its
SENDREQUEST_COMPLETE callback wait. New diagnostic fields split those intervals.
Optional native WinHTTP request-times/stats/protocol queries run only in the
diagnostic client, not ordinary corrections. Unsupported queries return null.

Six fixed synthetic requests used the unchanged saved gpt-4.1 model and payload,
three on a fresh client and three on a separately prewarmed client. Each block
reused its client sequentially. There were no clipboard, GUI, startup, updater or
settings/key writes. Original production data hashes were unchanged afterward.
All six completed with HTTP 200, one attempt, and exact `This is a test.` output.

Measured binary source: db48f0b; verified package from
[CI run 40037503370](https://github.com/ElliotDrel/Universal-Spell-Check/actions/runs/40037503370).
The later opt-in metadata flag changes ordinary corrections' collection overhead;
the request-only probe still explicitly selects collection. This is attribution,
not a before/after product speed comparison.

| Block / request | Total ms | API call ms | Completion wait ms | Connection acquire ms | Native send-end → headers-end ms |
|---|---:|---:|---:|---:|---:|
| fresh / 1 | 2266.66 | 4.06 | 2262.26 | 35.908 | 2223.19 |
| same client / 2 | 1149.19 | 0.40 | 1148.59 | 0.027 | 1148.48 |
| same client / 3 | 1164.18 | 0.35 | 1163.38 | 0.009 | 1163.27 |
| prewarmed / 1 | 839.76 | 0.43 | 839.11 | 0.042 | 839.01 |
| prewarmed / 2 | 635.94 | 0.34 | 635.38 | 0.016 | 635.28 |
| prewarmed / 3 | 607.14 | 0.31 | 606.63 | 0.012 | 606.49 |

Native request-write intervals were 0.16–0.21 ms. Connection acquisition was
35.9 ms for the fresh first request and under 0.05 ms thereafter; later connection
establishment timestamps predated their current request. Protocol flags were 1
(HTTP/2). Stats flags remained 32 and request index 0 even on subsequent requests;
do not infer a new socket or definitive reuse solely from those counters.

Two sequential small blocks cannot establish that prewarming caused the lower
response times. The app already warms its connection. No warm-interval change,
transport rewrite, cache, model/prompt change, or speculative overlap is shipped.
Any further change needs matched alternating runs and correctness/editor checks.

## Deterministic regression protection

CI runs the optimized product over ten frozen cases and requires exact final text
and rich payloads. A mutated expected output must fail in the deliberately changed
case; a setup failure is not an acceptable negative control. Unicode reads/writes
are explicit. The API probe must reject invocation without `--allow-api` before
reading settings or making requests. No real provider credentials are in CI.

Existing Dev/Prod tests cover protection, parameters, native HTTP body reads,
deadlines, asynchronous lifetime, and the 50-ms settlement minimum/message pumping.
A new gated loopback test verifies a request deadline while the server withholds
headers. An earlier proposed assertion that send completion always precedes
server release was falsified on this platform and removed; it was not a product
regression. Timing thresholds on shared runners do not replace deterministic
correctness checks or matched live performance evidence.

Evidence verifiers now reject duplicate/missing cases and wrong workload hashes,
even under Python -O, with mutation tests proving rejection. The earlier native
workload bytes are frozen independently of the browser acceptance fixture.

## Faster iteration

`bench/format-rust.ps1` checks formatting locally by default using Cargo, or an
explicit `-Rustfmt` portable binary. `-Write` opts into formatting. This desktop
now has the official checksum-verified formatter/runtime for the pinned Rust
1.99.1 toolchain in ignored project tooling; no global PATH/registry change.
The formatter reports rustfmt 1.10.0-stable. Source and SHA-256 provenance are
retained beside the tools. CI caches only Cargo registry/git/build outputs using
the SHA-pinned official actions/cache v5 action, and still runs all gates.
The local format check passed. In the recorded cold/cache-restored pair, lint plus Dev/Prod tests and optimized build took 252 vs 78 seconds; cache restore added 21 seconds (99 seconds combined). These are observed CI stage times across two runners, not a universal timing guarantee; see ci-iteration.json.

`samples.json` retains relative measurements without credentials, host identity
or raw machine-uptime counters. Native indices follow Microsoft's
[request time entries](https://learn.microsoft.com/en-us/windows/win32/api/winhttp/ne-winhttp-winhttp_request_time_entry).
The [send API contract](https://learn.microsoft.com/en-us/windows/win32/api/winhttp/nf-winhttp-winhttpsendrequest)
explicitly permits synchronous behavior on an asynchronous session.

## Final runtime confirmation

The final runtime source 3b85615 passed
[CI run 40038507675](https://github.com/ElliotDrel/Universal-Spell-Check/actions/runs/40038507675),
including the corrected gated-deadline test and frozen positive/negative pipeline.
The strengthened gate was also executed locally against that CI binary: all ten
cases passed, the deliberately mutated case failed, and missing API opt-in was
rejected. One additional prewarmed synthetic request confirmed its diagnostic
client: 836.82 ms total, 0.364 ms in the send API, 836.20 ms completion wait,
0.020 ms acquisition, 0.170 ms native request writing, and 836.09 ms between
native send-end and headers-end. It returned the exact expected text in one
attempt. Saved data presence and encrypted credential contents remained unchanged.
This confirmation is the seventh sample and is separate from the initial blocks.
Run `python bench/records/2026-10-desktop-latency/request-attribution/verify.py` to check the retained facts.

The later CI/tooling source 728a8b2 also passed
[CI run 40038816667](https://github.com/ElliotDrel/Universal-Spell-Check/actions/runs/40038816667).
No new product speedup is claimed. The earlier verified settlement improvement
remains; the current work adds trustworthy attribution and deterministic gates.
