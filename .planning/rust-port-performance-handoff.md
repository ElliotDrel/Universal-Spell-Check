# Rust Port and Performance Handoff

## Active Rust Port - October 17, 2026

This section supersedes the historical cleanup and pre-port instructions below.

- Continue in `C:/Users/2supe/.codex/worktrees/rust-port/Universal Spell Check`, branch `codex/rust-port`, based on primary local `main` at `8c919a7`. Preserve main's fifteen unpublished baseline commits, the frozen baseline, backups, and unrelated formatting worktree.
- The native Rust implementation is in `rust/`. Read `rust/AGENTS.md`, `docs/architecture.md`, `docs/bench.md`, and Git status before continuing. The installed product remains C#; no Rust installation or publication is authorized by this test run.
- Use KISS/MVP judgment. Native Windows controls, clipboard, `SendInput`, DPAPI, and asynchronous WinHTTP implement the app without a web renderer. Channel constants still come from `src/BuildChannel.cs` at build time.
- Requested thinking/adversarial reviews used `claude -p --safe-mode --model claude-opus-5-5 --effort medium --tools '' --strict-mcp-config --no-session-persistence`. Reports are retained under primary `benchmark_runs/rust-port/claude-*-review*.json`; normal hooks in the first report caused an irrelevant follow-up, so later reviews disable hooks.
- The completed desktop window was explicitly authorized by Elliot's `run tests now`. Desktop automation has ended and installed C# is restored. Require fresh idle authorization in a later turn.

### Active acceptance preparation - stop before desktop control

- Elliot asked to finish original Google Docs/ChatGPT and installed updater acceptance, prepare everything, then END THE TURN BEFORE taking computer control. He also requested an Astra subagent review. The review is complete; no active agent remains.
- Do not reuse the previous idle window. No focus, clipboard, browser UI, tray UI, installer or update tests ran during this preparation. Wait for his next explicit `Run`.
- Astra confirmed two defects, now fixed: Restart ignored a downloaded package when the feed was unavailable; optional HTML/RTF/native MIME read failures aborted valid Unicode capture. Preserve deliberate current-target paste behavior, as required by the app/site formatting plan; the reviewer withdrew that finding.
- Broader checks caught a timeout mock race: expected cancellation could reset the connection before the server read request headers. Permit reset/abort only in that cancellation test; the strict success case and callback-state lifetime assertions remain intact.
- Current normal editor candidate is primary `benchmark_runs/rust-port/acceptance-prep/updater/editor-candidate/UniversalSpellCheck.exe`, SHA-256 `762b007961e6baf956b2162fe23012c66a11a7f3d0e4508c9b96baa0d7e2c598`. This includes the review fixes. Keep the earlier measured candidate and benchmark results unchanged; do not claim the new candidate has completed original-editor acceptance.
- Four synthetic cases and exact visual checks are in `bench/fixtures/editor-acceptance.json`; rendered copy-source fixtures are under primary `acceptance-prep/editor-fixtures/`. Run actual Google Docs/ChatGPT controls with frozen C# and reviewed Rust after authorization. Use only new synthetic documents/composers, preserve original drafts, and do not submit ChatGPT messages.
- Three private QA packages (`0.13.0-acceptance.1`, `.2`, `.3`) are prepared under primary `acceptance-prep/updater/releases/`. Their app ID/mutex is `UniversalSpellCheck.Acceptance`; shortcuts are disabled. The compile-time QA feature uses a private FileSource, suppresses correction hotkeys/login writes, and validates canonical paths plus installed identity. No QA installation exists yet.
- Read `docs/bench.md`'s prepared acceptance section. Use `bench/rust-update-acceptance.py` only with a fresh Run authorization before adding `--allow-desktop`. Test no-update, manual download, offline cached apply, corrupted update recovery, automatic launch apply, plain restart, and private uninstall. Keep the feed DIRECTORY present while offline; rename only its index.
- Preserve synthetic settings/DPAPI fixture hashes under `acceptance-prep/updater/fixture-integrity.json`. Production credentials were never copied into QA. The helper compares fixtures with their frozen seed and records production settings/key/login startup at the first authorized mutation for the entire run. Astra caught and fixed a per-invocation-only comparison gap; four regressions cover asynchronous changes/deletion, and Windows Rust CI runs them. A private Windows uninstall entry is expected; remove only QA after evidence capture.
- `acceptance-prep/updater/preflight-report.json` verifies all three package identities, checksums, exact embedded binaries, four licenses and no shortcuts; the mutation helper refuses missing desktop authorization. Headless normal Release formatting parity is again 76/76. Dev/Prod each pass 20 unit plus one integration test; acceptance passes 21 unit plus one integration test; all three Clippy configurations and formatting pass. Four Python protected-data helper regressions also pass.
- Downloading the full `vpk` tool ran out of disk. Elliot approved a private tool copy; Windows/.NET 10 files are extracted under `acceptance-prep/tools/`, with source URL/hash/provenance in `extraction.json`. The duplicate compressed download was removed after extraction. The failed staging cleaned itself up.
- Only this worktree's disposable Rust incremental compiler cache was cleared after disk pressure; no user data, frozen baseline, backups or other worktrees were removed. Acceptance preparation uses `CARGO_INCREMENTAL=0`. About 660 MiB remained at package completion; recheck before any new build.
- The first QA v2 packaging attempt used incompatible no-installer/no-portable options. Diagnostics are retained; the corrected preparer creates private installers for all three versions. `finish-preparation.py` in the ignored artifact directory completed this interrupted prep; use the committed preparer with a fresh root for a fresh build.
- Installed C# remains PID 5600 at preparation, exact installed path unchanged; recheck before stopping it for editor tests. All production settings/encrypted-key hashes match their pre-desktop values. No publication or production installation occurred.

