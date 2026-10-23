# C# / Rust Baseline

The portable historical measurement record is committed at
[`bench/records/2026-10-rust-port/`](../bench/records/2026-10-rust-port/README.md).
It includes synthetic cases/responses, canonical per-attempt samples, sanitized
manifests, startup measurements and a verifier. Run its `verify.py` to check the
record without local binaries or desktop access. Original ignored packages below
remain optional local replay material; their superseded `BEFORE` snapshots were
removed after curation. New completed benchmark checkpoints should commit a
bounded record with attempts, failures, provenance and verification, excluding
private logs, credentials, browser profiles and generated executables.

## Credential isolation incident and completed recovery

The October 16 offline harness originally supplied a custom settings directory
without overriding the key path. The old SettingsStore default still pointed
the key path at production, so SaveApiKey replaced the saved active key with the
offline placeholder. Live testing was stopped; the installed process was kept
running with its cached key. After Elliot explicitly approved read-only recovery,
the cached field was recovered on October 16, validated with HTTP 200, and restored
to DPAPI-encrypted storage. Encrypted readback verified the recovered value and
all other key-store entries and metadata. No dump or plaintext key file was created;
the app remained running. The encrypted pre-recovery file was preserved.
Do not execute older archived benchmark clients: their entry points are quarantined.

The repair derives omitted key paths from the configured data directory, passes
both paths explicitly in the harness, and rejects the placeholder before any live
request. Tests assert destinations before writes and verify that an offline smoke
leaves the production encrypted file byte-identical. Credential recovery, the live
desktop baseline and ten startup measurements are complete. The installed app was
restored; encrypted credentials and settings were unchanged by the completed runs.

## Scope

The October 16, 2026 harness replaces the old benchmark. Use Release builds and
`bench/baseline-cases.json`: ten fixed public synthetic cases covering short,
paragraph, long, unchanged, replacements, protected literals, Unicode, blank lines,
bold/link HTML and list HTML. Do not regenerate this file from changing production
logs. Version a new case file if the workload changes.

| Mode | What runs | Desktop required? |
|---|---|---|
| `headless` | Real coordinator, live API, protection/post-processing; no capture, target hooks or rich mapping | No |
| `compute` | Real protection, payload construction, response parser, post-processing and rich mapper with recorded AI output | No |
| `local` | Registered hotkey message/coordinator/capture/formatting/paste; only HTTP transport replaced with recorded output | Yes |
| `e2e` | Registered hotkey message/coordinator/capture/formatting/live API/paste | Yes |
| `self-test` | Measurement invariants and real-mapper fixture checks | No |

`compute` is a component measurement, not a substitute for the full local pipeline.
The replay response envelope is synthesized from recorded model text; it is not a
byte-for-byte replay of the complete server response. Replay has no network transport.
Offline modes use a dummy key in isolated run-local settings, never the user's key.
Desktop modes accept `--developer-logging` to match the actual production setting;
otherwise developer evidence is disabled. Record and match that flag across languages.
The only product change is optional HTTP-handler injection; the normal handler and
product path are unchanged.

## Run

From the repository root, use the SDK selected by `global.json`:

```powershell
dotnet build bench/UniversalSpellCheck.Bench.csproj -c Release
dotnet bench/bin/Release/net10.0-windows/UniversalSpellCheck.Bench.dll --mode self-test
dotnet bench/bin/Release/net10.0-windows/UniversalSpellCheck.Bench.dll --mode headless --runs 10 --warmup 2 --output bench/results/csharp-api-a
dotnet bench/bin/Release/net10.0-windows/UniversalSpellCheck.Bench.dll --mode compute --fixtures bench/results/csharp-api-a/fixtures.json --runs 20 --warmup 2 --output bench/results/csharp-compute-a
```

Live mode reads the current saved model and protected API key. `--model` overrides the
model explicitly; use `--settings` to select the real production data directory if a
sandbox redirects LocalAppData. Never put a key in a command or result file.
Each case has its own excluded warmups. Ten cases with ten runs and two warmups
make 120 correction requests, before service retries. There is no parallel request
load. First-use warmups are retained in raw output rather than silently discarded.
Check free output-volume space before running and monitor it between blocks. The
October 16 live attempt hit disk-full after 50 successful measured replacements;
retain that incomplete block as diagnostics, including potentially truncated
summary/fixture files. Its successful full restart began with at least 32 MiB
free. Free substantially more space before compiling the Rust port.

Desktop tests MUST wait until Elliot has stopped using the computer. Preparing the
command or building the runner does not authorize running it during active use.
Both desktop modes reject invocation without `--allow-desktop`:

