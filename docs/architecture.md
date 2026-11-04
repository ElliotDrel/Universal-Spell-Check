# Architecture & File Layout

## Overview

The product is the native Rust tray app and dashboard under `rust/`. `main.rs` bootstraps Velopack; `app.rs` owns tray/hotkey lifetime; `runtime.rs` runs corrections; `dashboard.rs` owns the GUI; `update.rs` owns serialized update work; `store.rs` and `platform.rs` retain compatible settings, DPAPI keys and legacy-data migration. Channel constants are generated from `src/BuildChannel.cs`. Optional evidence uses `evidence.rs` for capture and `evidence_storage.rs` for bounded storage and oldest-first retention; both channels share its existing named mutex.

The sections below document the retained C#/.NET 10 reference under `src/`. It bootstraps via Velopack, registers a global hotkey, and runs a clipboard-capture → API-request → post-process → paste pipeline. AHK is archived at `.archive/ahk-legacy/` and is not part of the active system.

---

## Startup sequence (`src/Program.cs`)

1. `VelopackApp.Build().SetAutoApplyOnStartup(false).Run()` — **must be the very first line of `Main`**. Handles first-run hooks and restart-after-update. Implicit staged-package application is disabled so CLI forwarding cannot install before acknowledgement or bypass the service-owned startup check. Safe no-op when running via `dotnet run`.
2. `AppPaths.EnsureDataMigration()` creates the safe data roots and copies or merges legacy Velopack-directory data newer than the previous migration checkpoint before any logger or settings service opens a file.
3. Single-instance mutex via `BuildChannel.MutexName`. A second launch shows a message box and exits 0. With `--restart` (or `--update`), it sends a bounded, channel-specific Windows message to the running instance instead and exits 0 only when acknowledged (1 if unavailable). Dev rejects both flags with exit 1.
4. Instantiate `System.Windows.Application` with `ShutdownMode.OnExplicitShutdown`. Merge `UI/Styles.xaml` and `UI/Components.xaml` into `app.Resources`. **Without this step**, WPF `DynamicResource` lookups crash (see watchlist).
5. `Application.Run(new SpellCheckAppContext())` — starts the WinForms message loop.

`--dashboard-smoke` mode can be passed to run a headless WPF layout pump and exit 0/1. Used for CI regression detection.

---

## Channel split (`src/BuildChannel.cs`)

All channel-specific values live in `BuildChannel` as `const` or static-read-only members. Never hardcode a hotkey, mutex name, data folder, or display string anywhere else.

| Member | Prod (`Release`) | Dev (`Dev` / `DEV` defined) |
|---|---|---|
| `IsDev` | `false` | `true` |
| `DisplayName` | `Universal Spell Check` | `Universal Spell Check (Dev)` |
| `ChannelName` | `prod` | `dev` |
| `AppDataFolder` | `UniversalSpellCheck.Data` | `UniversalSpellCheck.Dev` |
| `MutexName` | `UniversalSpellCheck` | `UniversalSpellCheck.Dev` |
| `TrayTooltip` | `Universal Spell Check` | `Universal Spell Check (Dev)` |
| `HotkeyVk` | `0x55` (U) | `0x44` (D) |
| `AppVersion` | from `AssemblyInformationalVersion` (injected at build time from tag) | same base version as prod with `-dev` appended (for example `0.1.6-dev`) |

`IsDev` is a `const bool`, which means the compiler eliminates dead code at build time. CS0162 "unreachable code" warnings in channel-conditional blocks are expected and intentional.

---

## Settings isolation vs. unified logs (`src/AppPaths.cs`)