### Candidate and correctness

- Native GUI/resource fixes through `3f60b70` are packaged in primary `benchmark_runs/rust-port/candidate-prod-final/`. Earlier candidate folders are marked superseded. Static CRT, replacements, three font licenses and NWG MIT license are packaged. No production release has occurred.
- Dev and Prod each pass 19 unit tests plus one GDI integration test, covering ten frozen processing fixtures and 76 C# rich-formatting cases. Format and both Clippy configurations pass; existing upstream vendor warnings remain. The final optimized candidate again matched all 76 recorded C# cases exactly (`final-regressions/release-parity.json`).
- Every final timing block must match settings, prompt, cases, developer logging and shared evidence-store quota. Use only the frozen `reference/safe-runner/UniversalSpellCheck.Bench.dll`, never quarantined older runners.
- `bench/compare-rust.ps1` waits for actual native process exit with `ProcessStartInfo` and drains stdout/stderr concurrently. `compute-abba-01` is excluded because the old PowerShell invocation could overlap GUI-subsystem children.

### Desktop failures repaired

- NWG reserves raw handler IDs through `0xFFFF`; application handlers use `style::raw_handler_id` above that range. A real startup smoke caught this panic, which pure processing tests missed.
- Immediate post-input developer clipboard readback could contend with the target paste. It now runs once, without waiting, before injection; full bytes live in sidecars. This verifies publication, not rendering. Moving the reader alone did not fix every missed paste.
- Zero-delay clipboard publication still produced unchanged editors with corrected clipboard bytes and full source selection. The exact external contention source is unproven. Retain the original C# 50 ms settling interval, implemented as a full pumping deadline; never blindly repaste. Forty focused live attempts then passed. Their timing is diagnostic because compilation overlapped that run.
- Exclude incomplete `e2e-abba-01`, `e2e-abba-02`, and zero-delay paste diagnostics from final timing. `local-abba-01` passed but its zero-delay binary subsequently failed live tests, so its roughly tenfold speedup is superseded.
- Startup uses the embedded PE icon resource; NWG binary icon decoding requires an optional feature and previously panicked. CI now executes optimized `--startup-smoke` in isolated app data.
- Dashboard diffs append through `EM_REPLACESEL` to preserve earlier formatting; the NWG convenience append erased previous spans. Model labels no longer cover the selector; the window centers and scales its 900 by 600 logical minimum.
- NWG 1.0.13 leaked a screen DC on each DPI query. The narrow vendor patch releases it. Primitive reproduction leaked 100 objects without release and zero with release; final dashboard GDI counts remained 85 across three resize cycles. Regression checks 100 actual library calls. Keep provenance, license and upstream issue with the vendor; do not edit unrelated upstream code.

### Native acceptance and limits

- Actual Chrome paste tests passed plain correction, a ProseMirror-style formatted sample preserving bold/link, unchanged-text paste, no-selection recovery, overlap rejection, and physical Ctrl+Alt+U with Chrome retaining focus.
- The browser fixture uses an explicit copy producer and real Chrome paste/render. It is not acceptance in original ChatGPT or Google Docs. Its elapsed fields include observation delays and are not timing evidence.
- Dashboard checks passed Home/Settings, masked keys without editing them, diff color/strikeout, output copy, separate side-by-side controls, timing expansion, initial 30-entry page and scrolling into the next page. The model selector is present at the minimum physical 1125 by 750 size on this display. Activity refresh is explicit; no automatic watcher is claimed.
- Original-editor visual acceptance, OS Win+V retention, installed updater/release behavior, and wider-machine acceptance remain unverified. Do not equate clipboard payload tests with those checks.

### Final matched measurements

All final results are under primary `benchmark_runs/rust-port/`; `README.md` and `final-results.json` summarize the raw blocks.