```powershell
dotnet bench/bin/Release/net10.0-windows/UniversalSpellCheck.Bench.dll --mode local --fixtures bench/results/csharp-api-a/fixtures.json --runs 20 --warmup 2 --allow-desktop --output bench/results/csharp-local-a
dotnet bench/bin/Release/net10.0-windows/UniversalSpellCheck.Bench.dll --mode e2e --runs 10 --warmup 2 --allow-desktop --output bench/results/csharp-e2e-a
```

The benchmark registers Ctrl+Alt+B, separate from Prod/Dev. Its measured trigger
posts WM_HOTKEY to the real registered window and requires an explicit dispatch
acknowledgment within two seconds. This measures queue/handler-to-editor behavior,
not physical key delivery or a human's modifier-release delay. The earlier SendInput
block was retained as incomplete diagnostics after a missing coordinator record;
do not combine its totals with the acknowledged-message baseline. Native message
dispatch is checked 100 times by the offline harness suite.

The visible editor allows
up to three seconds of unmeasured activation/selection preparation, then verifies
foreground, full selection and fixture text before injection. A normal STA
`Application.Run` message loop replaces the old `DoEvents`/sleep polling loop.
The textbox observes completion of WM_PASTE or the native Ctrl+V character command,
including unchanged-text replacement,
and checks the complete resulting text against the actual requested `paste_text`.
Only CRLF/LF normalization is allowed. Missing telemetry, paste receipt, wrong text,
lost foreground or mismatched requested clipboard HTML fails the trial. Unsafe
fixture/telemetry failures stop the block; completed product failures remain in
the sample and may continue only with foreground retained and a fresh verified
selection. Never silently retry or exclude a failed attempt.

Developer-evidence completion is awaited by run ID outside the timed interval
before preparing another trial, so asynchronous clipboard readback and after-paste
capture cannot overlap the next case. Rich sources must supply the exact fixture
CF_HTML; local replay must match the frozen requested HTML as well as editor text.
Exit codes: `0` complete success, `1` completed with failed trials, `2` fatal setup,
`3` safety abort. Initial fixture diagnostics remain saved separately.

Rich fixtures provide synthetic CF_HTML on copy, exercising the production mapper
and rich clipboard writer. The textbox verifies text and clipboard HTML, not visual
bold/link/list rendering in a browser. Original-editor acceptance remains separate.
Desktop runs change focus and leave corrected text on the clipboard, like the app.
The rich source publishes Unicode/UTF-8 CF_HTML with bounded native clipboard calls;
it must not use an OLE flush inside the test editor's copy handler. A discovered
source-side Clipboard.SetDataObject exception previously escaped the coordinator
and left it waiting; those attempts remain diagnostics. UI exceptions now fail the
current trial explicitly instead of opening an unobserved WinForms error dialog.
The registered benchmark window also supplies the real clipboard-history owner,
so the production history-exclusion step runs. The harness has no loading overlay,
tray, updater or dashboard; production observations remain separate context.
Output directories must be empty; existing evidence is never overwritten.

## Results and interpretation

Every result directory contains:

- `manifest.json`: commit/dirty state, product/harness/case/replacements/fixture hashes,
  OS/runtime/architecture, stopwatch frequency, model, prompt, options and boundaries.
- `trials.jsonl`: every attempted warmup and measured trial, with timings, failures,
  complete output and exact expected-text comparison.
- `summary.json`: attempted/succeeded/failed counts and mean/median/p95/min/max overall
  and per case. Statistics describe successful attempts; failures remain explicit.
- `fixtures.json` (live modes): the first successful model output per case, including
  warmup, plus the resulting production text/HTML reference for deterministic replay.
- `spellcheck-YYYY-MM-DD.jsonl`: normal coordinator diagnostics. Read with the
  `read-logs` skill and `--log-dir <result-directory>`.

Live `quality_match` is exact agreement with the hand-written expected text, not a
complete semantic judge. Legitimate wording variations require review. Success
requires the production pipeline to succeed; offline replay additionally requires
exact frozen output/mapping. Frozen output is a behavior reference, not proof that
every existing behavior is desirable. The original expected case text remains intact.

Compare directories with `python bench/baseline_compare.py <before-directory> <after-directory>`.
The comparison rejects mismatched workloads/settings or incomplete coverage. It
reports failures and successful-only timing distributions together; an existing
product failure must not prevent analysis of whether Rust improves reliability.
It never awards a passing verdict based on speed alone.

`total_ms` is call-to-headless-return, compute start-to-map-return, or synthetic
hotkey injection-to-editor-WM_PASTE completion, depending on mode. Production phase
times remain whole milliseconds; component and external timings use Stopwatch ticks.
Keep zero samples. Null denotes an unmeasured/inapplicable phase; historical
`payload_ms` and `parse_ms` zeros are placeholders and are converted to null.
Compute `replay_service_ms` includes real payload/parser work and fake transport;
it is never reported as API latency. Logging finalization is awaited between trials
but is outside external total measurements. The manifest records effective developer
evidence logging and the saved setting separately; headless/compute do not capture evidence.

