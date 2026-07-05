# Architecture & File Layout

## Overview

The product is a C#/.NET 10 WinForms tray app with an embedded WPF dashboard, living under `src/`. It bootstraps via Velopack, registers a global hotkey, and runs a clipboard-capture → API-request → post-process → paste pipeline. AHK is archived at `.archive/ahk-legacy/` and is not part of the active system.

---

## Startup sequence (`src/Program.cs`)

1. `VelopackApp.Build().Run()` — **must be the very first line of `Main`**. Handles first-run hooks and restart-after-update. Safe no-op when running via `dotnet run`.
2. `AppPaths.EnsureDataMigration()` creates the safe data roots and copies or merges legacy Velopack-directory data newer than the previous migration checkpoint before any logger or settings service opens a file.
3. Single-instance mutex via `BuildChannel.MutexName`. A second launch shows a message box and exits 0.
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

- `AppDataDirectory` — `%LocalAppData%\{BuildChannel.AppDataFolder}`. Prod and Dev are fully isolated for settings and API keys. Prod uses `UniversalSpellCheck.Data`; Dev uses `UniversalSpellCheck.Dev`.
- `LogDirectory` — always `%LocalAppData%\UniversalSpellCheck.Data\logs\` regardless of channel. Both channels append to the same daily file `spellcheck-{yyyy-MM-dd}.jsonl`. Every line carries `channel`, `app_version`, and `pid`.
- `%LocalAppData%\UniversalSpellCheck\` is Velopack's installer-owned root. `AppPaths.EnsureDataMigration()` runs immediately after Velopack bootstrap and copies legacy settings, API key, state, and logs into the safe data root. It rechecks files newer than its checkpoint so writes made by an older installed version during rollout are not missed. Never place durable data back in the installer root; reinstall cleanup may replace it wholesale.
- `ReplacementsPath` — walks up from `AppContext.BaseDirectory` until `replacements.json` is found. Works for both dev checkout (`src/bin/...`) and Velopack-installed prod (file is copied next to the exe at publish time).

---

## Tray lifetime (`src/SpellCheckAppContext.cs`)

Owns all long-lived objects: `NotifyIcon`, `HotkeyWindow`, `SpellcheckCoordinator`, `UpdateService`, `LoadingOverlayForm`, and the WPF `MainWindow` reference.

Tray menu items:
1. Version label (disabled) — shows `v{version}` or `v{version} — Update available ({new})` / `— Downloading {ver}…` / `— Checking…`
2. Check for Updates → `UpdateService.CheckAsync(ManualTray)`
3. Update Now (hidden until `UpdateState.UpdateReady`) → `UpdateService.ApplyUpdatesAndRestartAsync()`
4. Separator
5. Open Dashboard → `ShowSettings()` (reuses existing window if open)
6. Open Logs Folder → `Process.Start(AppPaths.LogDirectory)`
7. Quit

Dev tray icon is orange-tinted at runtime (draws a semi-transparent orange overlay onto `SystemIcons.Application`).

The dashboard auto-opens once on startup via `Application.Idle` so WPF resource failures surface immediately.

---

## Hotkey window (`src/HotkeyWindow.cs`)

Thin `NativeWindow` subclass. Calls `RegisterHotKey` with `BuildChannel.HotkeyModifiers` and `BuildChannel.HotkeyVk`. Raises `HotkeyPressed` on `WM_HOTKEY`. Prod: Ctrl+Alt+U. Dev: Ctrl+Alt+D. Both channels can run side-by-side without collision.

---

## Spell-check pipeline (`src/SpellcheckCoordinator.cs`)

Serialized via `SemaphoreSlim(1, 1)`. Overlapping hotkey presses are rejected (`guard_rejected reason=already_running`), never queued.

1. `SetPhase(Copying)` — tray text changes, `LoadingOverlayForm` shows with "Copying text...".
2. **Capture** — `ClipboardLoop.CaptureSelectionAsync()`. Waits for hotkey keys to release, snapshots the clipboard sequence number, sends Ctrl+C, waits for the sequence number to change, then polls for changed Unicode text.
3. On capture failure: restore original clipboard, notify user, log `capture_failed`, return.
4. **Exclude captured text from history** — `ClipboardLoop.ExcludeTextFromHistory()` tags the captured (incorrect) text out of Windows clipboard history (Win+V) so only the corrected text persists there. Best-effort, never fails the run; logs `capture_history_excluded` / `capture_history_exclude_failed`. Mechanism and gotchas: `docs/watchlist.md` § Clipboard history exclusion.
5. **Protect literals** — replace URLs, UUIDs/session IDs, API keys, file paths, and opaque IDs with collision-safe placeholders.
6. **Request** — the overlay reads "Sending to AI..." while the request body is written, then "Waiting for AI..." until response headers arrive. `OpenAiSpellcheckService` records separate send, wait, and response-download timings.
7. On request failure: restore clipboard, notify user, log `request_failed`, return.
8. **Post-process and restore** — `SetPhase(Pasting)` (overlay reads "Pasting..."), then `TextPostProcessor.Process(output, protection)`. Applies replacements, strips prompt-leak text, and restores every protected literal byte-for-byte. Missing or duplicated placeholders fail safely without a paste.
9. **Focus check** — verify foreground process still matches original target. On mismatch: restore clipboard, log `paste_failed`, return.
10. **Paste** — writes corrected text to the clipboard (`Clipboard.SetText`, **untagged** so it IS kept in history), sends Ctrl+V. On success the corrected text is intentionally left on the clipboard (not restored).
11. Log `replace_succeeded` with full timing breakdown.
12. `SetPhase(Done)` in `RunAsync` the moment the hot path returns — loading overlay hides even on failure, and **before** the original-clipboard restore, which can block for seconds on failed runs while the OS renders the original clipboard formats.

---

## Loading overlay (`src/LoadingOverlayForm.cs` + `src/OverlayHost.cs`)

Borderless, topmost WinForms form. Uses `WS_EX_NOACTIVATE` + `WS_EX_TOOLWINDOW` to avoid stealing focus. Positioned at bottom-center of the primary screen's working area.

Shows per-phase status text via `SetPhase(SpellcheckPhase)`: `Copying` shows the form ("Copying text..."); `Sending`, `Waiting`, and `Pasting` swap the label only ("Sending to AI..." / "Waiting for AI..." / "Pasting..."); `Done` hides it. A right-aligned elapsed timer starts at `Copying`, updates every 100 ms, and stops when `Done` hides the overlay. The box is sized once at startup to the widest phase string and timer width (measured via `TextRenderer`) — it never wraps or resizes mid-run.

`OverlayHost` owns a dedicated STA background thread with its own message loop; the form and its Win32 handle are pre-created there at startup, and `SetPhase` calls from the async pipeline are queued via `BeginInvoke` so they return immediately and never block the hot path. Threading gotchas: `docs/watchlist.md` § Loading overlay UI-thread marshalling.

---

## Update service (`src/UpdateService.cs`)

Single entry point: `CheckAsync(UpdateTrigger)` where `UpdateTrigger ∈ { Launch, Periodic, ManualTray, ManualDashboardButton }`.

State machine: `Idle | Checking | Downloading(version) | UpdateReady(version) | UpToDate | Failed(reason)`.

Flow:
1. If `BuildChannel.IsDev` or not installed via Velopack: log skip, return.
2. Check for concurrent call via `SemaphoreSlim(1,1)`.
3. Query GitHub Releases via `GithubSource` + `UpdateManager.CheckForUpdatesAsync()`.
4. If no update: set `UpToDate`, return.
5. If stale pending download exists for a different version: evict it.
6. Download via `DownloadUpdatesAsync`. Set `UpdateReady`.
7. If `trigger == ManualDashboardButton`: call `ApplyUpdatesAndRestart` immediately.
8. Otherwise leave pending; Velopack applies it silently on next launch.

Periodic check: 4-hour `System.Threading.Timer` owned by `UpdateService`. Dev channel skips all update activity.

---

## WPF dashboard (`src/UI/`)

`MainWindow` hosts two pages in a `Frame`: **Home** → `ActivityPage`, **Settings** → `SettingsPage`. Sidebar nav is 240px; content area hosts the active page. Receives `UpdateService` reference; shows an update banner when state is `UpdateReady`.

### Activity feed (`ActivityPage`)

Reads the **shared** log corpus (`AppPaths.LogDirectory`, `spellcheck-{yyyy-MM-dd}.jsonl`) — same path for Prod and Dev. `NativeActivityLogReader` (in `ActivityPage.xaml.cs`) parses lines containing `spellcheck_detail` JSON blobs; `DiagnosticsLogger` is used only to record `activity_load_failed` diagnostics.

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
| Feed order | Newest first: daily files descending by date, lines within a file from EOF toward BOF |
| Pagination | 30 entries per page; `ActivityLogCursor` (file index + line index); infinite scroll near bottom + viewport fill |
| Stats bar | All-time checks, corrections, accuracy, day streak — full scan of all daily files |
| Diff UI | `InlineTextDiff` (line align + char LCS); optional side-by-side per row |
| Scroll | `SmoothScrollViewer` — smooth trackpad lerp, native mouse wheel, hidden scrollbar |
| Refresh | Clears `FeedItems` panel only; reloads stats + first page |

Successful rows require `status=success` with non-empty `input_text` and `output_text`. See `DESIGN.md` for visual contract.

### Settings (`SettingsPage`)

Handles API-key save/clear, opens log folder, and opens `replacements.json`. The dashboard "Update Now" button calls `UpdateService.CheckAsync(ManualDashboardButton)`.

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