- `candidate-prod-final/UniversalSpellCheck.exe`: 7,430,656 bytes; SHA-256 `541AB7110BFB2E8C030B6D1C74567B4FC0CE5761502095B4BD30328A4A08E27E`. App code is through `3f60b70`; subsequent startup-helper fix `80ac2e4` does not change this executable.
- `local-abba-final`: C#/Rust/Rust/C#, each 30/30 measured and 10/10 warmup verified pastes. Median C# 256.634/262.748 ms versus Rust 89.136/87.026 ms; matched ratios 2.88/3.02. Local p95 C# 271.355/269.932 versus Rust 99.423/97.135 ms.
- `e2e-abba-final`: same counts/order and live API, every actual paste passed. Median C# 1094.421/1079.575 ms versus Rust 888.976/838.941 ms; 18.8%/22.3% less elapsed time (1.23/1.29 times faster). Live p95 C# 1736.269/1485.474 versus Rust 1448.907/1494.596 ms. Do not claim consistent tail improvement, significance, or that all API variation is a language effect.
- All 320 local/live developer-evidence completions reported `storage_limit`, matching the full shared store. C# completion records use `developer_evidence`; Rust completion records use `reference`; both statuses were inspected through the approved log reader. Expected-text differences were only the known rich-list trailing spaces.
- `startup-abba-final-02`: three samples per block, six per implementation. Median C# 596.056/636.925 ms versus Rust 80.587/75.606 ms. Warm-cache OS-process-to-ready boundary excludes installed updater and automatic dashboard rendering. Preserve every individual launch; no cold-boot claim.
- `startup-abba-final` is excluded. PowerShell implicitly converted ISO JSON timestamps to DateTime, then lost fractional seconds when reparsing; a real same-second ready milestone fell before its recorded launch. The helper now uses `ConvertFrom-Json -DateKind String` so precision and offsets survive.
- `compute-abba-final`: 200/200 measured frozen-output/mapping checks plus 20/20 warmups per block. Median C# 0.28190/0.23405 ms versus Rust 0.09175/0.14525 ms. p95 C# 1.18511/1.301635 versus Rust 1.17747/1.27845 ms. All blocks retain 180/200 handwritten expected-text matches because of the identical frozen list spaces.
- `post-test-integrity.json`: all 131 baseline hashes, settings and encrypted credentials unchanged. Installed C# Prod v0.12.3 (`c370110ec95b5b5f70816e9494a3c2c8dc3f3e76`) restored and verified running, PID 5600 at closeout. Recheck PID in later turns.
- Both task Chrome fixture windows and their loopback server are closed. No Rust GUI/benchmark/Claude/compiler remains running. Preserve the ignored browser profile and diagnostic artifacts, including any backups.
- Original Google Docs/ChatGPT visual acceptance and installed updater/release validation remain pending. The candidate is tested, portable and reviewable; deployment still requires the user's release instruction.

## Completed Baseline Handoff

## Compaction Handoff — Start Here

- Elliot wants this Windows spell checker ported to Rust for speed, using KISS/MVP judgment; the requested pre-port baseline is finished, and no Rust implementation has started.
- The current task is cleanup and compaction preparation. Start the port when Elliot resumes with that instruction; do not interpret the historical investigation below as the current task.
- Continue from the primary checkout's local `main`, which contains the completed benchmark and credential-isolation repair. It was 14 commits ahead of `origin/main` at benchmark completion; this handoff adds another commit. Do not reset to `origin/main` or lose those unpublished changes.
- The benchmark worktree `codex/rust-baseline` is being archived after its work is committed and fast-forwarded into `main`; use a fresh worktree based on local `main` for the Rust port.
- Preserve the unrelated `.worktrees/chatgpt-formatting` checkout, other branches, existing backups, user data and installed Prod app. Cleanup is scoped to this benchmark session.
- Read root `AGENTS.md`, `docs/overview.md`, `docs/architecture.md`, `docs/bench.md`, `docs/model-config.md` and the routed rich-text/formatting documents before implementation. Preserve the select → hotkey → corrected replacement behavior and existing feature contracts.
- Choose the simplest viable Windows Rust implementation after inspecting the current code; no Rust UI framework or migration architecture has been selected.
- Verify the Rust toolchain and available disk space first. Elliot is handling the disk-space problem; do not expand the task into general disk cleanup.
- Keep Prod and Dev channel separation, safe data paths, DPAPI-protected named keys, shared stamped logs, replacements, rich formatting and literal protection intact. Never use production credential storage for offline tests.
- Installed Prod was restored as version `0.12.3+c370110ec95b5b5f70816e9494a3c2c8dc3f3e76`; PID 31036 was verified after the final run, but recheck current process state before acting. No deployment or publication occurred.
- Require a fresh explicit idle-desktop window before any future focus/clipboard benchmark. The completed idle window has ended; ordinary coding can proceed without desktop control.
- Preserve `benchmark_runs/rust-baseline-2026-10-16/` in the primary checkout: `README.md`, `baseline-completion.json`, `post-run-integrity.json`, raw trials, fixtures, frozen Release binaries, repaired source archive and `checksums.json`. These ignored artifacts do not travel with a normal Git checkout.
- The portable `.dotnet/sdk-runtime` remains available to run/rebuild the C# reference; session FSharp compiler files were removed during the disk-full incident, while the C# compiler and runtime were retained.
- Canonical live results: `desktop/e2e-space-verified-final`, 100/100 measured replacements and 20/20 warmups; median 996.8639 ms, p95 2,118.2642 ms. Five measured expected-text differences were only trailing list spaces.
- Canonical local desktop results: `desktop/local-native-final`, 98/100 measured successes and 19/20 warmups; median 370.3875 ms, p95 6,178.29305 ms among successful attempts. Keep clipboard failures in the reliability counts.
- Canonical compute results: `compute-final-a` and `compute-final-b`, each 200/200 measured successes, medians 0.50595/0.54315 ms. Startup: `startup-final`, ten successes, median 575.4677 ms, p95 708.828935 ms with no cold-cache claim.
- Most visible latency is API/clipboard work; a faster language alone does not establish a speedup. Compare matched C#/Rust/Rust/C# blocks on this machine, keeping correctness, failures and successful-only timing distributions together.
- The earlier harness overwrote the active encrypted key with an offline placeholder. The isolation fix is committed, and explicitly approved cached-key recovery restored the credential with HTTP 200 and encrypted readback. Final live/startup runs left encrypted credentials and settings unchanged. Do not repeat recovery or inspect process memory as routine port work.
- Use only `reference/safe-runner/UniversalSpellCheck.Bench.dll` for the frozen C# benchmark. Older entrypoints remain quarantined as `.UNSAFE-DO-NOT-RUN`; older source archives are historical and must not be executed without the repaired credential guards.
- All 120 final live developer-evidence completions reported `storage_limit`; match or explicitly record any change to this logging/evidence condition. Synthetic editor checks do not prove visual formatting acceptance in the original browser/editor.
- Treat `desktop/e2e-native-final` (HTTP 401), `desktop/e2e-recovered-final` (disk-full), earlier fixture diagnostics and `compute-a`/`compute-b` as excluded diagnostics, never canonical latency samples.