Before porting, copy the exact Release binaries and source archive to a durable
ignored `benchmark_runs/` directory, retain the case/replacements files, and checksum
the package. Keep results outside disposable worktrees. Never commit real run artifacts.

After porting, rerun C# and Rust on the same machine with identical fixtures/settings.
Alternate C#/Rust/Rust/C# blocks, preserve all failures and inspect per-case order
effects. Report milliseconds saved and percentage/speed ratio separately for local
and full E2E. Neither a 5% threshold nor a fixed sample count establishes statistical
confidence. Use a modest first run and repeat when variability makes the result unclear.

The old `compare.py`, `check_correctness.py` and autoopt documents target the legacy
schema; they are historical and must not be used to validate this baseline.

## Startup

`bench/measure-startup.ps1` uses the existing `--startup-smoke` path to measure ten
standalone Release process starts through the `started` log, after real hotkey and
tray/context initialization. It temporarily stops the exact installed process to
release its hotkey, then restores that executable in `finally`. It refuses missing
first-run registration state and ambiguous processes. Run only in the authorized
idle-desktop window. Pass the frozen executable, installed executable, production
data directory and fresh output directory explicitly.

Read ready milestones through the log-reader CLI, matched by PID and lifetime.
Retain process-through-exit timings separately; they include smoke teardown and
are not startup readiness. Cross-process readiness uses wall-clock timestamps;
filesystem caches remain warm, and installed update checks/dashboard rendering
are excluded. Preserve the first launch separately rather than claiming cold boot.

## October 16, 2026 checkpoint

- Preserve the local package under the primary checkout's ignored
  `benchmark_runs/rust-baseline-2026-10-16/` directory.
- Record 100/100 measured live headless successes plus 20/20 successful warmups:
  API median 843.5 ms, p95 1,891.3 ms, mean 1,026.54 ms.
- Retain six measured list outputs with two extra spaces before LF; 94/100 outputs
  match the hand-written expected text exactly, and all six differences were checked.
- Use `compute-final-a` and `compute-final-b`: each passed 200 measured and 20 warmup
  frozen-output/mapping checks; total medians 0.50595/0.54315 ms and p95 1.593775/1.871985 ms.
- Mark earlier `compute-a`/`compute-b` superseded: their fake transport unnecessarily
  rebuilt all fixture protection maps inside the timed path; retain those artifacts.
- Keep the 30-run production observation separate: v0.12.3, October 15-16, developer
  logging enabled, 27 successful and three failed; logged successful total median
  2,625 ms and p95 8,464.8 ms do not prove verified editor replacement.
- Preserve `desktop/local-native-final`: 98/100 measured verified editor replacements,
  two clipboard-write failures, median 370.3875 ms, p95 6,178.29305 ms and mean
  1,899.12704 ms among successful attempts; warmups were 19/20 successful.
- Use the acknowledged WM_HOTKEY boundary and native rich-copy producer for that
  local block. Retain earlier incomplete fixture diagnostics separately.
- Exclude `desktop/e2e-native-final` from latency analysis: invalid placeholder
  authentication caused HTTP 401 responses before testing was stopped.
- Use `desktop/e2e-space-verified-final` as the completed live desktop block:
  100/100 measured verified replacements and 20/20 successful warmups; total median
  996.8639 ms, p95 2,118.2642 ms and mean 1,164.046523 ms.
- Retain 95/100 exact expected-text matches; the five measured and two warmup
  mismatches were checked and differ only by two spaces before the first list newline.
- Preserve API/capture/paste phase medians of 722/108/150 ms from that live block;
  do not add phase medians or subtract medians from separate blocks.
- Preserve all 120 live developer-evidence completions as `storage_limit`; match
  this condition or explicitly record its change for the Rust comparison.
- Exclude `desktop/e2e-recovered-final`: disk-full interrupted it after 50 successful
  measured trials. No failures or partial trials from it are folded into the restart.
- Preserve `startup-final`: ten successful readiness samples, median 575.4677 ms,
  p95 708.828935 ms and mean 516.40215 ms; first launch 503.9401 ms, with no cold-cache claim.
- Read the completed package's `README.md`, `baseline-completion.json` and
  `post-run-integrity.json` for validation and restored installed-app checks.
- Treat the C# baseline as complete. Original-editor visual acceptance remains
  separate; the installed-app memory figure is one aged-process snapshot, not a
  repeated baseline. Pure text processing is about 0.5 ms, so a language change
  alone does not establish a meaningful end-to-end speedup.

