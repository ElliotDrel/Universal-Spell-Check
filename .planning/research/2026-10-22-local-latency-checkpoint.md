# Local latency measurement checkpoint — October 22, 2026

## Finding

- Prioritize capture for the first local investigation. Do not pick a deletion or
  optimization until its modifier/copy/wait/read breakdown is measured.
- Keep the earlier audit's other-machine timings as history. The observations below
  were read from this machine's existing log directory; they are ordinary-use
  telemetry, not a new controlled hotkey-to-editor baseline.
- Preserve correction quality, formatting, protected literals, recovery, unchanged
  pastes and active-target behavior. Keep the model, transport and 50-ms settlement.

## Existing on-machine observations

- Freeze the approved reader's October 22 Prod snapshot at 21:35 EDT, covering
  records from 00:30:29 through 21:28:16. Retain the timing-only summary locally in
  `benchmark_runs/local-latency-2026-10-22/pre-instrumentation-summary.json`.
- Count 64 attempts: 62 successful pipeline returns and two failures. All report
  v0.13.2, `gpt-4.1` and developer logging enabled. Do not equate a successful
  pipeline return with verified editor acceptance or semantic correction quality.
- Read records with `.agents/skills/read-logs/scripts/logs.py --log-dir
  <real-LocalAppData>/UniversalSpellCheck.Data/logs --from 2026-10-22
  --to 2026-10-22 --channel prod --event spellcheck_detail --json`.

| Successful-run phase | Mean ms | Median ms | p95 ms |
|---|---:|---:|---:|
| Logged total | 1,932.709 | 1,858.792 | 2,893.072 |
| Capture | 342.035 | 209.663 | 905.570 |
| Request | 1,516.245 | 1,404.347 | 2,454.861 |
| Paste through input return | 69.545 | 66.523 | 89.438 |
| Replacements | 0.075 | 0.056 | 0.144 |

- Use medians from the middle pair and p95 from sorted index `floor((n-1)*.95)`
  in this observational summary. Keep all 64 attempts in reliability counts; phase
  statistics condition on the 62 successes. Do not add phase percentiles.
- Attribute approximately 78% of mean logged total to the request boundary. Do not
  label all of it AI generation: transport/setup/server work are not separated.
- Observe roughly 4.8 ms of mean residual outside the old major buckets. This
  bounds their aggregate *mean* unmeasured work in this snapshot, not a particular
  operation's cost in individual runs. The new timers expose that attribution.
- Treat capture's long tail as evidence to investigate, not proof that clipboard
  format reads are expensive. Human modifier release is still inside capture.
- Do not spend the first optimization on replacements. Its measured mean is less
  than one tenth of a millisecond.

## Implementation and measurement limits

- Add monotonic operation timings with retry accumulation and partial failure/panic
  retention. Keep unexecuted stages null; retain existing timing names.
- Separate dispatch, setup, backup, capture, history exclusion, input preparation,
  request, replacements, rich mapping, diagnostics and paste. Expose the residual.
- Separate capture modifier release, copy injection, message-aware waits and format
  reads; separate paste writing, diagnostic readback, settlement, modifiers and input.
- Retain the full coordinator timings in the existing native-editor benchmark and
  stamp its manifest with commit/dirty state and creation time.
- Keep app receipt/input return distinct from physical key delivery, editor paste
  completion and visible rendering. The native fixture acknowledges complete text
  replacement, but does not verify browser rendering or physical modifier behavior.
- Correct the overview's stale C# build/product/packaging guidance and mark the old
  mandatory three-PR plan superseded by the smaller approved measurement checkpoint.

## Requirement review before optimization

| Operation | Why it exists | Decision at this checkpoint |
|---|---|---|
| Clipboard backup/recovery | Preserve existing clipboard after failure | Keep; measure backup cost separately |
| Modifier-release wait | Avoid incorrect copy/paste chords | Keep; distinguish human hold time from app work |
| Unicode, HTML and native Docs reads | Supply correction and preserve rich formatting | Keep; measure format-specific waits |
| RTF read | Feed diagnostics/evidence; not consumed by the Rust rich mapper | Candidate for removal/conditional collection if measured cost warrants changing diagnostic coverage |
| Format-name enumeration | Explain diagnostic completeness and offered formats | Question its need before request/paste; measure first |
| Metadata/string/base64 serialization | Retain diagnostic evidence | Candidate to defer where measured and safe |
| Clipboard history exclusion | Keep captured incorrect text out of clipboard history | Preserve existing behavior; measure the extra write |
| 50-ms publication interval | Retained after real-editor failures at zero delay | Keep; do not optimize it in this batch |

- Attribute quality, formatting, recovery and paste-behavior requirements to Elliot's
  explicit instructions. Do not invent a historical owner for implementation details.
- Verify the RTF dependency in `rust/src/runtime.rs`: it is copied to detail metadata;
  `rich::rewrite` receives only HTML, text, correction and native bytes. That establishes
  its purpose, not its latency or the acceptability of losing diagnostic evidence.

## Verification and remaining decision

- Pass the portable archive verifier: 17 historical blocks, 2,000 attempts including
  warmups, and 22 startup launches. This verifies history, not this machine's speed.
- Retain an initial disk-full linker failure and rerun checks with debug symbols and
  incremental compilation disabled; preserve normal release optimization settings.
- Measure the exact optimized timing helper in three isolated 200,000-span loops:
  215–245 ns per wrapped no-op including loop/accumulation overhead. This is an
  instrumentation sanity check, not measured end-to-end app overhead.
- Complete the final check results in the PR verification section.
- Require explicit idle-desktop authorization before running the prepared local
  replay blocks with diagnostics off/on and physical-hotkey checks. No new desktop
  test or billable correction has run in this session.
- Keep exact capture attribution, new measured baseline and the first optimization
  choice pending those runs. Do not claim the requested measurement checkpoint is
  fully complete before that evidence exists.

## Evidence-ranked next step

1. Run the instrumented local replay baseline and deliberate physical-hotkey/editor
   checks. Determine whether capture waits on the human, source app, clipboard open,
   or a specific format; compare diagnostic modes separately.
2. Delete or move work only if the breakdown demonstrates it is unnecessary before
   paste. Investigate the mechanism if an OS/source wait dominates instead.
3. Inspect the request boundary separately if the full sub-second target remains the
   priority: its existing mean alone exceeds one second. Do not infer a transport
   switch is the answer from the current send/wait labels.


## October 23 desktop attempt — baseline still pending

- Use Elliot's explicit five-minute idle-desktop authorization for three bounded
  offline attempts. All abort during setup before a posted hotkey; retain zero
  measured trials and zero warmups. Diagnostics-on never starts.
- Preserve initial, focus-observation and activation attempt directories under the
  existing ignored checkpoint directory. The final activation returned success,
  but the fixture still reported its combined foreground/selection failure.
- Add setup-only diagnostics before claiming a cause. Preserve the strict guard;
  do not discard the failed attempts or weaken the correctness prerequisite.
- End desktop work and notify Elliot. Treat his notification acknowledgment as an
  acknowledgment only, not renewed desktop authorization.