## October 16 Baseline Supersedes the Old Comparison Harness

- Read `docs/bench.md` before using the historical investigation below.
- Find the completed C# reference in the primary checkout at `benchmark_runs/rust-baseline-2026-10-16/README.md` and `baseline-completion.json`.
- Preserve live desktop results at 100/100 measured verified replacements, median 996.8639 ms and p95 2,118.2642 ms; local replay retained two measured clipboard failures, median 370.3875 ms and p95 6,178.29305 ms among successes.
- Preserve ten startup readiness samples with median 575.4677 ms, plus two complete local-processing blocks near 0.5 ms median.
- Use the repaired safe runner and frozen product, not quarantined older benchmark entrypoints; credential recovery is complete and encrypted storage was verified unchanged after the final runs.
- Match the recorded logging/evidence condition and retain all failures; the October 10 observations below are historical hypotheses, not a substitute for the new fixed-workload baseline.
- Free disk space before Rust builds; the completed benchmark left only about 49 MiB free, and an earlier incomplete live attempt stopped on disk-full.

## Historical October 10 Investigation — After the Port

- Resume the goal of reducing **selected-text hotkey → verified editor replacement by at least 2×**.
- Treat this as the October 10, 2026 investigation snapshot, not a description of the future Rust implementation.
- Keep Elliot's interactive keyboard and clipboard untouched while he uses his computer.
- Recognize that hidden windows and another desktop in the same window station still share the clipboard.
- Resume after the full Rust port; Elliot chose to defer optimization of the C# writer that will be replaced.
- Treat the main bottleneck as localized **before keyboard injection**, with the exact blocking clipboard/OLE call still unproven.
- Do not claim a 2× improvement: none was established.

## Repository and Recoverable Work

- Start in `C:/Users/2supe/All Coding/Universal Spell Check` and read its current `AGENTS.md` before acting.
- Read `docs/bench.md`, `docs/debugging-principles.md`, `docs/watchlist.md`, `docs/replacements-and-logging.md`, and `docs/model-config.md`.
- Locate the investigation code through these commits, even if the Rust port deletes the C# files:

| Commit | Meaning |
|---|---|
| `dc989e7` | Previous product implementation using WinForms `SendKeys.SendWait` for copy/paste; baseline product commit. |
| `b1738a6` | Batched Win32 `SendInput` copy/paste helper and native smoke instrumentation. |
| `5742e80` | Frozen-input benchmark support, local replay, exact paste validation, normal message loop, custom-input correctness checker, and comparison documentation. |
| `d38ca2e` | Saved-evidence investigation, separate paste-stage timers, and fixture selection guard. |