## Rust candidate and comparison

- Build the candidate with `cargo build --manifest-path rust/Cargo.toml --release --no-default-features`.
- Keep the installed C# app and the frozen package unchanged while validating the candidate.
- Run `cargo test --manifest-path rust/Cargo.toml` for native mock HTTP/deadline, key isolation, update-queue, activity-reader, statistical, 76 C# golden formatting and ten frozen-workload regressions.
- Run `rust/tests/parity.py` to re-record the real C# formatting suite and compare its outputs with the Rust candidate. Its generated oracle and reports belong in ignored `benchmark_runs/` directories.
- Use `--bench-compute` for the same protection, payload serialization/parser, frozen-response lookup, response parser, postprocessing and rich-mapping boundaries as C#.
- Use `--bench-desktop --mode local|e2e --allow-desktop` only during an explicitly authorized idle-desktop window.
- Require explicit `--settings` for live keys; offline replay never creates a live HTTP client or reads/writes production keys.
- Require `--evidence-log-dir` with developer logging so the store/quota condition can match C#; do not silently compare a fresh private evidence store with the full shared reference store.
- Match `hotkey_trigger=posted_registered_message`; Rust records dispatch acknowledgment and the native editor procedure's actual paste completion separately.
- Retain every attempted warmup and measured trial, including aborted trials, editor/HTML mismatch, clipboard failure, missing dispatch and evidence timeout.
- Treat native editor text/CF_HTML checks as payload acceptance; verify real browser/editor rendering separately.
- Run `bench/compare-rust.ps1` for C#/Rust/Rust/C# block ordering and before/after settings and encrypted-key integrity checks.

```powershell
./bench/compare-rust.ps1 -Mode compute `
  -RustExe <Rust-release-executable> `
  -BaselinePackage <primary-checkout>/benchmark_runs/rust-baseline-2026-10-16 `
  -Dotnet <portable-sdk>/dotnet.exe `
  -DataDirectory "$env:LOCALAPPDATA/UniversalSpellCheck.Data" `
  -Output <fresh-ignored-output-directory>
