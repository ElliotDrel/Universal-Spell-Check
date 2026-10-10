# Resume Hotkey Performance Work After the Rust Port

## Read This First

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