- `AppDataDirectory` — `%LocalAppData%\{BuildChannel.AppDataFolder}`. Prod and Dev are fully isolated for settings and named API-key collections. Prod uses `UniversalSpellCheck.Data`; Dev uses `UniversalSpellCheck.Dev`.
- `LogDirectory` — always `%LocalAppData%\UniversalSpellCheck.Data\logs\` regardless of channel. Both channels append to the same daily file `spellcheck-{yyyy-MM-dd}.jsonl`. Every line carries `channel`, `app_version`, and `pid`.
- `%LocalAppData%\UniversalSpellCheck\` is Velopack's installer-owned root. `AppPaths.EnsureDataMigration()` runs immediately after Velopack bootstrap and copies legacy settings, API key, state, and logs into the safe data root. The API-key file is copied only when the destination is missing, so an old single-key file can never overwrite a named collection. Other migratable files and logs are rechecked against the checkpoint. Never place durable data back in the installer root; reinstall cleanup may replace it wholesale.
- `ReplacementsPath` — walks up from `AppContext.BaseDirectory` until `replacements.json` is found. Works for both dev checkout (`src/bin/...`) and Velopack-installed prod (file is copied next to the exe at publish time).

---

## Tray lifetime (`src/SpellCheckAppContext.cs`)

Owns all long-lived objects: `NotifyIcon`, `HotkeyWindow`, `SpellcheckCoordinator`, `UpdateService`, `LoadingOverlayForm`, and the WPF `MainWindow` reference.

Tray menu items:
1. One status line combining the current version and last-check/update state.
2. Check for Updates → `UpdateService.CheckAsync(ManualTray)`.
3. Restart → `UpdateService.RequestRestart()`.
4. Separator, Open Dashboard, Open Logs Folder, and Quit.

Both channels use the a/checkmark brand in `src/Assets/`: forest green for Prod and golden yellow for Dev. `BuildChannel.IconAsset` and `LogoAsset` select embedded tray/window/sidebar resources; the project selects the matching executable icon per configuration. Release packaging uses the Prod icon for the installer and Windows shortcuts.

The dashboard auto-opens once on startup via `Application.Idle` so WPF resource failures surface immediately.

---

## Hotkey window (`src/HotkeyWindow.cs`)

Thin `NativeWindow` subclass. Calls `RegisterHotKey` with `BuildChannel.HotkeyModifiers` and `BuildChannel.HotkeyVk`. Raises `HotkeyPressed` on `WM_HOTKEY`. Prod: Ctrl+Alt+U. Dev: Ctrl+Alt+D. Both channels can run side-by-side without collision.

---

## Spell-check pipeline (`src/SpellcheckCoordinator.cs`)

Serialized via `SemaphoreSlim(1, 1)`. Overlapping hotkey presses are rejected (`guard_rejected reason=already_running`), never queued.

1. `SetPhase(Copying)` — tray text changes, `LoadingOverlayForm` shows with "Copying text...".
2. **Capture** — `ClipboardLoop.CaptureSelectionAsync()`. Waits for hotkey keys to release, snapshots the clipboard sequence number, sends Ctrl+C, waits for the sequence number to change, then polls for changed Unicode text. It reads **both** clipboard flavors in that same window: `CF_UNICODETEXT` drives the run, and `CF_HTML` is captured alongside it into `RunRecord.CapturedHtml` (`""` when the source offers none). The HTML read must happen here — step 4 empties the clipboard and the source markup is gone for good. At paste time, `RichTextClipboard` verifies Google Docs' `docs-internal-guid-` clipboard serialization and aligns edits into the original inline spans and paragraph/list markup. It retains NBSP in HTML, preserves real blank paragraphs, and excludes copy-only trailing separators/interchange markers. Replacement HTML removes the captured `docs-internal-guid-` ID so Docs cannot use it to retrieve the original cached slice instead of corrected text. Single-paragraph selections omit list/paragraph/heading wrappers to remain inline inside the destination item. Captured single-paragraph ProseMirror skill/file-reference selections have model-added line breaks removed before alignment so those breaks cannot flatten the reference through plain fallback. `RichTextClipboard` also preserves verified simple LinkedIn messaging paragraphs using the captured CF_HTML producer URL, including empty paragraphs whose copied Unicode separators would otherwise add blank lines. It also handles verified ProseMirror paragraph and list shapes, then aligns corrections to original HTML text nodes for mixed spans, links, and lists. Text-node output escapes only HTML syntax (`&`, `<`, `>`) and retains apostrophes and Unicode literally to avoid LinkedIn numeric-entity paste corruption. It keeps tags and attributes when edits map to source text nodes and also preserves HTML text nodes omitted from the Unicode clipboard flavor. Those omitted nodes are inserted into the replacement Unicode text as well. For one verified bare list item, it strips the generated marker and pastes only the corrected body as Unicode text because ChatGPT inserts an HTML block as a nested item. When rich reconstruction cannot be verified, corrected Unicode text still pastes with any recovered omitted HTML text.
3. On capture failure: restore original clipboard, notify user, log `capture_failed`, return.
4. **Exclude captured text from history** — `ClipboardLoop.ExcludeTextFromHistory()` tags the captured (incorrect) text out of Windows clipboard history (Win+V) so only the corrected text persists there. Best-effort, never fails the run; logs `capture_history_excluded` / `capture_history_exclude_failed`. Mechanism and gotchas: `docs/watchlist.md` § Clipboard history exclusion.
5. **Resolve target formatting** — `TargetFormattingPipeline` scans its short ordered rule list, freezes the first match, and runs its deterministic after-copy hook. The first rule is terminal normalization. No rule means the original string reference continues unchanged.
6. **Protect literals** — replace URLs, UUIDs/session IDs, API keys, file paths, and opaque IDs with collision-safe placeholders.
7. **Request** — the overlay reads "Sending to AI..." while the request body is written, then "Waiting for AI..." until response headers arrive. `OpenAiSpellcheckService` records separate send, wait, and response-download timings.
8. On request failure: restore clipboard, notify user, log `request_failed`, return.
9. **Post-process and restore** — `SetPhase(Pasting)` (overlay reads "Pasting..."), then `TextPostProcessor.Process(output, protection)`. Strips an exact echoed input followed by a correction divider, applies replacements, strips prompt-leak text, and restores every protected literal byte-for-byte. Missing or duplicated placeholders fail safely without a paste.
10. **Format for paste** — recapture the current foreground context and run the optional frozen before-paste hook. Before-paste rules receive formatter-neutral, private-use placeholders; a missing or duplicated placeholder skips the optional formatting and uses the already-corrected text.
Docs corrections also retain the bounded original Chromium native slice before
history exclusion. One selection model owns Docs structure: verified HTML
serialization distinguishes actual final blank paragraphs from copy-only
separators; unfamiliar HTML shapes can use copied Unicode and the native slice.
The correction is normalized to that model once (same paragraph count, blank paragraphs stay
blank, edge whitespace kept, a model terminal newline dropped) or the run
declines rich formatting. CF_HTML and the native slice then verify against it
independently. The HTML serializer treats `p` and `h1`-`h6` as Docs blocks.
`GoogleDocsClipboard` verifies native text against the copied selection and
remaps sparse style positions (heading level/ID, list, link, font) with the
same character diff. A copy-only terminal native LF is removed when it exceeds
the model; its list and paragraph styles become trailing styles for the final
partial paragraph. It removes the original cache ID and emits edited custom MIME
beside Unicode and, when verified, HTML. Only verified slices without positional
embedded content/suggestions use this path; unsupported native data retains the
corrected HTML/text fallback.
Native bytes are used in memory regardless of logging, and persisted only when
Developer logging is enabled. Unchanged text always replaces the selection.

11. **Paste** — write corrected Unicode text, plus `CF_HTML` for verified rich selections, to the clipboard (untagged so it IS kept in history). Even if rich reconstruction declines or throws, write the corrected Unicode text and paste it. Wait for the existing settle delay and send Ctrl+V to whichever app is active. Switching apps while the request runs does not change the destination behavior.
12. Log `replace_succeeded` with target-formatting metadata and separate hook timings.
13. `SetPhase(Done)` in `RunAsync` the moment the hot path returns — loading overlay hides even on failure, and **before** the original-clipboard restore, which can block for seconds on failed runs while the OS renders the original clipboard formats.

### Target formatting (`src/TargetFormatting/`)

`TargetContext` is an immutable snapshot of foreground process, PID, HWND, root-owner HWND, title,
and optional pre-cached browser metadata. `TargetFormattingPipeline` owns one explicit ordered list
of deterministic rules and catches optional hook failures so formatting cannot turn a valid generic
spellcheck into a failed request. `TerminalFormattingRule` preserves the former terminal normalizer's
process list, transformation order, output, and counters.

Site-rule resolution requires a fresh, focused HTTP(S) browser snapshot when the run starts. The
selected rule remains frozen for the run, but changing the active window or tab does not cancel
the eventual paste. The Chrome cache/extension that supplies such snapshots remains
deferred until a named target requires real URL matching; no browser query occurs on the hot path.

For the implemented file map, verified Phase 1 evidence, first-rule intake template, rule-authoring
contract, and next-chat checklist, read `.planning/app-site-formatting-customizations.md`.

---

## Loading overlay (`src/LoadingOverlayForm.cs` + `src/OverlayHost.cs`)

Borderless, topmost WinForms form. Uses `WS_EX_NOACTIVATE` + `WS_EX_TOOLWINDOW` to avoid stealing focus. Positioned at bottom-center of the primary screen's working area.

Shows per-phase status text via `SetPhase(SpellcheckPhase)`: `Copying` shows the form ("Copying text..."); `Sending`, `Waiting`, and `Pasting` swap the label only ("Sending to AI..." / "Waiting for AI..." / "Pasting..."); `Done` hides it. A right-aligned elapsed timer starts at `Copying`, updates every 100 ms, and stops when `Done` hides the overlay. The box is sized once at startup to the widest phase string and timer width (measured via `TextRenderer`) — it never wraps or resizes mid-run.

`OverlayHost` owns a dedicated STA background thread with its own message loop; the form and its Win32 handle are pre-created there at startup, and `SetPhase` calls from the async pipeline are queued via `BeginInvoke` so they return immediately and never block the hot path. Threading gotchas: `docs/watchlist.md` § Loading overlay UI-thread marshalling.

---

## Update service (`src/UpdateService.cs`)

Single update check: `CheckAsync(UpdateTrigger)` for `Launch`, `Periodic`, `ManualTray`, and `ManualDashboard`. The service owns one downloader and one check lock. State remains `Idle | Checking | Downloading(version) | UpdateReady(version) | UpToDate | Failed(reason)`.

- **Startup:** check for the newest release, verify/download it, and request automatic installation if ready. Velopack waits for the old process to exit, installs silently, and relaunches without command arguments. A current or failed check leaves the current app usable. Failure state shows a notification.
- **Background/manual checks:** prepare a verified download for the next restart. No install dialog or clickable ready notification. Manual checks may report that the app is current. The dashboard's `Restart to update` action uses the same restart request as the tray and terminal.
- **Terminal:** `--restart` (with `--update` retained as an alias) sends the existing channel-specific registered Windows message, using the original `BuildChannel.UpdateRequestMessage` wire name for compatibility. The app shows exactly one informational notice, waits for any active correction, and requests a graceful restart through `UpdateExe.Start(..., waitPid, [])`. Startup then follows the same update check as every launch. A failed check or restart is the exception to the one-notice success path: it notifies the user and leaves the current version available. No up-to-date or install-ready notification follows a successful terminal request.
- **Lifetime:** the app context owns shutdown and cleanup; `PrepareRestart` only starts Velopack's existing waiter. New hotkeys are blocked while startup updates or restart are underway. Command acknowledgement has a two-second bound and means accepted, not installed. If closed, the command launches the app with one notice and runs its normal startup update sequence.
- **Channels:** Dev never auto-updates and rejects both flags. Standalone/uninstalled builds skip update checks. Four-hour checks continue while Prod is running.

### Simplicity audit

Elliot's requirements are automatic updates on startup/restart, one terminal progress notice, an additional failure notice when needed, and a native command. Keep the existing Velopack downloader, concurrency lock, graceful PID waiter, and shared channel constants because they protect installation and user data. Delete the separate terminal install-intent state, command-only check trigger, install prompt, balloon click handler, and redundant pending-version eviction branch. Keep manual checking as an explicit user action and background downloading as preparation. No repository installer script, custom updater process, new update framework, or second download path is needed.

---

## WPF dashboard (`src/UI/`)

`MainWindow` hosts two pages in a `Frame`: **Home** → `ActivityPage`, **Settings** → `SettingsPage`. Sidebar nav is 240px; content area hosts the active page. Receives `UpdateService` reference; shows an update banner when state is `UpdateReady`.

### Activity feed (`ActivityPage`)

Reads the **shared** log corpus (`AppPaths.LogDirectory`, `spellcheck-{yyyy-MM-dd}.jsonl`) — same path for Prod and Dev. `NativeActivityLogReader` (in `ActivityPage.xaml.cs`) parses lines containing `spellcheck_detail` JSON blobs, including their optional `timings` objects; `DiagnosticsLogger` is used only to record `activity_load_failed` diagnostics. Rows with timing telemetry show a hover clock that lazily creates and toggles a compact phase breakdown. Older entries without timings render normally without the clock.

Startup and pagination are deliberately split across dispatcher turns:

1. `Loaded` starts the all-time statistics scan and first-page read on worker threads.
2. Only the first 30 parsed entries are materialized into WPF controls initially.
3. A viewport-fill check is queued at `DispatcherPriority.ContextIdle`, after WPF has measured the new content. It may request one page per dispatcher turn when the measured content does not fill the viewport, then re-measures before deciding whether another page is needed.
4. Scroll-triggered pages repeat the same read/yield/layout cycle. Pagination must never call itself synchronously.
5. Inline diffs are the initial view. Side-by-side controls and their second diff pass are created lazily when the user selects that view.
6. The LCS diff implementation has a matrix-size ceiling. Oversized inputs fall back to whole-text delete/insert segments instead of allocating an unbounded `n * m` matrix on the dispatcher.

This separation is a responsiveness contract. The WinForms message loop and WPF dispatcher share the startup thread; blocking dashboard rendering also blocks window paint, tray interaction, and `WM_HOTKEY` delivery.

| Concern | Implementation |
|---|---|
| Feed order | Newest first: daily files descending by date, lines within a file from EOF toward BOF; appended pages retain descending day and entry order |
| Pagination | 30 entries per page; `ActivityLogCursor` (file index + line index); infinite scroll near bottom + viewport fill |
| Stats bar | All-time checks, corrections, accuracy, day streak — full scan of all daily files |
| Diff UI | `InlineTextDiff` (line align + char LCS); optional side-by-side per row |
| Scroll | `SmoothScrollViewer` — smooth trackpad lerp, native mouse wheel, hidden scrollbar |
| Refresh | Clears `FeedItems` panel only; reloads stats + first page |

Successful rows require `status=success` with non-empty `input_text` and `output_text`. See `DESIGN.md` for visual contract.

### Settings (`SettingsPage`)

Adds, selects, and removes named API keys; only masked identifiers are displayed. Selection refreshes `CachedSettings`, so the next request uses the chosen key. It also opens the log folder and `replacements.json`. The Logs card exposes an optional developer
logging toggle, off by default; `CachedSettings` refreshes its value after a settings change so the
next correction can capture full clipboard sidecars and bounded native editor/window evidence.
Evidence serialization and worker waits are off the correction thread. The schema and limits are
in `docs/replacements-and-logging.md`. The Updates card shows current version, last updated, last checked, and calls `UpdateService.CheckAsync(ManualDashboard)`. The sidebar reload icon uses the same action.

---

## Auto-start (`src/StartupRegistration.cs`)

Writes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\{BuildChannel.MutexName}` pointing at the running exe. Dev skips auto-start registration. Prod registers on first launch only (guarded by a flag file in `AppDataDirectory`). Can be toggled from `SettingsPage`.