```

- Add `-Mode local -AllowDesktop` or `-Mode e2e -AllowDesktop` only after the user has confirmed the desktop is idle.
- Use the existing startup measurement helper for matched standalone starts; it stops/restores only the explicitly named installed executable and reads ready milestones through the approved log reader.
- Keep desktop and startup comparisons pending until they have actually run; compilation and golden tests do not establish an end-to-end speedup.

### Rust offline checkpoint, October 17, 2026

Sequential `compute-abba-02` used the frozen safe C# runner and optimized Rust candidate on the same machine, ten cases, 20 measured and two warmups per case. All four blocks passed 200/200 measured and 20/20 warmup frozen-output/mapping checks. The identical rich-list fixture retains two spaces before LF, so each block has 180/200 matches to hand-written expected text.

| Block | Median total ms | p95 total ms |
|---|---:|---:|
| C# A | 0.26205 | 1.11392 |
| Rust A | 0.11835 | 1.208485 |
| Rust B | 0.09715 | 2.255075 |
| C# B | 0.26515 | 1.135035 |

These component medians improved 2.21–2.73 times; Rust tail latency did not improve. This establishes neither an end-to-end speedup nor statistical significance. All 131 baseline hashes, settings and encrypted key hashes were unchanged. Final Release differential formatting passed 76/76 original C# cases.

Exclude `compute-abba-01`: the initial PowerShell wrapper did not wait for GUI-subsystem native executables, allowing possible overlap. The repaired wrapper explicitly waits for each process and drains stdout/stderr concurrently. Keep the failed-method diagnostics. Desktop, startup and visual acceptance were pending at this earlier checkpoint; the completed results below supersede that status.


### Final Rust desktop/startup checkpoint, October 17, 2026

User-authorized idle-desktop testing completed. Canonical ignored artifacts are under primary `benchmark_runs/rust-port/`; preserve `README.md`, `final-results.json`, raw trials, manifests, failures and integrity reports. The measured optimized candidate is `candidate-prod-final`, app code through `3f60b70`, executable SHA-256 `541AB7110BFB2E8C030B6D1C74567B4FC0CE5761502095B4BD30328A4A08E27E`.

Each desktop block used ten cases, three measured trials and one warmup per case in C#/Rust/Rust/C# order. Every block passed 30/30 measured replacements and 10/10 warmups, verified against actual native editor completion. All 320 developer-evidence completions reported `storage_limit`, matching the reference store condition. Expected-text differences were only the known two rich-list spaces before LF.

| Mode / block | Median total ms | p95 total ms |
|---|---:|---:|
| Local C# A | 256.634 | 271.355 |
| Local Rust A | 89.136 | 99.423 |
| Local Rust B | 87.026 | 97.135 |
| Local C# B | 262.748 | 269.932 |
| Live C# A | 1094.421 | 1736.269 |
| Live Rust A | 888.976 | 1448.907 |
| Live Rust B | 838.941 | 1494.596 |
| Live C# B | 1079.575 | 1485.474 |

Local median ratios are 2.88/3.02. Live ratios are 1.23/1.29, or 18.8%/22.3% less elapsed time. Live p95 improved in pair A and was slightly worse in pair B. These are observed block differences, not significance tests; API variation cannot all be attributed to the port.

`startup-abba-final-02` used three launches per block, six per implementation: median C# A/B 596.056/636.925 ms, Rust A/B 80.587/75.606 ms. The process-to-ready boundary excludes installed update checks and automatic dashboard rendering; caches remain warm. Preserve first launches separately.

`startup-abba-final` is an excluded helper diagnostic: implicit JSON DateTime conversion lost fractional seconds during reparse and rejected a valid same-second readiness event. Preserve ISO strings with `ConvertFrom-Json -DateKind String` before `DateTimeOffset.Parse`.

`compute-abba-final` passed all 200 measured frozen-output/mapping checks and 20 warmups per block. C# A/B medians 0.28190/0.23405 ms and p95 1.18511/1.301635 ms; Rust A/B medians 0.09175/0.14525 ms and p95 1.17747/1.27845 ms. All blocks retain 180/200 handwritten expected-text matches because of identical frozen list spacing.

Keep the original 50 ms clipboard publication settling interval. Zero-delay `local-abba-01` speed claims are superseded after live paste failures; incomplete `e2e-abba-01`, `e2e-abba-02` and focused diagnostics are excluded. Moving developer readback before injection removed one contention risk but did not alone resolve all failures. Forty focused settled live attempts passed before final matched runs; their timings overlap compilation and are not canonical.

Final Dev/Prod configurations each pass 19 unit tests and one GDI integration test. Format, both Clippy configurations and all 76 optimized C# formatting parity cases pass; upstream vendor warnings remain. Native Chrome acceptance includes physical hotkey, plain/unchanged paste, an instrumented formatted copy producer, no-selection and overlap guards. It does not prove original Google Docs/ChatGPT rendering or OS clipboard-history retention, and fixture elapsed fields include observation delays.

All 131 frozen baseline hashes, settings and encrypted credential bytes remain unchanged. Installed C# v0.12.3 is restored; task browser windows/server are closed. No Rust installation, publication or installed updater validation occurred. Future focus/clipboard tests require a new idle-desktop authorization.


### Completed original-editor and private updater acceptance, October 18, 2026

- Completed the authorized idle-desktop cycle with reviewed candidate SHA-256 `762b007961e6baf956b2162fe23012c66a11a7f3d0e4508c9b96baa0d7e2c598` and frozen safe C# control. Retain the earlier speed results and measured binary unchanged.
- Passed all eight C#/Rust editor/case parity comparisons: four synthetic cases each in actual Google Docs and the ChatGPT composer. Read back native Docs text/styles/hrefs/heading IDs/list nesting and actual ChatGPT rendered/copied-back content; all four ChatGPT after-text/HTML pairs are byte-identical. Rust logged four `docs_native` and four `aligned_html` successes.
- Record 14/16 strict fixture runs, rather than an unqualified 16/16: both implementations preserve ChatGPT rich-link chip labels verbatim, leaving `lnik` inside the chip while correcting normal text. The href, bold, italic and paragraph structure survive. This is inherited behavior, not a Rust regression; the original handwritten link-label correction requirement is still unmet for that editor.
- Passed private installed updater current/no-update, manual download, offline cached v1-to-v2 apply, damaged-v3 rejection with v2 intact, automatic v2-to-v3 launch apply, same-version restart and uninstall. Verify exact package hashes/PIDs, real Settings state and approved-reader milestones. The private QA uninstall entry and payload are gone, with fixtures and production settings/key/startup unchanged.
- Restored exact installed C# Prod v0.12.3, PID 27476 at closeout. Closed both test browser windows and cleared the unsent composer; retained only the synthetic Google Doc and local evidence. No production installation/publication occurred. Require fresh idle authorization for another interactive test.
- Preserve primary `benchmark_runs/rust-port/acceptance-prep/acceptance-result.json` and `verify-editor-evidence.py`, Docs snapshots, copied-back HTML/text, screenshots, approved-reader runtime exports, updater action reports, approved-reader `updater-runtime.jsonl` checksum/start milestones and final integrity status. The verifier computes strict text totals from the unchanged fixture expectations and asserts these retained QA milestones; visual appearance and GUI sequencing remain separately reviewed observations. Initial focus/selection/control-ID failures remain excluded diagnostics, not hidden application successes or regressions.
- Treat this as acceptance, not a timing run. Private QA does not validate public release publication or migration from installed C# to Rust; a four-hour periodic wait and the full race-condition checklist were outside this MVP cycle.

### Original-editor and installed-updater acceptance rerun playbook

The following preparation/run sequence was executed October 18. Its pre-run state is historical; the completed verdict above is current. Require a new idle authorization before repeating it.

- Stop before desktop control. Elliot asked to prepare everything and end the turn before testing. Require his next explicit `Run` before browser navigation, focus, clipboard, app/installer launch, update application or uninstall.
- Continue in `codex/rust-port`. Use primary `benchmark_runs/rust-port/acceptance-prep/` for retained packages, results and local tooling. Keep the earlier measured `candidate-prod-final` and its results unchanged; the reviewed editor candidate includes later fixes and has not had original-editor acceptance yet.
- Read `bench/fixtures/editor-acceptance.json` for four synthetic cases: plain text with protected URL/path, inline bold/italic/link, heading/nested list, and unchanged formatted content. Compare the frozen safe C# product and reviewed normal Rust candidate in the actual Google Docs editor and ChatGPT composer.
- Create a private acceptance folder and a new Google Doc in it after authorization. Use a fresh ChatGPT composer in a task-owned tab; never submit a chat message or overwrite an existing draft. Seed only synthetic fixture text. Verify the seeded editor state first; initial editor sanitization is a control limitation, not automatically a Rust regression.
- Select actual editor text and use physical hotkeys. Capture source and corrected text/HTML/native Docs formats, before/after views, exact link targets, heading/list nesting and paragraph boundaries. A `success` log or matching clipboard bytes alone does not pass visual acceptance. Read runtime events only through the approved log-reader CLI.
- Run C# controls for any formatting difference. The prior C# partial-start nested-list rebasing limitation remains inherited; do not claim the Rust port fixes it. Rebuild/relaunch after any product fix, then repeat affected cases. Preserve all failures and raw evidence.

#### Isolated updater setup

- Build with `python bench/prepare-rust-acceptance.py --root <fresh-acceptance-root> --vpk <extracted-net10-vpk.dll> --dotnet <portable-dotnet.exe>`. This runs non-desktop tests/lint, builds three QA prereleases and a normal editor candidate, and packages without executing the app (`--skipVeloAppCheck`). It does not install anything.
- Keep `vpk` pinned to 1.2.161, matching the Rust/C# libraries. A private Windows/.NET 10 extraction is recorded in primary `acceptance-prep/tools/extraction.json`. The normal full tool install hit disk pressure; the selected extraction and source-package hash are retained. Neither tool nor packages are published.
- Keep the normal C# production release workflow unchanged. Manual `vpk pack` here builds only the distinct `UniversalSpellCheck.Acceptance` package, never a production package or tag.
- Use the compile-time `acceptance` feature with `--no-default-features`. Bind it to `USC_ACCEPTANCE_ROOT` and one of the whitelisted `USC_ACCEPTANCE_VERSION` values `0.13.0-acceptance.1`, `.2`, `.3`. Keep every version bound to the same absolute root. Ordinary builds cannot select a private feed through runtime environment variables.
- Derive the QA mutex, package ID and display name from `src/BuildChannel.cs`. Suppress correction hotkey registration and login-startup registry writes in QA. Validate canonical executable, data/log directories, feed and installed updater paths before bootstrap; validate the installed package identity. Use fresh ordinary directories and reject paths escaping through junctions.
- Store settings and the synthetic DPAPI key outside `<root>/install`, under `<root>/localappdata`. Never copy real API credentials into QA. Compare fixture hashes against the frozen seed and persist the first authorized action's production settings/key/login-startup baseline across the whole cycle. A per-action before/after comparison alone misses asynchronous changes between invocations.
- Expect a real private Windows uninstall entry. Package shortcuts are explicitly `None`; an environment override alone does not sandbox installer registry effects. Use the private installer with an explicit directory and silent mode, not the production installer.

#### Authorized updater run sequence

- Invoke `python bench/rust-update-acceptance.py --root <root> <action> --allow-desktop`; mutations refuse to run without the flag. `status` and `verify` are read-only. The flag is a mechanical guard, not a substitute for Elliot's fresh authorization.
- Publish version 1 with `publish --version 1`, then `install --version 1`. Install only beneath `<root>/install`; silent setup does not launch the normal GUI. Start it with `start`, inspect Settings update state and verify the exact installed executable/version.
- Publish version 2. Use the real Settings or tray Check action, wait until download is ready, and record the cached package hash. Run `offline` to hide only `releases.win.json`, keeping the feed directory present. Click Restart to update. Require version 2 to start from its exact prepared binary despite the unavailable feed; this exercises the repaired cached-update path. Restore the index with `restore-feed`.
- Publish version 3, then `corrupt --version 3`. Use the real check action and require failure notification with version 2 still usable, unchanged installed binary and preserved data. Republish version 3 to restore its valid bytes.
- Quit QA and start version 2 with the valid version 3 feed. Require launch check/download/apply/restart into version 3, matching its manifest and executable hash. Run `verify --version 3`. Also exercise Restart when no update remains and require a new PID at the same version.
- Inspect all ready/download/failure/restart milestones through the log reader, matching PID/version. GUI and helper invocation success do not establish update completion. Periodic checks share the same worker; a real four-hour wait is not part of this MVP cycle.
- Uninstall only QA with `uninstall` after evidence capture. The helper rejects protected backup names, stops only the exact QA process, and invokes only that installation's updater. Verify the QA uninstall entry, process and install payload are gone while retaining data/results/source packages and all backups.
- Stop only task-created editor candidates/windows, restore the exact installed C# executable if it was stopped, and verify its running version plus byte-identical settings/encrypted key and unchanged login startup entry. Preserve the private synthetic Google Doc as evidence; leave the ChatGPT composer unsent and clear its test draft before closing.

Installer argument sources: [Velopack Windows setup CLI](https://docs.velopack.io/reference/cli/content/setup-windows), [Windows updater CLI](https://docs.velopack.io/reference/cli/content/update-windows). The private feed tests installed package behavior, not public GitHub publication or the production C#-to-Rust release transition.

### Public Rust release transition, October 18, 2026

- Published v0.13.0 from commit `2f21440` after release CI and independent Dev/Prod checks passed. The release workflow builds locked production Rust, preserves the old package identity/feed, validates native version metadata, and actually installs/launches the generated installer before upload.
- The laptop's existing installed C# v0.12.3 accepted its built-in `--restart` request, restarted, downloaded/applied v0.13.0 and launched Rust PID33684. A second native Rust restart launched PID11784 and completed a successful public-feed `Up to date` check.
- Verified installed executable and package manifest version 0.13.0, unchanged settings/encrypted keys/login startup, and removal of the old C# DLL from the active payload. No manual package apply, data deletion or app patch release was needed.
- Retain primary `benchmark_runs/rust-port/production-release/` release/CI metadata, integrity snapshots, approved-reader upgrade events and completion report. This validates one current C# installation transition plus CI fresh install; it is not a new speed run or a guarantee about every older configuration.


## Local latency measurement checkpoint

Use the existing Rust desktop runner; a new harness is unnecessary. Build the
instrumented branch in Release. Keep all output in a fresh ignored directory and
record whether developer evidence is enabled. Do not execute desktop commands
without Elliot's explicit idle-desktop window.

The raw `trials.jsonl` now retains complete `coordinator_timings`, its schema and
boundary, including partial timings for product failures. Its existing `timings`
also retains new top-level fields, but `total_ms` remains the external
posted-hotkey-to-native-editor-completion duration; `coordinator_total_ms` remains
trigger-to-input-return. Nested capture/paste durations stay in
`coordinator_timings`. Never sum both layers or infer editor time by subtracting
percentiles from different trials. Native editor completion can occur before the
input API returns, so their difference is not universally an editor-wait duration.
The desktop manifest now records commit, dirty state and UTC creation time in
addition to binary/workload/replacements hashes and machine identity.

Prepare these commands from the instrumented worktree root. PowerShell should use
`Start-Process -Wait -PassThru` or a waiting subprocess wrapper for the GUI binary.

```powershell
# Noninteractive: validate the portable historical record.
python bench/records/2026-10-rust-port/verify.py

