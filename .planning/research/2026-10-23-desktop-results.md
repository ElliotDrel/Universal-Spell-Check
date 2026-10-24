# Desktop installation and latency checkpoint — October 23, 2026

## Installation repaired

The October 3 Hermes setup published a local Release build and ran its startup smoke against real user data. That smoke registered the checkout executable for login startup. It displayed the default local version 1.0.0 and skipped updates as not_installed. See [incident #9](https://github.com/ElliotDrel/Universal-Spell-Check/issues/9).

Installed official v0.13.1 (published checksum verified); its own launch updater downloaded/applied/restarted into official Rust v0.13.2 before Elliot requested no further updater testing. No later updater test was performed. Final running PID 4024, executable `%LocalAppData%/UniversalSpellCheck/current/UniversalSpellCheck.exe`, file/package/uninstall versions 0.13.2. Login startup and Desktop/Start Menu shortcuts target that managed executable. Encrypted key bytes matched the pre-repair baseline. Preserved BEFORE-repair-data backup and original evidence; removed obsolete checkout publish payload. No new production release.

Actual installed Ctrl+Alt+U corrected the complete disposable Notepad selection, visibly preserving URL, Windows path and UUID. One live run took 1,812.68 ms including 1,727.71 ms request; this single acceptance run is not a controlled live latency distribution. Browser-rich rendering remains unverified: the background browser fixture could not be activated through its available browser interface and was closed without a successful correction. No claim of original Google Docs/ChatGPT acceptance.

## Controlled local desktop baseline

Candidate built by [CI at ee08a5f](https://github.com/ElliotDrel/Universal-Spell-Check/actions/runs/39971592973), binary SHA256 `1aeb80006d226fc2382faa5f1494635897eb6ecdd7de6c3e6d8e6ec8edc08032`. Use frozen ten-case workload and replay responses, gpt-4.1 metadata, unchanged transport and 50-ms settlement. Native registered-message trigger to complete verified native-editor replacement; excludes remote generation, physical-key delivery/human hold and browser rendering.

| Mode | Measured / successes | Warmups | Completion median | Completion p95 | Under 500 / 1,000 ms |
|---|---:|---:|---:|---:|---:|
| Diagnostics off | 30 / 30 | 10 | 82.788 ms | 93.273 ms | 30/30 / 30/30 |
| Diagnostics on, fresh isolated evidence store | 30 / 30 | 10 | 82.739 ms | 89.691 ms | 30/30 / 30/30 |

Both modes retain 27/30 handwritten matches. Three rich-list samples per block retain two spaces before a newline, matching frozen pipeline output but differing from handwritten expectations; see [#16](https://github.com/ElliotDrel/Universal-Spell-Check/issues/16). Do not hide this distinction or pool logging modes. Single sequential blocks do not establish a logging-mode performance difference. No p99 estimate.

| Operation mean | Off | On |
|---|---:|---:|
| Clipboard capture | 4.709 ms | 3.208 ms |
| Original clipboard backup | 3.598 ms | 3.287 ms |
| History exclusion | 2.336 ms | 1.077 ms |
| Paste total through input return | 63.868 ms | 66.249 ms |
| Nested settlement wait | 59.647 ms | 61.611 ms |
| Nested diagnostic readback | skipped | 0.952 ms |
| RTF read | 0.025 ms | 0.024 ms |
| Format enumeration | 0.444 ms | 0.342 ms |
| Unattributed coordinator residual | 0.350 ms | 0.326 ms |

Capture required one copy/read attempt in every measured trial. The large laptop capture bucket is not reproduced by this synthetic editor. Do not subtract this baseline from other-machine/live medians to invent network time. The reader's coordinator totals and external completion boundary remain distinct.

Retain three desktop preflights separately: one occupied Ctrl+Alt+B registration and two foreground-only aborts, zero trials. Exact focus, selection and fixture text passed; only foreground failed. Explicit benchmark-only Ctrl+Alt+F10 and 30-second unmeasured setup allowance allowed supported external activation; every gate stayed intact. Default benchmark behavior remains unchanged. See [#14](https://github.com/ElliotDrel/Universal-Spell-Check/issues/14) and [#15](https://github.com/ElliotDrel/Universal-Spell-Check/issues/15).

## One next optimization to review

The dominant local phase is deliberate clipboard settlement. Its 50-ms minimum is an existing reliability requirement; measured wait adds about 9.6–11.6 ms beyond that minimum. Source `clipboard::settle_publication` uses a deadline loop around message-aware timed waits. Scheduling/timer behavior is a plausible overshoot mechanism; its exact contribution is not separately measured.

Recommend one bounded experiment: make settlement waiting more deadline-accurate while preserving at least 50 ms and message pumping, then require original-editor paste reliability. Potential savings are limited to measured overshoot, not the full wait. Do not remove RTF/metadata first: this workload provides no material latency case for doing so. This is a recommendation only; no optimization was implemented. No model, transport or paste-delay change.

Public per-attempt timings, warmups, sanitized provenance and preflights are in [the desktop record](../../bench/records/2026-10-desktop-latency/). Run its verify.py. Private runtime evidence and protected pre-repair backup remain ignored locally. Installed Production stays running; disposable editor, browser fixture and server were closed. Agent-installability is tracked in [#13](https://github.com/ElliotDrel/Universal-Spell-Check/issues/13).

## Settlement investigation completed later October 23

The recommendation-only status above is superseded by the authorized experiment in [the settlement record](../../bench/records/2026-10-desktop-latency/settlement/README.md). A high-resolution wait retains the 50-ms minimum and message pumping, with the original wait as fallback. All 480 matched desktop attempts passed delivery checks; candidate median local completion improved about 10–13 ms. Seven actual-hotkey candidate runs delivered complete text; supported rich structure matched the installed baseline. One unsupported-rich formatting loss reproduced in installed Production and is tracked separately in issue #18. The candidate remains on PR #8; no release was published.

## Performance follow-up and iteration tooling

PR #8 was marked ready for review at Elliot's request. The timer improvement is
one verified step, not an assertion that the application is optimally fast.
`AGENTS.md` now explicitly prioritizes speed, low-level/KISS implementation,
questioning unnecessary work and building reusable verification when friction
slows iteration.

The requested independent consultation was attempted with `claude -p --effort
medium`: the `fable` alias resolved to `claude-fable-5-1`; after Elliot selected
Fable 5.1, that exact model was retried. Both reported usage credits required.
The attempted 5.5 identifier was rejected as unrecognized. No independent model
assessment was obtained, and no substitute model or purchased credits were used.

### Larger opportunities, ranked by current evidence

1. **Understand request latency before changing transport.** The seven live
   candidate runs averaged 1,206.55 ms total and 1,132.67 ms request time. These
   small acceptance samples establish priority, not a stable performance baseline.
   `http::Client` already retains the WinHTTP session/connection, enables HTTP/2
   and warms the service. Replacing WinHTTP or adding more warm requests without
   measuring actual negotiation/reuse would be guessing. A focused next experiment
   should expose non-secret WinHTTP DNS/connect/TLS/request lifecycle milestones
   and negotiated protocol/reuse where supported, then compare cold and warm live
   requests. Do not infer server computation from the existing header-wait bucket.
2. **Question repeated remote work.** An exact-request cache might avoid the
   dominant delay on repeated selections, but observed hit rate is unknown. First
   measure repeated-request frequency using bounded, non-content identifiers.
   Any cache must account for model, prompt, replacements and target context,
   credential boundaries and protected-literal restoration. Do not add it merely
   because hashing is cheap, or claim a general speedup from repeated fixtures.
3. **Consider overlap only after measuring the work to hide.** `runtime::run`
   performs history exclusion and request preparation before starting the remote
   request. Much of the remaining local path is small compared with the request;
   moving work concurrently risks clipboard ownership/order and should have a
   measured benefit before introducing synchronization. The 50-ms settlement,
   original clipboard recovery and complete atomic replacement remain requirements.
4. **Avoid cosmetic low-level rewrites.** Current capture and RTF reads are already
   small in the matched desktop record. More unsafe code, custom sockets, busy
   polling or removing reliability waits is not evidence of better performance.
   Keep the verified native timer change; select the next experiment by complete
   editor-replacement latency and reliability, not a component microbenchmark alone.

### Reusable report, rather than another throwaway analysis script

`bench/phase_report.py --block NAME=DIRECTORY` accepts repeated ordered blocks and
emits JSON with successful measured median/p95/mean, nested coordinator phases,
sample counts, all failures, warmup failures and incomplete case coverage. Missing
timings stay missing. It checks workload/settings/machine/trigger identity within
each diagnostic mode and also checks benchmark hotkey/setup limits. It neither
pools modes nor awards an automatic winning verdict. Parent/nested intervals
overlap and must not be added together. `--output` exclusively creates a fresh
file, preserving prior evidence.

Example, from the worktree root with retained local artifacts:

```powershell
python bench/phase_report.py --block A1=benchmark_runs/settlement-2026-10-23/off-a1 --block B1=benchmark_runs/settlement-2026-10-23/off-b1 --block B2=benchmark_runs/settlement-2026-10-23/off-b2 --block A2=benchmark_runs/settlement-2026-10-23/off-a2 --output benchmark_runs/phase-report.json
```

This command reads only benchmark artifacts; it never launches the app, touches
the clipboard or credentials, or reads runtime logs. The four new guards and five
existing comparison tests pass. The real retained ABBA blocks produced a complete
report. CI now runs the new guards. Foreground activation and live editor acceptance
still require the approved desktop tools; this report does not solve those layers
or replace the correctness gates. No additional product optimization or desktop
test was performed in this follow-up. Resume Fable consultation when usage access
is available, then choose and verify one measured product experiment.

Provenance clarification: the controlled desktop measurement used ee08a5f;
the subsequent 10–13 ms settlement comparison used baseline f60aacd against
candidate 9b6033a. Browser rich acceptance used synthetic contenteditable markup,
not actual Google Docs or ChatGPT; its limitations remain in the settlement record.

## Completed Opus request investigation

The historical Fable access blocker above is superseded for this investigation:
Elliot requested and approved Opus 5.5, which completed the read-only consultation
at medium effort in auto permission mode. The approved source and native probe
findings are summarized in the [request attribution record](../../bench/records/2026-10-desktop-latency/request-attribution/README.md).
Seven fixed requests returned exact expected text. Native send API calls took
0.3–4.1 ms; most request time occurred after native transmission and before response
headers. That interval combines remote network/proxy/service work and is not an
isolated measurement of model generation. No new request-speed optimization is
retained, and prewarming differences are not a causal speedup claim.

The optimized native pipeline now has deterministic CI positive/negative controls,
coverage/hash checks with optimized-Python mutation tests, and gated request-deadline
tests. The live probe requires explicit API opt-in and avoids desktop/startup work.
Local pinned formatting and cached CI reduce iteration friction. Production stays
installed and running; its saved data was preserved. No release was published.
