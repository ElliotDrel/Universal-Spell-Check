# Architecture & File Layout

## Overview

The product is a C#/.NET 10 WinForms tray app with an embedded WPF dashboard, living under `src/`. It bootstraps via Velopack, registers a global hotkey, and runs a clipboard-capture → API-request → post-process → paste pipeline. AHK is archived at `.archive/ahk-legacy/` and is not part of the active system.

---

## Startup sequence (`src/Program.cs`)

1. `VelopackApp.Build().Run()` — **must be the very first line of `Main`**. Handles first-run hooks and restart-after-update. Safe no-op when running via `dotnet run`.
2. Single-instance mutex via `BuildChannel.MutexName`. A second launch shows a message box and exits 0.
3. Instantiate `System.Windows.Application` with `ShutdownMode.OnExplicitShutdown`. Merge `UI/Styles.xaml` and `UI/Components.xaml` into `app.Resources`. **Without this step**, WPF `DynamicResource` lookups crash (see watchlist).
4. `Application.Run(new SpellCheckAppContext())` — starts the WinForms message loop.

`--dashboard-smoke` mode can be passed to run a headless WPF layout pump and exit 0/1. Used for CI regression detection.

---

## Channel split (`src/BuildChannel.cs`)

All channel-specific values live in `BuildChannel` as `const` or static-read-only members. Never hardcode a hotkey, mutex name, data folder, or display string anywhere else.

| Member | Prod (`Release`) | Dev (`Dev` / `DEV` defined) |
|---|---|---|
| `IsDev` | `false` | `true` |
| `DisplayName` | `Universal Spell Check` | `Universal Spell Check (Dev)` |
| `ChannelName` | `prod` | `dev` |
| `AppDataFolder` | `UniversalSpellCheck` | `UniversalSpellCheck.Dev` |
| `MutexName` | `UniversalSpellCheck` | `UniversalSpellCheck.Dev` |
| `TrayTooltip` | `Universal Spell Check` | `Universal Spell Check (Dev)` |
| `HotkeyVk` | `0x55` (U) | `0x44` (D) |
| `AppVersion` | from `AssemblyInformationalVersion` (injected at build time from tag) | `0.0.0-dev` |

`IsDev` is a `const bool`, which means the compiler eliminates dead code at build time. CS0162 "unreachable code" warnings in channel-conditional blocks are expected and intentional.

---

## Settings isolation vs. unified logs (`src/AppPaths.cs`)

- `AppDataDirectory` — `%LocalAppData%\{BuildChannel.AppDataFolder}`. Prod and Dev are fully isolated (settings, API key, Velopack staging).
- `LogDirectory` — always `%LocalAppData%\UniversalSpellCheck\logs\` regardless of channel. Both channels append to the same daily file `spellcheck-{yyyy-MM-dd}.jsonl`. Every line carries `channel`, `app_version`, and `pid`.
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

1. **Capture** — `ClipboardLoop.CaptureSelectionAsync()`. Waits for hotkey keys to release, writes a sentinel to clipboard, sends Ctrl+C, polls for changed Unicode text.
2. On capture failure: restore original clipboard, notify user, log `capture_failed`, return.
3. `SetBusy(true)` — tray text changes, `LoadingOverlayForm` shows.
4. **Request** — `OpenAiSpellcheckService.SpellcheckAsync(text)`.
5. On request failure: restore clipboard, notify user, log `request_failed`, return.
6. **Post-process** — `TextPostProcessor.Process(output, promptInstruction)`. Applies replacements and strips prompt-leak text.
7. **Focus check** — verify foreground process still matches original target. On mismatch: restore clipboard, log `paste_failed`, return.
8. **Paste** — `ClipboardLoop.ReplaceSelectionAsync(replacement)`. Writes corrected text to clipboard, sends Ctrl+V.
9. Log `replace_succeeded` with full timing breakdown.
10. `SetBusy(false)` in `finally` — loading overlay hides even on failure.

---

## Loading overlay (`src/LoadingOverlayForm.cs`)

Borderless, topmost WinForms form. Uses `WS_EX_NOACTIVATE` + `WS_EX_TOOLWINDOW` to avoid stealing focus. Positioned at bottom-center of the primary screen's working area. Its Win32 handle is force-created on the UI thread at startup so `InvokeRequired` is meaningful when `SetBusy` is called from the async pipeline.

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

`MainWindow` hosts two pages in a `Frame`: `ActivityPage` (recent spell-check history from `spellcheck_detail` log entries) and `SettingsPage` (API key, log folder, replacements file). Receives `UpdateService` reference; shows an update banner when state is `UpdateReady`.

`SettingsPage` handles API-key save/clear, opens log folder, and opens `replacements.json`. The dashboard "Update Now" button calls `UpdateService.CheckAsync(ManualDashboardButton)`.

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
