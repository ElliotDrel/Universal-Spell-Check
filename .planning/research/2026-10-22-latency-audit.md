# Hotkey-to-Paste Latency: Next Optimization Wave

## Recommendation

- Prioritize capture latency and transport attribution before changing networking libraries.
- Compare measured HTTP/2 connection reuse against a persistent Responses WebSocket using the same model, text, quality requirements, and final-paste boundary.
- Treat sub-1-second completion as the first distribution target; treat sub-500-ms completion as a more demanding target likely to require reducing or bypassing remote work.
- Preserve one complete, validated paste, protected literals, formatting, and failure recovery.
- Separate provider execution we cannot directly control from request design we can control: predicting unchanged output, avoiding duplicate work, and choosing a supported service tier can influence the remote duration.

## Current Evidence

### Recent Production Observations

- Read October 20–22, 2026 production records through the approved log-reader CLI on October 22.
- Observe 237 attempts: 55 on v0.13.1 and 182 on v0.13.2; all used `gpt-4.1`.
- Retain the single v0.13.2 capture failure: the selected application did not copy text.
- Interpret `success` as pipeline completion, not verified target-editor replacement.
- Keep these observations separate from controlled benchmarks and avoid attributing differences between versions to code changes.

| Version | Attempts / logged successes | Successful total p50 | Successful total p95 | Successful runs under 1 s / 0.5 s |
|---|---:|---:|---:|---:|
| 0.13.1 | 55 / 55 | 1,732 ms | 2,699 ms | 0 / 0 |
| 0.13.2 | 182 / 181 | 1,597 ms | 2,869 ms | 4 / 0 |

- Calculate the displayed observational quantiles as sorted sample index `floor((n-1)*q)`; do not confuse them with interpolated benchmark quantiles.
- Observe v0.13.2 successful means of **1,760 ms total**, **1,419 ms request**, and **257 ms capture**.
- Attribute about **81% of that mean total** to the request boundary, without labeling all of it model generation.
- Observe all 236 successful runs with developer logging enabled, one request attempt, and zero reported cached input tokens.
- Find zero repeated serialized request payloads among those 236 successful runs; an exact-response cache has no demonstrated hit-rate benefit in this sample.

| Phase, all 236 successful observations | Mean | p50 | p95 |
|---|---:|---:|---:|
| Total | 1,784.8 ms | 1,621.1 ms | 2,868.8 ms |
| Capture | 275.2 ms | 225.0 ms | 524.4 ms |
| Request | 1,428.8 ms | 1,279.9 ms | 2,326.8 ms |
| Reported request send | 1,399.3 ms | 1,248.9 ms | 2,285.2 ms |
| Reported request wait | 0.059 ms | 0.046 ms | 0.115 ms |
| Response download | 29.1 ms | 30.3 ms | 46.8 ms |
| Replacements | 0.068 ms | 0.044 ms | 0.200 ms |
| Paste, ending at input injection | 66.3 ms | 65.9 ms | 78.5 ms |

- Do not add phase percentiles: their slow samples need not be the same runs.
- Treat the send/wait split as an attribution problem: callback boundaries do not establish wire-upload time versus server generation time.
- Include short text in the investigation: 93 successful selections of at most 100 characters still averaged 1,614 ms total and 1,294 ms request.

### Verified Historical Benchmark

- Verify the [portable benchmark archive](../../bench/records/2026-10-rust-port/README.md): 17 blocks, 2,000 attempts including warmups, and 22 startup launches passed its verifier this session.
- Retain October 17 Rust local medians of **89.136 / 87.026 ms** and live medians of **888.976 / 838.941 ms**.
- Retain compute-only medians of **0.09175 / 0.14525 ms**; this is a poor first optimization target.
- Respect its boundary: posted registered hotkey message to verified native-editor paste, excluding physical input delivery and human modifier release.
- Respect its workload: synthetic fixtures and a full evidence store reporting `storage_limit`, rather than ordinary browser use and successful evidence capture.
- Do not subtract historical local medians from current production medians to manufacture a network estimate.

### What the Code Already Does

Audit anchor: checkout `a1293076b07a12ad9a5ad7e75699ba467bb29060`.