---

## Release pipeline (`.github/workflows/release.yml`)

Triggered by `v*.*.*` tags. Steps: checkout → dotnet publish Release win-x64 → `vpk pack` → `vpk upload github`. Version is injected from the tag; the csproj has no hardcoded `<Version>`. Delta packages and `RELEASES` manifest land as GitHub Release assets so installed copies pick them up on the next periodic check or launch.

---

## Training data layout

- `fine_tune_runs/` — one dated folder per fine-tune run: train/val JSONL, finetune_job.json, benchmark.json, summary.md.
- `benchmark_runs/` — one dated folder per standalone benchmark run.
- `tests/` — pytest suites for Python fine-tune dataset tooling. Reference `replacements.json` at repo root.

## Rust port in progress

- Build the Rust candidate under `rust/`; retain `src/` as the C# behavior reference until acceptance passes.
- Use `native-windows-gui` for native controls and direct documented Win32 APIs for clipboard ownership, input, DPAPI, and WinHTTP.
- Generate channel constants from `src/BuildChannel.cs`; default Cargo builds select Dev, while `--no-default-features` selects Prod.
- Keep the dashboard, correction worker, and logging independent so dashboard I/O and rendering cannot delay registered hotkeys.
- Preserve current-target paste, unchanged-text paste, captured-text history exclusions, corrected-text history inclusion, and existing encrypted named-key storage.
- Verify text, CF_HTML, native Docs bytes, mode and fallback metadata against the existing C# formatting tests using `rust/tests/parity.py`.
- Preserve the completed C# baseline in the primary checkout; ignored baseline files are not copied into the Rust worktree.
- Treat the Rust app as unfinished until tray/dashboard, updater, developer evidence, native acceptance and matched measurements pass.

