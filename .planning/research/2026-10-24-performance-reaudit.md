# Performance re-audit — October 24, 2026

The dominant measured delay is still remote response completion, not capture or
Rust computation. The largest retained local interval is the intentional 50-ms
paste handoff. There is no evidence of another removable 250–350 ms local delay
on this desktop. Stop before implementing an optimization.

## Current baseline and evidence quality

- Source/release: `711ff377b4915f2df3db37831c50d7144a36974a`, published v0.13.3
  after [PR #8](https://github.com/ElliotDrel/Universal-Spell-Check/pull/8).
  Installed executable hash and sanitized observations are in the
  [new evidence manifest](../../bench/records/2026-10-desktop-latency/reaudit/manifest.json).
- Safely fetched main; it still matches this release. Production remains PID31492
  from its managed installation; settings/key/startup marker presence and hashes
  match the pre-update inventory. No settings, model, transport or paste-delay change.
- Approved-reader export found four Rust 0.13.2 corrections, one historical C#
  1.0.0 correction, and zero 0.13.3 correction records at audit time. Do not pool
  implementations. The four Rust runs had capture median 10.36 ms, paste median
  61.50 ms, and paired `total-request` median 78.29 ms. These sparse pre-fix
  observations are context, not the released version's new live baseline.
- The October 23 controlled settlement blocks are the best available desktop
  path evidence, at the same timer implementation now released. They start from
  a posted hotkey and verify a synthetic native editor. They omit physical key
  delivery/human release and do not establish actual Chrome/Docs rendering latency.
- No fresh idle-desktop window was confirmed during this audit. No focus,
  clipboard, hotkey or original-editor tests ran. A new full hotkey-to-visible-
  replacement baseline remains pending; no timing from another boundary fills it.

## Where the local time goes

Medians below come from separate retained candidate blocks, not pooled data.
Nested intervals overlap their parents. Do not add medians to infer a total.

| Operation | Retained median range | Assessment |
|---|---:|---|
| Editor completion, API replaced with replay | 69.96–71.08 ms | Full synthetic local path, not full live request |
| Coordinator trigger to input API return | 63.65–64.81 ms | Different boundary from editor completion |
| Paste settlement | 50.29–50.37 ms | Largest local interval; minimum stays 50 ms |
| Clipboard capture | 3.05–4.76 ms | Not the historical hundreds of milliseconds |
| Previous clipboard backup | 3.34–3.56 ms | Next notable synchronous operation; required for recovery |
| Copy or paste input injection | roughly 1.6–2.5 ms | Native calls, not interpreter overhead |
| History exclusion | 0.68–1.08 ms | Extra publication before request; preserves Win+V semantics |
| Developer readback, enabled blocks only | 0.90–0.97 ms | Currently precedes the full 50-ms settlement |
| Diagnostic hot-path construction | 0.025–0.028 ms | Bulk persistence already asynchronous |

The fixed local floor is therefore real. Eliminating a 3-ms backup cannot make a
750-ms request complete under 500 ms; this is a budget constraint, not a claim
that small savings never matter. Preserve the timer win and data recovery.

## Fresh v0.13.3 measurements

The installed optimized executable ran compute mode without GUI, clipboard,
network or credentials. Ten frozen cases passed 100 measured trials plus ten
warmups: per-case compute medians 0.035–0.502 ms. Ten manually specified rich
cases, unchanged or one sparse correction, passed 200 measured trials plus ten
warmups. At 20,024/20,027 input characters, medians were 7.96/7.37 ms total and
0.43/0.38 ms rich mapping. This does not cover every dense edit, native Docs slice
or pathological markup shape. Both failed fixture preparations are retained and
excluded from passing performance claims; no product fix was made to pass them.

Six fixed real API requests used saved gpt-4.1 with the existing WinHTTP client.
Each returned exact `This is a test.` in one attempt. Each block is a separate
diagnostic process; neither proves the resident tray client's idle behavior.

| Block | Request totals ms, in order | Connection acquisition ms |
|---|---|---|
| Fresh then reused client | 1479.29, 561.83, 1106.60 | 38.38, 0.017, 0.008 |
| Separately prewarmed client | 720.26, 775.00, 746.93 | 0.016, 0.008, 0.007 |

Native request writing was 0.142–0.186 ms; send API calls were 0.297–3.740 ms.
Send-end to headers-end was 1433.87 ms on the first request and 561.18–1105.93 ms
thereafter: over 96% of each total. HTTP protocol flags were 1 (HTTP/2).
This interval includes remote transit/proxy/service work; it does not isolate
model generation. Sequential blocks are not a randomized warming experiment.
The first request's 38-ms connection acquisition does not explain its 1.48 s.
Even this set's fastest request alone exceeded 500 ms; adding a local median
from another experiment would be an estimate, not a measured full-path result.

## First-principles operation audit

| Step | Keep, question, or deepen measurement |
|---|---|
| Resident worker, session and HTTP/2 connection | Keep; already removes process/model-client startup per correction |
| Modifier release before copy/paste | Keep correctness gate; measure real key hold separately before changing polling |
| Snapshot every prior clipboard format | Keep recovery. Eager GetClipboardData can force delayed rendering; per-format timing is needed only if real backup tails appear |
| Read HTML/native clipboard flavors | Keep; needed for formatting/literals. RTF is diagnostic, but current measured read is ~0.02 ms; removing it is no demonstrated win |
| Exclude captured text from history | Question whether it must precede the request, but overlap has ordering/race costs for only ~1 ms median benefit |
| Literal protection and output validation | Keep; cannot trade exactness for a stopwatch improvement |
| Recreate the whole corrected output remotely | Question first for the next material experiment; most text is often unchanged |
| Rich map and serialization | No current short-text bottleneck. Bounded diff caps exist; longer inputs need separate workload strata |
| Publish, read back, then wait 50 ms | Keep 50-ms minimum. A publication-relative deadline could overlap ~1 ms of readback only when logging is enabled; low-priority bounded opportunity |
| Async disk/evidence storage | Already outside visible delivery. Detail clones/evidence finalization/replacement reload can delay worker readiness after input; timing excludes this tail |

## What the logging still cannot answer

1. Production ends at accepted-trigger to input API return, not physical-key
   activation or visible accepted text. Actual editors need external completion
   checks. The synthetic runner already enforces foreground/selection correctness.
2. Backup has an aggregate timer, without per-format render/duplicate/open times.
   A byte limit after GetClipboardData does not time-bound external rendering.
3. Failure paths retain local request elapsed time but lose individual HTTP phase
   times/attempt diagnostics because only a returned Response carries them.
4. Native queries are deliberately opt-in; ordinary logs have the call/wait split
   but cannot isolate provider time. Warm HEAD requests discard status/timing and
   the 240-second worker wait restarts on messages, so warmth is not a proven SLA.
5. Post-input serialization/evidence scheduling/worker-ready tail is not included
   in reported correction total. Avoid mistaking this for visible paste latency.

Tracked separately: [failed-request diagnostics #21](https://github.com/ElliotDrel/Universal-Spell-Check/issues/21) and [warmup observability #22](https://github.com/ElliotDrel/Universal-Spell-Check/issues/22). The completed timer issue #17 is now closed after release verification.

These gaps justify targeted diagnostics if their workload matters, not another
blanket instrumentation framework. Partial/missing values must remain unavailable.

## Refreshed primary-source research

- [Microsoft delayed rendering](https://learn.microsoft.com/en-us/windows/win32/dataxchg/clipboard-operations): requesting a format can synchronously ask its owner to render it. This supports investigating backup tails; it does not prove one occurred here. It also documents complexity/overhead, so lazy publication is not automatically faster for small text.
- [WinHttpSendRequest](https://learn.microsoft.com/en-us/windows/win32/api/winhttp/nf-winhttp-winhttpsendrequest) can complete synchronously or asynchronously. The [native timing enumeration](https://learn.microsoft.com/en-us/windows/win32/api/winhttp/ne-winhttp-winhttp_request_time_entry) defines the measured boundaries. Do not relabel callback wait as upload or isolated generation.
- [Wispr Flow](https://api-docs.wisprflow.ai/websocket_api) sends audio progressively and explicitly commits completion. [OpenWhispr](https://openwhispr.com/blog/local-streaming-speech-to-text) reuses a local streaming encoder and flushes completed work instead of re-decoding. Transfer the principle of avoiding duplicate work; their stop-to-transcript claims do not measure our hotkey-to-paste interval.
- [Handy's current transcription manager](https://raw.githubusercontent.com/cjpais/Handy/main/src-tauri/src/managers/transcription.rs) manages loaded state and idle unload. Resource retention is already present here. A [Handy clipboard proposal](https://github.com/cjpais/Handy/pull/1770) is an author-reported approach, not verified behavior in this app; a render notification is not proof of complete rich paste.
- [OpenAI latency guidance](https://developers.openai.com/api/docs/guides/latency-optimization) prioritizes avoiding unnecessary generated tokens. [Predicted Outputs](https://developers.openai.com/api/docs/guides/predicted-outputs) supports gpt-4.1 through Chat Completions for mostly known output. That is an endpoint/payload experiment, not a field to add blindly to Responses.
- [Responses WebSocket guidance](https://developers.openai.com/api/docs/guides/websocket-mode) emphasizes many continuation/tool round trips. Its published multi-turn benefit does not establish a win for one independent correction over already warm HTTP/2. Stream previews do not satisfy complete replacement.

## Evidence-ranked next actions and stopping point

1. **Refresh real-editor baseline first**, once the desktop is explicitly idle:
   short/paragraph/long, supported rich and protected literals; retain failed
   setup attempts; measure modifier holds, capture, backup, requested text, exact
   replacement and external completion separately. Keep model/transport/50 ms fixed.
2. **Recommended next material experiment: avoid regenerating known text.**
   Compare current Responses against same-model Chat Completions without prediction,
   then with the protected input as prediction. Alternate order on identical short,
   long/sparse and heavily corrected fixtures; report complete output latency,
   accepted/rejected prediction tokens, cost and exact quality separately.
   Preserve atomic paste and the existing restoration/rich pipeline. Savings are
   unknown: no implementation, API change or claimed speedup is authorized by
   this research result. Stop for the endpoint/payload experiment decision.
3. **If endpoint changes stay out of scope**, investigate real backup/capture tails
   and measurable idle/sleep reconnect delays. Do not implement a warmer cadence
   from these sequential tiny samples. If none exist, say the local ceiling is
   mostly reached under the retained 50-ms requirement.
4. Defer small readback/history overlap, parser tuning, caching, edit-only formats,
   WebSocket rewrites and removing the paste wait. Their present evidence/risk
   ranking is weaker. Never return unchanged text from a heuristic shortcut as
   a substitute for checking grammar.

No product implementation, merge, release, focus test or clipboard test occurred
in this re-audit. Production remains installed and running at v0.13.3.
