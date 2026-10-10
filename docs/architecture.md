# Architecture & File Layout

## Overview

The product is a C#/.NET 10 WinForms tray app with an embedded WPF dashboard, living under `src/`. It bootstraps via Velopack, registers a global hotkey, and runs a clipboard-capture → API-request → post-process → paste pipeline. AHK is archived at `.archive/ahk-legacy/` and is not part of the active system.

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

Dev tray icon is orange-tinted at runtime (draws a semi-transparent orange overlay onto `SystemIcons.Application`).

The dashboard auto-opens once on startup via `Application.Idle` so WPF resource failures surface immediately.

---

## Hotkey window (`src/HotkeyWindow.cs`)

Thin `NativeWindow` subclass. Calls `RegisterHotKey` with `BuildChannel.HotkeyModifiers` and `BuildChannel.HotkeyVk`. Raises `HotkeyPressed` on `WM_HOTKEY`. Prod: Ctrl+Alt+U. Dev: Ctrl+Alt+D. Both channels can run side-by-side without collision.

---

## Spell-check pipeline (`src/SpellcheckCoordinator.cs`)

Serialized via `SemaphoreSlim(1, 1)`. Overlapping hotkey presses are rejected (`guard_rejected reason=already_running`), never queued.

1. `SetPhase(Copying)` — tray text changes, `LoadingOverlayForm` shows with "Copying text...".
2. **Capture** — `ClipboardLoop.CaptureSelectionAsync()`. Waits for hotkey keys to release, snapshots the clipboard sequence number, sends Ctrl+C, waits for the sequence number to change, then polls for changed Unicode text. It reads **both** clipboard flavors in that same window: `CF_UNICODETEXT` drives the run, and `CF_HTML` is captured alongside it into `RunRecord.CapturedHtml` (`""` when the source offers none). The HTML read must happen here — step 4 empties the clipboard and the source markup is gone for good. At paste time, `RichTextClipboard` preserves verified simple LinkedIn messaging paragraphs using the captured CF_HTML producer URL, including empty paragraphs whose copied Unicode separators would otherwise add blank lines. It also handles verified ProseMirror paragraph and list shapes, then aligns corrections to original HTML text nodes for mixed spans, links, and lists. It keeps tags and attributes when edits map to source text nodes and also preserves HTML text nodes omitted from the Unicode clipboard flavor. Those omitted nodes are inserted into the replacement Unicode text as well. For one verified bare list item, it strips the generated marker and pastes only the corrected body as Unicode text because ChatGPT inserts an HTML block as a nested item. When rich reconstruction cannot be verified, corrected Unicode text still pastes with any recovered omitted HTML text.
3. On capture failure: restore original clipboard, notify user, log `capture_failed`, return.
4. **Exclude captured text from history** — `ClipboardLoop.ExcludeTextFromHistory()` tags the captured (incorrect) text out of Windows clipboard history (Win+V) so only the corrected text persists there. Best-effort, never fails the run; logs `capture_history_excluded` / `capture_history_exclude_failed`. Mechanism and gotchas: `docs/watchlist.md` § Clipboard history exclusion.
5. **Resolve target formatting** — `TargetFormattingPipeline` scans its short ordered rule list, freezes the first match, and runs its deterministic after-copy hook. The first rule is terminal normalization. No rule means the original string reference continues unchanged.
6. **Protect literals** — replace URLs, UUIDs/session IDs, API keys, file paths, and opaque IDs with collision-safe placeholders.
7. **Request** — the overlay reads "Sending to AI..." while the request body is written, then "Waiting for AI..." until response headers arrive. `OpenAiSpellcheckService` records separate send, wait, and response-download timings.
8. On request failure: restore clipboard, notify user, log `request_failed`, return.
9. **Post-process and restore** — `SetPhase(Pasting)` (overlay reads "Pasting..."), then `TextPostProcessor.Process(output, protection)`. Strips an exact echoed input followed by a correction divider, applies replacements, strips prompt-leak text, and restores every protected literal byte-for-byte. Missing or duplicated placeholders fail safely without a paste.
10. **Format for paste** — recapture the current foreground context and run the optional frozen before-paste hook. Before-paste rules receive formatter-neutral, private-use placeholders; a missing or duplicated placeholder skips the optional formatting and uses the already-corrected text.
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