## Rust implementation details

- `t3.rs` recognizes complete assistant-quote links copied by the desktop T3 Code
  composer. `Protection::for_target` protects each citation as one literal for the
  exact `T3 Code` / `T3 Code (Beta)` identities. Only selections containing those
  citations receive the explicit placeholder-preservation instruction. Ordinary T3
  text uses the existing protection/prompt; there is no new Markdown validator,
  code/formula/chip handling, or context-HTML rewriting. `--transform-json` accepts
  optional `active_exe` for deterministic target-path replay.

- Formatting consumes `diff::try_diff`, which returns a precise edit map or an explicit refusal. Above the small LCS budget, unchanged line boundaries anchor per-line edits; larger sparse changes use a bounded Myers search. A wholesale display diff must never be consumed as a formatting map. This supersedes the temporary mixed-list paragraph fallback, which only bypassed the underlying size-limit failure for one markup family.
- `rich_links.rs` reconciles Codex link chips with their HTTP(S) Markdown destinations in the copied Unicode before text alignment. HTML tags/attributes are tokenized independently of nesting, quoting, attribute order and inline styling. Repeated labels are matched by verified occurrence order; source/output destination sequences must agree. The same projection supplies HTML anchors and Unicode labels. Ambiguity retains original Unicode destinations and reports an unverified `link_status`; it never guesses a destination. Ordinary typed Markdown remains literal.