- Recover source with `git show d38ca2e:src/ClipboardLoop.cs` or the corresponding repository-relative path below.
- Find the local investigation branch at `codex/native-copy-paste`; it was not merged, pushed, or deployed by this task.
- Recover portable patches for the three investigation commits from the private package's `code-patches/` if repository history is lost; review them as C# reference code rather than blindly applying them to the Rust port.
- Find the two original managed checkouts by their archived attachments in this chat: `native-copy-paste` and `copy-paste-baseline`.
- Do not assume those checkout directories still exist; cleanup archives them after preserving the private data.
- Recheck installed app version and current Git state after the Rust port; this task did not deploy any change, and other sessions may have changed production.

## Original Product Path

- Follow the C# hot path: hotkey receipt → physical modifier release → selection capture → clipboard-history exclusion → formatting/literal protection → Responses API → text processing/rich reconstruction → replacement clipboard write → 50 ms settle await → current foreground lookup → Ctrl+V injection.
- Preserve the current destination behavior deliberately: production pastes into whichever app is active when the correction is ready, even if it differs from the capture app.
- Preserve Unicode text and verified CF_HTML formatting rather than optimizing through a plain-text-only replacement.
- Preserve best-effort exclusion of captured incorrect text from clipboard history while leaving the corrected write untagged for history inclusion.
- Recheck all these contracts in the Rust port before comparing performance.

| C# source at `d38ca2e` | Relevant behavior |
|---|---|
| `src/ClipboardLoop.cs` | Capture, history exclusion, WinForms clipboard write/retries, readback, and local replacement. |
| `src/NativeKeyboard.cs` | One batch per Ctrl+C/Ctrl+V chord; preserves a held Ctrl and handles partial insertion. |
| `src/SpellcheckCoordinator.cs` | Hot-path ordering, headless path, paste timing boundaries, and log serialization. |
| `src/RunRecord.cs` | Stopwatch timestamps and run metadata. |
| `src/DeveloperEvidence.cs` | Saved payload hashes, wall-clock evidence marks, screenshots, and accessibility observations. |
| `src/OpenAiSpellcheckService.cs` | Persistent transport, connection warming, and request timing boundaries. |
| `bench/Program.cs` | Headless/E2E/local replay modes and frozen dataset loading. |
| `bench/BenchHarness.cs` | Per-trial timing and exact editor-text checks. |
| `bench/BenchTargetForm.cs` | Disposable native TextBox, focus/selection setup, and paste events. |
| `bench/ClipboardReplayBench.cs` | Real local capture/write/paste using recorded corrections without API access. |

## Private Data Preserved Locally

- Find all preserved data under `bench/results/native-input-comparison/` in the primary checkout.
- Keep this directory ignored by Git; it contains private production selections, corrections, and accessibility text.
- Preserve it separately when moving to a new repository or machine; cloning Git will not include these files.
- Verify original copied files against `preservation-manifest.json`, which records relative paths, byte counts, and SHA-256 hashes.
- Keep `inputs.json` frozen: nine `{id, text, output}` cases selected by length from real production corrections.
- Interpret `output` as the original recorded `paste_text`, including product processing; do not substitute raw AI output.
- Keep the original analysis scripts as evidence of the method; their historical worktree paths are stale after cleanup.
- Run the portable copies below instead; they use preserved results and manifests without touching the live clipboard, keyboard, API, or runtime logs.

| Artifact | Purpose |
|---|---|
| `inputs.json` | IDs `prod-01` through `prod-09`; input sizes 19, 45, 98, 100, 214, 972, 1,111, 1,113, and 1,134 characters. |
| `historical-timings.json` | Original production timestamps and phase timings for the nine cases. |
| `summary.json` | Original analysis snapshot; absolute worktree result references are historical. |
| `summary-preserved.json` | Portable recomputation with result paths in the primary checkout. |
| `network-summary.json` | Per-headless-block send/wait/download means and measured log counts. |
| `stall-evidence-summary.json` | Derived keyboard/pre-keyboard intervals, hash comparisons, and failure metadata. |
| `baseline-results/` | Old product live/headless result files, including the excluded initial live attempt. |
| `candidate-results/` | Native product headless result files; the interrupted native E2E block has no final result JSON. |
| `replay-results/` | Local replay trial results, including failures and preliminary fixture attempts. |
| `old-live3-a.details.jsonl`, `new-live3-a.details.jsonl` | Saved output of the log-reader CLI for the final live blocks. |
| `production-details.jsonl` | Saved CLI export of October 9 production details; analysis restricts to the original cutoff. |
| `evidence/developer-evidence/<run_id>/manifest.json` | Preserved evidence timestamps, targets, accessibility contexts, and payload metadata. |
| `*.stdout.log`, `*.stderr.log` | Block completion, runtime-log paths, and the native block's focus-loss exception. |
| `reanalyze-preserved.py` | Recompute aggregate successful-trial results from saved JSON. |
| `investigate-preserved-stalls.py` | Recompute stall bounds from preserved CLI exports and manifests. |
| `code-patches/` | Portable patches for `b1738a6`, `5742e80`, and `d38ca2e`. |