| Area | Verified implementation | Consequence |
|---|---|---|
| Worker lifetime | Persistent correction worker, cached settings/key/replacements in [runtime.rs](../../rust/src/runtime.rs) | No interpreter or model-client startup per ordinary correction |
| Networking | App-lifetime WinHTTP session/connection; HTTP/2 enabled in [http.rs](../../rust/src/http.rs) | Connection pooling is already present; negotiated protocol/reuse remain unmeasured |
| Warming | Startup warm request and warm request after a 240-second worker wait timeout | Not a guaranteed fixed-cadence heartbeat; worker messages restart the wait |
| Warm request | Unauthenticated `HEAD /v1/models`, two-second deadline, errors/status discarded | Can exercise transport; does not establish model readiness or successful reuse |
| Copy | Modifier-release checks, sequence validation, clipboard listener, message-aware wait in [clipboard.rs](../../rust/src/clipboard.rs) | Already wakes for clipboard messages; replacing a supposed fixed 25-ms sleep is not a proven win |
| Backup | Enumerates and duplicates original clipboard formats before capture | Delayed rendering and large prior clipboard contents deserve separate timing |
| History exclusion | Publishes captured text again before requesting correction | Extra clipboard operation is a candidate for overlap, with history semantics protected |
| Paste | Publishes rich/native/text formats, optional readback, 50-ms settlement, modifier check, `SendInput` | 50-ms wait is deliberate and previously required by real-editor failures |
| Diagnostics | Background disk writer; asynchronous evidence capture, but foreground readback/metadata work | Already offloads storage; diagnostic CPU and clipboard contention still need measurement |
| Request shape | One non-streaming Responses request, full corrected text, short prompt in [core.rs](../../rust/src/core.rs) | No chained LLM passes to remove; full-output regeneration remains an experimental opportunity |

## What Other Apps Actually Teach Us

