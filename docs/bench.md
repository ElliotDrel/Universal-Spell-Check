# C# / Rust Baseline

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
| `local` | Real hotkey/coordinator/capture/formatting/paste; only HTTP transport replaced with recorded output | Yes |
| `e2e` | Real hotkey/coordinator/capture/formatting/live API/paste | Yes |
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

Desktop tests MUST wait until Elliot has stopped using the computer. Preparing the
command or building the runner does not authorize running it during active use.
Both desktop modes reject invocation without `--allow-desktop`:

```powershell
dotnet bench/bin/Release/net10.0-windows/UniversalSpellCheck.Bench.dll --mode local --fixtures bench/results/csharp-api-a/fixtures.json --runs 20 --warmup 2 --allow-desktop --output bench/results/csharp-local-a
dotnet bench/bin/Release/net10.0-windows/UniversalSpellCheck.Bench.dll --mode e2e --runs 10 --warmup 2 --allow-desktop --output bench/results/csharp-e2e-a
```

The visible benchmark editor uses Ctrl+Alt+B, separate from Prod/Dev. It allows
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
- Leave local clipboard/E2E, original-editor acceptance and startup timing unmeasured
  until the requested idle-desktop turn. The installed-app memory figure is one
  aged-process snapshot, not a steady-state comparison.