```powershell
python bench/results/native-input-comparison/reanalyze-preserved.py
python bench/results/native-input-comparison/investigate-preserved-stalls.py
```

- Expect the portable aggregate script to write `summary-preserved.json`.
- Expect evidence manifests to reference payload sidecars/screenshots in the original log store; those binaries and sidecars were not duplicated into the preserved package.
- Use the manifest's stored hashes and timestamps for this investigation; they are sufficient to recompute its conclusions.
- Recover original full sidecars, if needed and still available, from `C:/Users/2supe/AppData/Local/UniversalSpellCheck.Data/logs/developer-evidence/<run_id>/`.
- Read additional runtime logs through `.agents/skills/read-logs/scripts/logs.py`, never by manually parsing runtime log files.
- Specify `--log-dir` explicitly; sandboxed processes can otherwise resolve a different LocalAppData path.
- Remember that `bench-*.jsonl` runtime files were copied to an ignored `progress-log/spellcheck-YYYY-MM-DD.jsonl` for the reader's filename discovery.

## Rebuilding the Historical C# Reference

- Use an isolated checkout of the desired commit; do not replace the Rust port's working files with the old C# tree.
- Use the bundled SDK at `C:/Users/2supe/All Coding/Universal Spell Check/.dotnet/sdk-runtime/dotnet.exe` if it still exists.
- Recognize that this session used SDK 10.0.401; system SDK 10.0.204 did not satisfy the repository's `global.json`.
- Set `DOTNET_ROOT` and prepend that SDK folder to `PATH` when launching compiled reference executables.
- Build with the following commands only after confirming the old project layout; building does not launch clipboard tests.

```powershell
$env:DOTNET_ROOT = 'C:/Users/2supe/All Coding/Universal Spell Check/.dotnet/sdk-runtime'
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
& "$env:DOTNET_ROOT/dotnet.exe" build bench/UniversalSpellCheck.Bench.csproj -c Release
& "$env:DOTNET_ROOT/dotnet.exe" build src/UniversalSpellCheck.csproj -c Dev
```

- Keep historical channel constants in `src/BuildChannel.cs`: Prod uses Ctrl+Alt+U, Dev uses Ctrl+Alt+D, and the benchmark uses Ctrl+Alt+B.
- Keep settings and protected API keys separate by channel while retaining unified logs stamped with channel/version/PID.
- Do not launch any of those hotkeys, the normal desktop app, or a clipboard smoke test while Elliot requires uninterrupted desktop access.

## Benchmark Method and Limits

- Compare Release product commits `dc989e7` and `b1738a6` with an identical benchmark harness and the frozen nine-input dataset.
- Use `gpt-4.1`, matching the measured production corpus.
- Alternate old/new/new/old blocks rather than running all old trials first.
- Exclude per-input warmups from local/E2E aggregates; exclude one global warmup per headless block.
- Count every failed measured trial; do not hide failures by reporting only successful averages.
- Verify actual editor text against requested `paste_text`, normalizing only CRLF to LF.
- Distinguish input submission from editor replacement: `SendInput` returning proves accepted input, not completed paste/rendering.
- Distinguish local replay from hotkey E2E: replay skips physical hotkey release, API, rich reconstruction, and developer evidence.
- Exclude preliminary `old-live-a`, interrupted `new-live-a`, `fixture-check`, `old-local-a`, and `old-local2-a` from the final timing comparison.
- Retain those diagnostic attempts; the former `DoEvents` plus 10 ms polling loop distorted live paste timing and was replaced by `Application.Run`.
- Use final local labels `old-local3-a`, `new-local3-a`, `new-local3-b`, and `old-local3-b`.
- Recognize that the native `new-live3-a` block aborted after focus loss and discarded its final aggregate; its saved runtime evidence remains usable for component diagnosis.
- Recognize that the custom-input contract checker skipped all nine cases because they contained no applicable protected literals or replacement variants; its pass is not semantic-quality evidence.
- Do not infer statistical confidence from a fixed 5% threshold or a small sample; report distribution, sample count, failures, and order effects.

## Collected Timings

All values below are milliseconds; averages exclude failed measured trials unless stated otherwise.

| Measurement | Previous | Native input |
|---|---:|---:|
| Headless request mean, 45/45 successful per version | 1,341.64 | 1,191.00 |
| Headless request median | 987 | 859 |
| Headless request p95 | 2,412.2 | 1,987.4 |
| Headless total mean | 1,342.22 | 1,191.47 |
| Request body-write interval mean | 1.00 | 0.47 |
| Body-write → response headers mean | 1,310.80 | 1,160.15 |
| Response body read mean | 28.98 | 29.56 |
| Local capture mean | 108.97 | 90.22 |
| Local paste mean | 1,559.69 | 1,803.07 |
| Local capture → verified replacement mean | 1,658.10 | 1,918.07 |
| Local measured successes / attempts | 39 / 90 | 60 / 90 |
| Completed E2E hotkey → editor mean | 3,272.93 (27/27 passed) | No completed matched block |

