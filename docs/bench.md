# C# / Rust Baseline

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

Exclude `compute-abba-01`: the initial PowerShell wrapper did not wait for GUI-subsystem native executables, allowing possible overlap. The repaired wrapper explicitly waits for each process and drains stdout/stderr concurrently. Keep the failed-method diagnostics. Desktop, startup and visual acceptance remain pending an explicitly idle computer.