| Project / primary evidence | Documented practice | Transfer to this app | Limit |
|---|---|---|---|
| [Wispr Flow WebSocket API](https://api-docs.wisprflow.ai/websocket_api) | Direct client connection; incremental audio; explicit commit; optional binary MessagePack | Avoid proxy hops; prepare connections early; mark completion explicitly | Public API documentation does not reveal every desktop-client optimization; its example closes the socket after final output |
| [OpenWhispr engineering write-up](https://openwhispr.com/blog/local-streaming-speech-to-text) | Stateful local streaming; reuse encoder context; commit the flushed stream instead of decoding the recording twice | Delete redundant passes and reuse completed work | It processes while the user speaks; its stop-to-result latency is not our hotkey-to-paste latency |
| [Handy transcription-manager source](https://raw.githubusercontent.com/cjpais/Handy/main/src-tauri/src/managers/transcription.rs) | Reuses loaded engine state; background loading; idle-unload policy; avoids unload during recording | Keep expensive resources resident with explicit lifecycle handling | Local speech-model speed says nothing quantitative about cloud grammar correction |
| [VoiceInk latency guide](https://tryvoiceink.com/docs/transcription-taking-too-long) | Breaks time into loading, transcription, cleanup, enhancement, delivery; offers prewarm at launch/wake | Measure each stage, including resume from sleep and optional enhancement | Hardware/model-specific recommendations cannot be imported as Windows latency claims |
| [VoiceInk issue #1003](https://github.com/Beingpax/VoiceInk/issues/1003) | User reports severe first-use delay and allegedly warming the wrong encoder | Verify that warmup reaches the resource the real request uses | User report, not a reproduced finding or proof about the current release |
| [WhisperLive](https://github.com/collabora/WhisperLive) | Separates streamed audio ingestion, partial results, and committed results | Distinguish progress from valid final completion | Transcription infrastructure rather than a directly comparable correction app |

- Reject “uses WebSockets” as an explanation by itself: transport persistence, incremental input, state reuse, and early execution are different mechanisms.
- Keep partial previews out of the success metric; the user asked for complete corrected text pasted into the target.
- Avoid copying binary audio optimizations into tiny JSON text requests without a measured byte-transfer bottleneck.
- Treat competitor statements as implementation evidence or vendor claims, not independently replicated performance results.

## Ranked Experiments

### 1. Establish the Real Critical Path

- Add monotonic milestones for hotkey receipt, worker dispatch, backup, modifier release, copy injection, clipboard change, each clipboard flavor, request preparation, network stages, output completion, mapping, publication, readback, settlement, paste injection, and editor completion.
- Query supported [WinHTTP request timings](https://learn.microsoft.com/en-us/windows/win32/api/winhttp/ns-winhttp-winhttp_request_times) and [request/connection options](https://learn.microsoft.com/en-us/windows/win32/winhttp/option-flags) for connection setup, protocol, and transport evidence; unavailable fields must remain unavailable rather than zero.
- Compare native timestamps with an ETW/network trace when the send/wait attribution remains unexplained.
- Preserve partial timing and retry history on failures; current successful timing rows do not characterize failed operations.
- Separate the physical-hotkey boundary from app receipt and final editor acceptance; [SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput) reports injected events, not target completion.
- Define visible completion separately from editor event completion when rendering is delayed; neither the current production timer nor a clipboard write measures that final paint.
- **Expected benefit:** attribution rather than direct speed; prerequisite for avoiding the wrong rewrite.
- **Decision gate:** identify where every substantial interval belongs, including the unexplained request phase split and actual modifier-release contribution.

### 2. Reduce the Real Capture Cost

- Break the v0.13.2 257-ms mean capture into held modifiers, source-app copy response, lock contention, and format reads.
- Time original clipboard backup separately because it runs before the existing capture stopwatch.
- Test diagnostics enabled/disabled without losing basic timing, and stratify by source app and plain/rich selection.
- Investigate avoiding RTF reads when no product consumer needs RTF; retain required HTML and Google Docs native data.
- Preserve source data before history exclusion; fetching rich formats later may race clipboard replacement.
- Avoid synthesizing modifier releases merely to improve a timer; a user holding the hotkey can impose a real lower bound on the current input contract.
- Test application-specific selection retrieval only where the target exposes reliable selection and formatting semantics; avoid replacing a selected range through whole-control `ValuePattern` writes.
- **Expected benefit:** potentially tens to hundreds of milliseconds if removable capture work dominates; no savings estimate is established yet.
- **Decision gate:** isolate a removable measured component; retain modifier safety and original-clipboard failure recovery.

### 3. Make Existing HTTP Warming Verifiable

- Log warm start/end/status, last real request, idle duration, connection setup, negotiated HTTP version, and network/resume transitions.
- Compare current warming, no periodic warming, and a bounded activity-aware policy at 0, 30, 60, 120, and 240 seconds idle plus sleep/resume.
- Use a deadline independent of unrelated worker messages if the current timeout is being postponed.
- Prefer waking/revalidating a connection when the user resumes activity over unconditional high-frequency traffic all day.
- Check whether unauthenticated warm traffic uses reusable transport for the authorized POST; do not assume a status error prevents TLS warming or proves it useful.
- **Expected benefit:** the measured reconnect/setup penalty on affected runs; near zero for already-reused connections.
- **Decision gate:** reduce cold/idle p95 without added correction contention, excessive wakeups, or a parallel backend service.

### 4. Compare Persistent Responses WebSocket Against Warm HTTP/2

- Prototype a single reusable Responses WebSocket behind the existing request interface.
- Hold model, prompt, input, and completion validation constant.
- Compare non-streaming HTTP, HTTP streaming buffered until completion, and WebSocket buffered until completion to avoid confounding protocol with streaming.
- Reconnect outside the hot path where possible; exercise sleep, network change, expired connections, cancellation, and lost final events.
- Keep unrelated corrections independent; do not chain previous user selections into subsequent requests.
- Evaluate instruction-only request preparation separately from transport reuse.
- **Expected benefit:** unknown incremental improvement over warm HTTP/2; potentially useful for idle tails, not a guaranteed hundreds-of-milliseconds win.
- **Decision gate:** retain only if end-to-final-paste p50/p95 improve enough to justify recovery complexity.

[OpenAI's WebSocket guide](https://developers.openai.com/api/docs/guides/websocket-mode) documents persistent Responses connections, `generate: false` request preparation, and a 60-minute connection limit. Its emphasis on tool-heavy continuation workflows means its benefits cannot be assumed for our single independent correction. Verify selected-model compatibility before the experiment.

### 5. Remove Diagnostic and Clipboard Work From the Critical Path

- Measure metadata/string duplication, native MIME encoding, rich-result serialization, and clipboard readback separately.
- Keep detailed serialization after paste where it does not change the evidence contract.
- Test starting the network request once required input and protection are ready while doing independent work during the request.
- Consider overlap for history exclusion only with explicit ordering that prevents a late write from overwriting corrected output.
- Keep clipboard operations serialized; do not introduce a background readback that races target paste.
- Keep any required publication interval measured from actual publication: currently diagnostic readback finishes before the 50-ms wait begins.
- **Expected benefit:** bounded by measured local work; likely smaller than capture or request improvements.
- **Decision gate:** gain verified time without weakening diagnostic fidelity or introducing clipboard races.

### 6. Revisit the 50-ms Publication Interval Through a Proven Contract

- Investigate why zero-delay paste failed using actual editors and clipboard ownership timing.
- Compare 50, 35, 20, and 0 ms only in isolated acceptance tests; keep the known-safe default until failures are understood.
- Distinguish clipboard publication from target readiness: [clipboard-change notification](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-addclipboardformatlistener) confirms a clipboard change, not that another app is ready to paste.
- Use target-specific insertion only if it preserves selection, undo behavior, focus policy, formatting, and completion acknowledgment.
- **Expected benefit:** at most 50 ms from eliminating the intentional interval itself, excluding scheduler overrun.
- **Decision gate:** no missed, duplicate, wrong-target, or malformed pastes across real editors and repeated trials.

### 7. Avoid Regenerating Unchanged Text With Predicted Outputs

- Supply the protected input text as the predicted output because spell corrections usually preserve most text.
- Test the same `gpt-4.1` model on Chat Completions with and without prediction before comparing against Responses.
- Track accepted/rejected predicted tokens, complete response time, cost, and correction quality across short and long selections.
- Preserve placeholder restoration and rich mapping exactly once after complete valid output.
- **Expected benefit:** potentially substantial on mostly unchanged longer text; unknown on short selections or heavily rewritten output.
- **Decision gate:** improve final completion latency without missed corrections, changed semantics, or unacceptable rejected-token cost.

[OpenAI's Predicted Outputs guide](https://developers.openai.com/api/docs/guides/predicted-outputs) explicitly includes GPT-4.1 support and demonstrates Chat Completions. Do not assume `prediction` can simply be added to the current Responses payload. This changes the generation workload and endpoint; keep it separate from the transport-only comparison.

### 8. Return Edits Instead of Rewriting the Entire Selection

- Explore an edit-only response for long selections with sparse corrections.
- Anchor edits to exact original substrings plus validated positions and a request input hash; reject overlaps, ambiguous anchors, or missing protected literals.
- Reconstruct the complete output locally and pass it through the same formatting/paste pipeline.
- Benchmark malformed responses and fallback cost; verbose structured syntax can be slower than full text for short selections.
- **Expected benefit:** potentially large output-token reduction on long sparse edits, with higher correctness risk than prediction.
- **Decision gate:** promote only after predicted output, if long-text latency remains a major problem.

[OpenAI's latency guidance](https://developers.openai.com/api/docs/guides/latency-optimization) prioritizes reducing generated tokens and unnecessary requests; prompt shortening usually offers a smaller benefit. These are general principles, not a measured gain for this app.

### 9. Test Supported Faster Service Tiers Separately

- Evaluate a supported fast service tier with the same model and workload if account eligibility and pricing make it practical.
- Record the tier actually returned, not only the tier requested.
- Compare cost per accepted correction and p95, rather than selecting a provider headline benchmark.
- **Expected benefit:** possible remote-latency reduction; account/model support and gain remain unverified.
- **Decision gate:** retain only if the observed gain justifies the cost.

[OpenAI's Fast mode documentation](https://developers.openai.com/api/docs/guides/fast-mode) describes request-level `service_tier`, supported-model restrictions, and possible downgrade to standard processing. This is an optional provider-side lever, not a prerequisite for local optimization.

### 10. Reserve Local Correction for a Separate Product Experiment

- Evaluate a small resident grammar model or narrowly deterministic correction path if sub-500-ms completion remains infeasible remotely.
- Measure CPU/GPU residency, first-use loading, battery cost, and grammar quality on this laptop.
- Avoid treating a dictionary's lack of spelling flags as proof that grammar needs no correction.
- Account for local rejection plus cloud fallback latency; a serial classifier can worsen ordinary requests.
- **Expected benefit:** removes network/server dependence for accepted local corrections, but changes quality and resource tradeoffs.
- **Decision gate:** require an explicit accuracy target and measured route coverage before adoption.

## Lower-Priority and Rejected Ideas

| Idea | Recommendation / reason |
|---|---|
| Exact-response cache | Defer: zero repeated payloads in 236 successes; if tested later, key by complete normalized request/model/prompt/protection version and remap current formatting |
| Semantic cache | Reject for automatic corrections: similar sentences can require different edits |
| Prompt caching | Defer: zero observed cached tokens and a short shared prefix; measure eligibility rather than padding instructions solely to qualify |
| Incremental paste | Reject as the default: partial output cannot yet pass complete literal/format validation and creates additional editor/undo races |
| Pre-correct every selection | Defer: changes privacy, cost, and selection-race behavior; moving work before the stopwatch is not free |
| Parallel sentence requests | Defer: can break grammar context and formatting; final completion waits for the slowest chunk |
| Hedge duplicate cloud requests | Defer: doubles work/cost and complicates cancellation; investigate only if measured remote tails justify it |
| Local proxy server | Reject absent evidence: the app already connects directly with a persistent native client |
| HTTP/3, custom TCP/TLS, DNS pinning | Defer until setup/RTT/loss is measured; protocol support and proxies matter, and IP pinning is brittle |
| Real-time process priority, busy polling, global timer changes | Reject as default: risk UI/input contention and battery cost for unproven benefit |
| Parser/allocator/regex micro-optimization | Defer: historical compute is around one tenth of a millisecond |
| Skip unchanged paste | Treat as a behavior change, not an optimization: the existing contract deliberately pastes unchanged text |
| Disable backup or safety checks | Reject silent removal: a faster damaged selection is a failed correction |

[OpenAI's prompt-caching documentation](https://developers.openai.com/api/docs/guides/prompt-caching) requires an eligible repeated prefix and model-dependent minimum length; cache reuse accelerates input processing rather than eliminating output generation.

## Manufacturing-Style Requirement Review

| Requirement or step | Origin / accountable decision | Action |
|---|---|---|
| Complete accurate corrected selection | Elliot's product goal | Keep; measure final editor result |
| Preserve formatting and protected literals | Existing product contract | Keep quality gates |
| One remote call on every correction | Current design, not a demonstrated necessity | Challenge through exact reuse, prediction, and local correction experiments |
| Return full text from the model | Current API design | Challenge with prediction first, edit-only output second |
| 50-ms settlement | Prior real-editor failure evidence | Investigate mechanism before reducing |
| Four-minute warm timeout | Implementation choice; no current reuse proof | Replace only after measured idle experiment |
| Synchronous diagnostic formatting | Implementation convenience | Move or delete when its evidence is unnecessary before paste |
| Paste unchanged text and paste into currently active app | Established behavior | Preserve unless Elliot explicitly chooses a new contract |

- Assign an owner for any new performance requirement instead of inventing a historical person responsible for an undocumented choice.
- Delete redundant work before tuning it; accelerate only steps that still need to exist.
- Automate regression gates after the measurement and behavior contracts are correct.

## Test Design and Promotion Gates

1. Freeze current production behavior, model, prompt, replacements, fixtures, build hash, logging mode, and target applications.
2. Add short, paragraph, long, unchanged, protected-literal, Unicode, and rich/native clipboard cases.
3. Measure physical hotkey delivery and human modifier release separately from posted-message replay.
4. Exercise warm use, long idle, sleep/resume, browser focus changes, locked clipboard, and failed network requests.
5. Run randomized or alternating matched blocks with one changed variable; retain warmups and all failures.
6. Report complete-paste p50/p95/p99 when sample size supports it, plus the fraction of all attempts correctly completed under 500 and 1,000 ms.
7. Compare quality, formatting, failure rate, resources, and cost alongside latency; a failed attempt never disappears from the denominator.
8. Validate native-editor payloads and actual ChatGPT/Google Docs/other daily-editor behavior separately.
9. Reject regressions in undo behavior, literal integrity, missing paste, duplicates, or wrong-target delivery.
10. Set a minimum practically useful gain before each experiment and expand sample size when uncertainty overlaps that threshold.

- Use **1,000 ms** as an initial target for short/plain common cases and report rich/long/idle cases separately.
- Budget an illustrative **100 ms local + 400 ms complete remote response** for a 500-ms goal; this is a requirement allocation, not a forecast.
- Recognize that v0.13.2's current **1,419-ms mean request** exceeds both targets before local work begins.
- Recognize that eliminating all currently measured non-request work cannot make that observed mean request sub-second.
- Reserve any claim of sub-500-ms performance for measured final-paste results on a stated workload and percentile.

## Documentation Findings

- Mark `docs/overview.md` sections describing C# as the product, Dev updates through `dotnet run`, and production release packaging remaining on C# as stale relative to current Rust code and release history.
- Treat `docs/architecture.md`'s explicitly labeled C# sections as reference behavior; verify Rust details in source.
- Treat old proxy-era handshake estimates under `.planning/phases/01-persistent-background-server/` as historical assumptions, not current measurements.
- Propose updating those overview sections in the next implementation session; this research session does not alter product code, configuration, or historical evidence.

## Evidence Boundaries

- Fetch the linked external sources on October 22, 2026; source URLs that follow `main` can change after this audit.
- Use source inspection, approved-reader production aggregates, and the portable benchmark verifier; no app restart, clipboard automation, new live correction benchmark, or billable model experiment ran.
- Retain private selected text and raw production responses outside this report.
- Treat every proposed saving as unmeasured until the corresponding experiment passes.
- Reproduce the observation window with `.agents/skills/read-logs/scripts/logs.py --from 2026-10-20 --to 2026-10-22 --channel prod --event spellcheck_detail --json --log-dir C:/Users/2supe/AppData/Local/UniversalSpellCheck.Data/logs`; aggregate the reader's output rather than reading raw logs directly.