- Interpret the headless 11.2% request reduction as observed request variability; the API path was unchanged and native keyboard code is not exercised headlessly.
- Treat local comparisons as inconclusive: 56 trials lost foreground focus, other trials failed capture/replacement, and successful-only samples are biased.
- Compare against the earlier production corpus only as context, not a matched optimization result.
- Use the original production window: 63 successful Prod runs on October 9 through 22:24:05 America/Indianapolis, recorded as app v0.10.0 with developer logging enabled.
- Use those production means: capture 302.7, request 1,465.8, send 33.0, wait 1,401.2, download 30.7, paste 292.4, total 2,070.2, and residual approximately 9.3.
- Use the selected nine cases' original means: capture 277.44, request 1,629.89, paste 140.89, and total 2,054.56.
- Do not extrapolate the synthetic editor's 1–2 second paste mean to normal production paste behavior.

## Confirmed Stall Findings

- Derive approximate keyboard duration from evidence `paste_issued_at` to the after-paste context's `requested_at`.
- Confirm code ordering: `MarkPasteIssued` is immediately before injection; the after-paste context is requested immediately after the injection call returns.
- Include small intervening overhead and wall-clock uncertainty in that derived interval; it is not a direct Stopwatch measurement.
- Subtract the approximate keyboard interval from logged `paste_ms` to locate the preceding delay.
- Find 36 old live calls including warmups with a mean approximate keyboard interval of 83.72 ms.
- Find 17 completed native live calls before interruption with a mean approximate interval of 1.93 ms; these are not matched end-to-end samples.
- Find 21 old live pastes at least one second long: mean paste 3,197.14 ms, mean pre-keyboard interval 3,112.95 ms, and mean keyboard interval 84.19 ms.
- Find eight native live pastes at least one second long: mean paste 2,091 ms, mean pre-keyboard interval 2,088.85 ms, and mean keyboard interval 2.15 ms.
- Confirm the same pattern in production: four pastes exceeded one second; three had both evidence bounds.
- Find those three production runs averaged 2,847.37 ms before injection and 64.63 ms in the approximate keyboard interval.
- Inspect production run `4aa4ecf243134fa0ad10a46beede9c6e`: 6,213 ms paste, approximately 6,143.73 ms before injection, and 69.27 ms in injection.
- Confirm requested/readback text hashes matched in all 63 production manifests; this proves retained clipboard payloads, not correct editor rendering.
- Recognize that readback is started after the setter returns and is not awaited on the hot path; it cannot by itself explain all delay before the write completes.
- Do not identify a particular OLE call, clipboard lock owner, retry count, or scheduling delay as proven from these saved logs.

## Reliability Findings and Prepared Diagnostics

- Identify four native live runs with the target changing from the benchmark to ChatGPT or Chrome; those cannot validate paste in the benchmark editor.
- Identify both logged native capture failures with unchanged clipboard sequence numbers, no held modifiers, an unelevated benchmark foreground process, and saved accessibility reporting `caret_or_no_selection`.
- Treat missing selection as an observed fixture condition; its precise cause and timing were not established.
- Preserve the `d38ca2e` fixture change: select all after focus transitions and refuse capture injection unless the TextBox is focused and has the whole nonempty input selected.
- Recognize that this guard cannot prevent a later target switch during an API request; a future isolated test must not run the general E2E path on Elliot's interactive desktop.
- Preserve the new `timings` fields when porting diagnostics:

| Field | Boundary |
|---|---|
| `paste_clipboard_write_ms` | Actual setter wrapper, including WinForms/OLE calls and outer retries. |
| `paste_clipboard_setup_ms` | Remaining write-wrapper work, including starting evidence readback or Dev verification. |
| `paste_settle_ms` | Actual nominal 50 ms await duration, including scheduling delay. |
| `paste_target_check_ms` | Foreground lookup and evidence mark before input. |
| `paste_input_ms` | Native input call until return or caught failure. |

- Preserve zero for missing/unreached stages and account for whole-millisecond rounding when summing components.
- Recognize that these fields were added after the recorded benchmark; historical results cannot populate them retroactively.
- Treat instrumentation as prepared, not deployed or live-verified: Release/Dev builds passed, three offline correctness-checker tests passed, and no app was relaunched.
- Keep existing channel-specific CS0162 and unrelated CS8602 warnings separate from these changes.
- Do not mistake this diagnostics commit for a fix of the clipboard stall; write/retry behavior was unchanged.

## Transport Findings to Recheck in Rust