# IDLE DESKTOP APPROVAL REQUIRED: 30 measured trials + ten retained warmups.
# No API requests; both settings and credentials remain isolated by local mode.
# Replace <candidate.exe> and <fresh-output> with absolute paths.
<candidate.exe> --bench-desktop --mode local --cases bench/records/2026-10-rust-port/cases.json --fixtures bench/records/2026-10-rust-port/fixtures.json --replacements replacements.json --runs 3 --warmup 1 --allow-desktop --output <fresh-output>
```

- Run the first block without developer logging; use a second matched block with
  `--developer-logging --evidence-log-dir <fresh-private-evidence-directory>` to
  expose diagnostic cost. Keep the directory separate from ordinary production
  evidence and record that its fresh-store condition differs from the historical
  full-store benchmark. Keep both block results; do not pool logging modes.
- Read runtime detail records through the approved log-reader CLI with explicit
  `--log-dir <fresh-output>`; ordinary benchmark trials are directly analyzable.
- Retain every warmup and failed attempt. Report successful-only median/p95 along
  with failure counts, and verified-paste counts under 500/1,000 ms divided by
  all measured attempts. Exact handwritten quality agreement remains separate
  from correct delivery of the pipeline's requested text. Avoid p99 on this sample.
- Use the largest measured controllable cost to choose the next experiment.
  Identify whether it is necessary before optimizing; investigate a large
  unattributed residual before claiming complete attribution.
- Use deliberate physical-hotkey runs in normal editors to investigate human
  modifier-release time: the posted-message fixture does not reproduce it. Browser
  formatting/undo acceptance and visible rendering remain separate live checks.
- Keep models, transport, 50-ms settlement, protected literals, formatting,
  clipboard recovery and active-target paste behavior unchanged at this checkpoint.
- Do not call offline compute or historical/ordinary-use observations a new full
  hotkey-to-editor baseline. That baseline remains pending until live approval.


Setup failures now emit `benchmark_setup_failed` with a single snapshot of the
foreground/focus/selection/text-length and text-match prerequisites, excluding
text contents. `summary.json.block_error` retains the abort reason even if setup
failed before the first attempted warmup. Keep these zero-trial blocks as fixture
diagnostics; they cannot establish app latency or a product failure rate.

An occupied benchmark chord can be overridden explicitly with `--benchmark-vk N`
(decimal Windows virtual-key code). The default remains `BuildChannel.BenchmarkHotkeyVk`.
The manifest records `benchmark_vk`; this only changes the isolated registered
benchmark chord, not product hotkeys, posted-message dispatch or correctness gates.
Preserve registration failures as zero-trial preflight aborts.

For desktop automation that cannot activate the window within the default three
seconds, use `--setup-timeout-ms 30000` (bounded to 3,000–120,000). This only extends
unmeasured setup; all foreground, focus, complete-selection and fixture-text gates
still must pass before any hotkey is posted. The manifest records the setup limit.

### Settlement wait follow-up, October 23, 2026

The bounded [settlement comparison record](../bench/records/2026-10-desktop-latency/settlement/README.md) contains eight alternating blocks, 480 retained deliveries, a native wait-only probe and real-hotkey editor acceptance. The high-resolution wait preserves at least 50 ms and message pumping; native timer failures use the previous timeout path. Candidate local completion medians are about 70–71 ms versus baseline 81–83 ms on this desktop. Keep the unsupported-rich formatting limitation and frozen rich-list spacing discrepancy separate from delivery success.

For ordered raw-block phase comparisons use python bench/phase_report.py --block NAME=DIRECTORY (repeat the block flag). Optional --output exclusively creates a fresh JSON report. It checks matching workloads within each diagnostic mode, reports nested phase distributions with sample counts, and retains failures and incomplete coverage. It does not launch desktop tests or award an automatic speed/reliability verdict. See the desktop-results follow-up for an ABBA example.

### Deterministic correction and request investigation gates

CI runs the actual optimized executable over the frozen ten-case compute workload
and requires exact output, paste text and CF_HTML. It then mutates one expected
output and requires a nonzero result plus an explicitly retained failed trial.
Neither setup failure nor missing rows counts as a successful negative control.
The gate is `bench/check_native_contracts.py`; evidence uploads even after failure.
Existing Dev/Prod unit tests include protected literals/model parameters, native
HTTP reads/deadlines/lifetime, and a gated loopback response proving the shared deadline expires even while
the mock withholds response headers. Send completion ordering is not assumed. No provider key is in CI.
These deterministic contracts prevent covered regressions; they cannot guarantee
live service latency, all editor rendering, or every possible correction.

Evidence checks use explicit failures, per-case warmup/measured coverage, frozen
workload hashes and the recorded A/B claim. CI also corrupts case coverage under
`python -O` and requires rejection. The saved-record speed check validates an
existing observation, not a wall-clock threshold on shared CI hardware.

For request attribution without clipboard/focus changes, the optimized binary
supports `--bench-request --allow-api --settings <saved-data-directory> --output
<fresh-directory> --runs 3`, optionally `--prewarm`. It reads saved settings and
encrypted credentials without writing them, sends only `This is a tset.` to the
unchanged saved model/endpoint, reuses one client, and retains every result in
JSONL. It does not initialize the tray, updater, migration or startup registration.
`--allow-api` explicitly opts into these bounded real API calls. Output creation
fails if the directory exists. Compare cold first and subsequent same-client
requests using native metadata, not labels alone. This is a diagnostic probe,
not an end-to-end editor speed measurement.
