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