- Keep one correction worker with a clipboard-listener window and one outstanding correction; reject overlapping hotkeys rather than queuing them.
- Assign fully rendered clipboard memory to the tray's pumping window, avoiding an OLE clipboard writer while retaining the existing 50 ms clipboard settling period.
- Own clipboard backup formats before copy, including duplicated GDI/metafile objects; restore on failure only after a sequence change, and retain corrected text once published.
- Wait for physical modifiers before copy and paste; inject each chord with one `SendInput` batch and clean up partially inserted keys.
- Use one app-lifetime native WinHTTP session/connection with automatic proxy discovery and HTTP/2 enabled. Keep each request's body, headers and read buffer alive through `HANDLE_CLOSING`; enforce a 30-second deadline per attempt, matching two attempts and a 500 ms retry interval.
- Keep shared stamped log timestamps at event time while writing asynchronously; flush through a bounded barrier on orderly shutdown.
- Save the existing named DPAPI key collection with synchronized temporary-file contents and atomic replacement. Preserve corrupt files rather than overwriting them with defaults.
- Keep bounded screenshot and UIA workers off the correction path; preserve the existing cross-channel bounded evidence storage with oldest-first Rust retention and explicit failure/busy statuses.
- Queue and deduplicate restart requests during update checks. Use the existing updater's `start --waitPid` wire behavior, then let normal startup own the unified update check/install flow.
- Keep synthetic C# golden cases under `rust/tests/fixtures/`; do not add real runtime logs, copied selections or credentials to source control.