- Reuse the old app's persistent `HttpClient`/`SocketsHttpHandler` design as context, not an assumed Rust configuration.
- Note the C# preferences: HTTP/2 with HTTP/1.1 fallback, five-minute pool idle timeout, ten-minute connection lifetime, and a best-effort unauthenticated `/v1/models` warm request at startup and every four minutes.
- Recheck Rust client reuse, pool configuration, negotiated protocol, and endpoint `https://api.openai.com/v1/responses`.
- Interpret request-send time as submission → transport body-write callback, potentially including pool wait/DNS/TCP/TLS/upload.
- Interpret request-wait as body write → response headers, combining transit and server work/model generation for a non-streaming response.
- Interpret download as headers → completed body read.
- Recognize that existing logs do not isolate DNS/TCP/TLS, prove connection reuse, or measure model first-token latency.
- Account for payload/parse fields being fixed zero placeholders in the old logs.
- Preserve credentials through the app's protected key store; do not put keys in benchmark files or command lines.
- Respect the historical live API approval as approval for this nine-case comparison, not unlimited new uploads in a future context.

## Next Test: Isolated Clipboard Writer After the Port

1. Reconfirm Elliot's constraint and current repo/runtime state; do not run interactive E2E, local replay, developer-evidence smoke, hotkey injection, or clipboard backup/restore on his desktop.
2. Recover the C# reference and port its write-stage diagnostics to Rust before changing retry policy.
3. Establish a genuine clipboard boundary: a VM with clipboard redirection disabled, or a verified separate **noninteractive window station** with its own clipboard.
4. Reject a hidden window or alternate desktop within `WinSta0` as insufficient isolation.
5. Investigate the private-window-station design researched below; no launcher, child probe, VM, or isolated test was implemented in this session.
6. Create a separate station and private desktop, launch a dedicated child with that station/desktop in `STARTUPINFO.lpDesktop`, and verify the child's station identity and noninteractive status before its first clipboard call.
7. Fail closed if any creation, access-right, identity, or desktop binding check fails; never fall back to `WinSta0`.
8. Keep the dedicated probe free of `SendInput`, hotkey registration, foreground activation, screenshots, API clients, and interactive clipboard reads/restores.
9. Test the writer directly with the frozen recorded outputs; read back only the isolated station's clipboard to verify exact text/HTML.
10. Time OLE initialization, data-object creation, `OleSetClipboard`, `OleFlushClipboard`, each retry/backoff, and readback separately for C#; time corresponding Windows calls in Rust.
11. Compare plain Unicode and actual CF_HTML paths, preserving formatting and corrected-text history behavior.
12. Test clean writes first, then controlled contention inside the isolated environment; distinguish a reproduced stall from a simulated retry delay.
13. Compare old/new in alternating blocks with identical cases, warmup policy, success gates, and complete failure counts.
14. If the Rust writer removes the stall, stop tuning the abandoned WinForms path; document the measured component savings.
15. If the stall persists, identify the blocking OS call and inspect lock ownership/IPC/retry policy before changing behavior.
16. Keep the isolation limit explicit: an empty noninteractive station does not reproduce Elliot's browser, clipboard history manager, or competing desktop processes, so a clean isolated result cannot rule out interactive contention.
17. Require a separately authorized idle-desktop acceptance test for a final hotkey-to-editor speed claim and original-editor formatting verification.

## Windows Evidence and Open Hypotheses

- Treat nested retry behavior as a supported suspect: our writer makes eight outer attempts with 40 ms delays; WinForms allows ten synchronous retries with 100 ms waits around OLE set/flush operations.
- Reverify implementation for the exact runtime used in a future C# reference build; the upstream source links are not a version-pinned dump of the benchmark binary.
- Consider OS clipboard lock contention, OLE rendering/flush work, and UI scheduling as unresolved alternatives.
- Avoid blaming held modifiers or elevation for the two recorded benchmark capture failures; their forensics do not support those explanations.
- Use these Microsoft references, fetched during this investigation:
  - [Window stations own separate clipboards; only WinSta0 is interactive](https://learn.microsoft.com/en-us/windows/win32/winstation/window-stations).
  - [CreateWindowStationW access, naming and lifetime](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-createwindowstationw).
  - [SetProcessWindowStation binding](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setprocesswindowstation).
  - [CreateDesktopW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-createdesktopw).
  - [CreateProcessW and startup information](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw).
  - [GetUserObjectInformationW identity/flags](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getuserobjectinformationw).
  - [WinForms Clipboard setter defaults](https://source.dot.net/System.Windows.Forms/System/Windows/Forms/OLE/Clipboard.cs.html).
  - [OLE clipboard set/flush retry implementation](https://source.dot.net/Microsoft.Private.Windows.Core/System/Private/Windows/Ole/ClipboardCore.cs.html).

## Completion Standard

- Name the precise blocking call only after reproducing and timing it directly.
- Report isolation identity, cases, environment, failures, mean/median/tail timing, and what contention was present.
- Separate API variability, setter savings, keyboard submission savings, and verified end-to-end replacement.
- Demonstrate at least 2× on comparable real workloads before marking the original performance goal achieved.
- Preserve the private data and source references when cleaning up temporary test environments.