- Keep the dashboard lazy on its own native UI thread; load 30 activity rows before background statistics, forward wheel input from child controls, and bound text previews while copying full output.
- Use actual separate native rich-edit controls for side-by-side diffs and reset formatting before reuse. Handle close and deferred notices without holding UI borrows across a clipboard pump.
- Build with a static C runtime; package the executable, replacements and embedded-font licenses with `rust/package.ps1`. Candidate CI uploads this folder without changing the C# production release workflow.

- Verify developer clipboard readback synchronously before input injection with a single non-waiting open attempt. A post-input reader can lock the clipboard while the target attempts to paste. Persist full verification payloads only in evidence sidecars and retain summary metadata inline. This is publication verification, not proof the target rendered the text.
- Treat WinHTTP send/receive subphases as callback completion intervals, not directly comparable wire-upload/server-processing phases. Use whole `request_ms` for C#/Rust API comparisons.

- Use NWG's embedded-resource icon path and the local high-DPI screen-DC release patch (`rust/vendor/native-windows-gui/PROVENANCE.md`). The app uses native controls with a dedicated dashboard thread; no webview or renderer is loaded.
- Append dashboard diff text with native `EM_REPLACESEL`; NWG's convenience append replaces all text and erases previously applied diff styles. Bound previews and retain full copied output. Minimum dashboard tracking size scales 900 by 600 logical pixels to the active DPI.


### Acceptance-only updater build

- Compile `acceptance` only with `--no-default-features`. Bind a canonical root and one whitelisted prerelease version at build time; ordinary builds always use the existing GitHub source and channel behavior.
- Generate the QA package/mutex/display identity from `BuildChannel.cs`. Disable correction-hotkey registration and startup-registry writes for QA; retain the same updater worker, download/apply/restart logic and native dashboard.
- Validate canonical executable, data/log, feed and updater paths before bootstrap or restart forwarding. Validate the installed package ID before creating the private FileSource manager. Keep synthetic data outside the test install directory, and preserve production settings, credentials and startup registration.
- Run installed acceptance only after explicit idle authorization. The complete preparation and authorized run sequence are in `docs/bench.md`; `bench/prepare-rust-acceptance.py` only builds/packages, while mutation helpers require `--allow-desktop`.
