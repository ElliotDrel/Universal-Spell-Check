# Production / Dev Channel Split, Auto-Update, and Repo Cleanup

## Context

Today the C# WinForms app under `native/UniversalSpellCheck/` is the real product, but the repo is still organized around the legacy AutoHotkey script that originated it. There is no installer, no release process, and no way to run a stable build alongside in-progress code on the same machine — every change to the working tree affects the daily-driver hotkey. The user wants:

1. A **Prod** install that auto-updates from GitHub Releases so a `git tag` push reaches their machine(s) without manual steps.
2. A **Dev** flavor (the current repo checkout) that runs side-by-side with Prod under a different hotkey, with fully isolated settings/logs, so breaking changes during development cannot disrupt the daily-driver.
3. A repo whose top-level layout reflects that the C# desktop app is the primary subject. AHK script, Python proxy, JSONL log viewer, and old script versions are archived (not deleted) so the history is preserved but they stop framing the project.
4. A single, unified update code path used by every entry point (launch check, periodic check, tray "Check for Updates", dashboard "Update Now" button) — no parallel implementations.

Outcome: `git tag v1.2.3 && git push --tags` ships a release. Existing prod installs auto-update silently on next launch (or immediately if the user clicks Update Now). The repo's root folder makes it obvious this is a .NET desktop app.

## Recommended Approach

### A. Auto-update via Velopack

Adopt **Velopack** (free, MIT, modern Squirrel.Windows successor) as the update framework. It produces a Setup `.exe`, hosts release artifacts on GitHub Releases, and self-updates the installed app in the background without admin rights or code signing.

- Add `Velopack` PackageReference to the (renamed) csproj.
- In `Program.cs`, call `VelopackApp.Build().Run()` as the very first line of `Main` (Velopack hooks for first-run / restart-after-update must execute before any other startup code).
- Implement a single `UpdateService` class (new file) that wraps the unified update flow — see section D.
- Skip code signing. SmartScreen may warn on first install per machine; acceptable for a personal tool.

### B. Prod / Dev channel separation

Use an MSBuild build configuration `Dev` (in addition to default `Debug`/`Release`) that defines a `DEV` compile constant. All channel-specific values come from a single `BuildChannel` static class so changes never get out of sync.

`BuildChannel.cs` (new file) exposes:
| Member | Prod (Release) | Dev (`DEV` defined) |
|---|---|---|
| `DisplayName` | `Universal Spell Check` | `Universal Spell Check (Dev)` |
| `ChannelName` | `prod` | `dev` |
| `AppDataFolder` | `UniversalSpellCheck` | `UniversalSpellCheck.Dev` |
| `MutexName` | `UniversalSpellCheck` | `UniversalSpellCheck.Dev` |
| `Hotkey` | Ctrl+Alt+U (`VK_U = 0x55`) | Ctrl+Alt+D (`VK_D = 0x44`) |
| `TrayTooltip` | "Universal Spell Check" | "Universal Spell Check (Dev)" |
| `TrayIconTint` | none | orange tint applied to base icon at runtime |
| `IsDev` | `false` | `true` |
| `AppVersion` | from `Assembly.GetEntryAssembly().GetName().Version` (set at build time from git tag) | `0.0.0-dev` |

**Settings, API key, and per-channel state are isolated** under `AppDataFolder` (so Dev experiments cannot corrupt Prod settings). **Logs are deliberately NOT isolated** — see section H.

Files updated to consume `BuildChannel`:
- `native/UniversalSpellCheck/Program.cs:8` — replace literal `"UniversalSpellCheck.NativeSpike"` with `BuildChannel.MutexName`. Change message-box title to `BuildChannel.DisplayName`.
- `native/UniversalSpellCheck/AppPaths.cs:5-23` — `SettingsPath` and `ApiKeyPath` use `BuildChannel.AppDataFolder` (isolated per channel). `LogDirectory` is the **shared** location `%LocalAppData%\UniversalSpellCheck\logs\` (same path for both channels — see section H).
- `native/UniversalSpellCheck/HotkeyWindow.cs:10-13,34` — replace hardcoded `VkU` / modifier constants with values from `BuildChannel`.
- `native/UniversalSpellCheck/SpellCheckAppContext.cs:38-44` — tray `Text` and `Icon` come from `BuildChannel`. Dev tints `SystemIcons.Application` (or a future custom icon) by drawing a colored overlay on a `Bitmap` once at startup.

The Dev flavor is **not** auto-updated — `UpdateService` early-returns when `BuildChannel.IsDev` is true. Dev gets new code by `git pull` + `dotnet run`.

### C. Tag-driven release pipeline

GitHub Actions workflow `.github/workflows/release.yml` (new):

- **Trigger:** push of tag matching `v*.*.*`.
- **Steps:**
  1. Checkout, setup .NET 10.
  2. Parse semver from `${{ github.ref_name }}` (strip leading `v`).
  3. `dotnet publish src/UniversalSpellCheck.csproj -c Release -r win-x64 --self-contained false /p:Version=${SEMVER}`.
  4. `dotnet tool install --global vpk` (Velopack CLI).
  5. `vpk pack --packId UniversalSpellCheck --packVersion ${SEMVER} --packDir <publishDir> --mainExe UniversalSpellCheck.exe`.
  6. `vpk upload github --repoUrl ${{ github.server_url }}/${{ github.repository }} --token ${{ secrets.GITHUB_TOKEN }} --tag ${{ github.ref_name }}` (publishes installer + delta packages + `RELEASES` manifest as release assets).

The installed Prod app's `UpdateService` points its source at this same GitHub Releases URL; a tag push therefore propagates within the periodic-check window (or immediately on next launch).

The `<Version>` is **not** stored in `csproj` — it is injected at build time from the tag. Local Dev builds get a synthetic `0.0.0-dev` version.

### D. Unified update flow (`UpdateService`)

Single class. Single entry point: `public Task CheckAsync(UpdateTrigger trigger)` where `trigger ∈ { Launch, Periodic, ManualTray, ManualDashboardButton }`. Every UI affordance funnels into this method.

Behavior, called identically regardless of trigger:

1. If `BuildChannel.IsDev`, return immediately.
2. Query GitHub Releases for the latest published version via Velopack's `UpdateManager.CheckForUpdatesAsync()`.
3. If latest == current, set state `UpToDate` and return.
4. If a previously-downloaded pending update exists and its version differs from the latest, **delete it** and re-download (per user requirement: always converge on latest).
5. Download the new release in the background (`DownloadUpdatesAsync`).
6. Set state `UpdateReady(version)`. Notify subscribers (tray menu + dashboard button update via event).
7. If trigger is `ManualDashboardButton`, immediately call `ApplyUpdatesAndRestart()`. Otherwise leave pending; Velopack's `VelopackApp.Build().Run()` at next launch applies it silently.

State machine exposed as `UpdateState` enum: `Idle | Checking | Downloading | UpdateReady | UpToDate | Failed`. UI subscribes to a single `StateChanged` event.

Schedule:
- On launch (after `VelopackApp...Run()`, before tray icon shows).
- Every **4 hours** while the app is running, via a single `System.Threading.Timer` owned by `UpdateService`.
- Manually from tray "Check for Updates" or dashboard button.

### E. UI surfaces for update status

**Tray menu** (`SpellCheckAppContext.cs:65-72`) becomes:
1. Version row (disabled label) — shows either `v1.2.3` or `v1.2.3 — Update available (1.2.4)` when ready.
2. **Check for Updates** — calls `UpdateService.CheckAsync(ManualTray)`.
3. **Update Now** — visible only when state is `UpdateReady`; calls `UpdateService.ApplyUpdatesAndRestart()`.
4. ─── separator ───
5. Open Dashboard
6. Open Logs Folder
7. Quit

**Dashboard** (`UI/SettingsPage.xaml` / `.cs`): add a status banner at the top that is hidden when state is `UpToDate`/`Idle`, and shows "Update Available — vX.Y.Z" with an **Update Now** button when state is `UpdateReady`. Wires to the same `UpdateService` instance via DI/singleton.

### F. Repo restructure

Move:
- `native/UniversalSpellCheck/*` → `src/` (everything: `*.cs`, `*.csproj`, `UI/`, `README.md`, `CUTOVER.md`). Delete the now-empty `native/` directory.

Create `archive/ahk-legacy/` and move there:
- `Universal Spell Checker.ahk`
- `spellcheck-server.pyw`
- `Old Spell Check Version Files/` (entire folder)
- `generate_log_viewer.py`
- `.githooks/` (only bumps the AHK `scriptVersion`)

Add `archive/ahk-legacy/README.md` explaining what each file was and that they are kept for historical reference, not active use.

Keep at root unchanged: `replacements.json`, `tests/`, `benchmark_runs/`, `fine_tune_runs/`, `DESIGN.md`, `docs/`, `.gitignore`, `.env`.

Do **not** touch: `Untitled-1.md`, `AGENTS.md`.

Update at root:
- `README.md` (new top-level) — describes the desktop app, install link, hotkey, repo layout. Replaces AHK-as-primary framing.
- `CLAUDE.md` — rewrite Project Overview + Repo Map + Task Routing table to reflect new layout. Demote AHK to "archived legacy reference". Update file paths for native code (`src/...` instead of `native/UniversalSpellCheck/...`). Remove proxy recovery ladder from Hard Rules and replace with Velopack/release rules.
- `docs/architecture.md`, `docs/conventions.md`, `docs/replacements-and-logging.md`, `docs/watchlist.md`, `docs/debugging-principles.md`, `docs/model-config.md` — strip or rewrite AHK-specific sections; the C# side becomes primary.

### H. Unified logging across channels

**Goal:** keep all spell-check logs from Prod and Dev in one place so future fine-tuning / dataset work draws from a single corpus, but make every line attributable to the build that produced it (mirrors how the legacy AHK script stamps `script_version` on every entry).

- **Shared log directory:** `%LocalAppData%\UniversalSpellCheck\logs\` for **both** channels. `AppPaths.LogDirectory` returns this constant regardless of `BuildChannel` (it does NOT use `AppDataFolder`).
- **Filename convention:** `spellcheck-{YYYY-MM-DD}.jsonl` — single rolling daily file, **not split by channel**. Both Prod and Dev append to the same day's file. Two processes appending JSONL to the same file is safe on Windows when each line is written with a single `File.AppendAllText`/locked `FileStream` write; `DiagnosticsLogger` will use `FileShare.ReadWrite` + a short retry loop to handle the rare contention case.
- **Required fields on every log line** (added to `DiagnosticsLogger`):
  - `channel` — `"prod"` or `"dev"` (from `BuildChannel.ChannelName`)
  - `app_version` — semver of the running build (from `BuildChannel.AppVersion`); for Dev this is `"0.0.0-dev"` plus an optional short git SHA when running from a checkout
  - `pid` — process id, so two simultaneously-running channels can be disentangled
  - existing fields (event type, timing, model, request/response, etc.) remain unchanged
- **Filter convenience:** add a tray menu item (or update the existing "Open Logs Folder") so Prod and Dev both open the same folder. Downstream tooling (`tests/`, future fine-tune scripts) filters by the `channel` field; nothing relies on filename-based separation.
- **Log retention:** unchanged from current behavior. Old logs are preserved; rotation, if any, happens at the file-per-day granularity.

This is the **only** path in `AppPaths` that ignores `BuildChannel.AppDataFolder` — settings, API key, and Velopack staging stay isolated. Logs are intentionally shared.

### G. AppPaths replacements file lookup

`AppPaths.cs:25-48` currently walks up from `AppContext.BaseDirectory` to find `replacements.json`. After the move this still works for Dev (it walks up from `src/bin/...`). For Prod (Velopack-installed), the publish step copies `replacements.json` next to the exe via a `<None Include="..\replacements.json"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>` itemgroup in the csproj. The directory walk then finds it on the first try. No code change needed in `AppPaths`.

## Critical Files

**Modified:**
- `native/UniversalSpellCheck/UniversalSpellCheck.csproj` → `src/UniversalSpellCheck.csproj` — add Velopack PackageReference, `Dev` Configuration, `replacements.json` include, no hardcoded `<Version>`.
- `native/UniversalSpellCheck/Program.cs:1-46` → `src/Program.cs` — `VelopackApp.Build().Run()` first; mutex + exit message use `BuildChannel`.
- `native/UniversalSpellCheck/AppPaths.cs:5-23` → `src/AppPaths.cs` — folder name from `BuildChannel.AppDataFolder`.
- `native/UniversalSpellCheck/HotkeyWindow.cs:10-13,34` → `src/HotkeyWindow.cs` — hotkey from `BuildChannel`.
- `native/UniversalSpellCheck/SpellCheckAppContext.cs:38-44, 65-72` → `src/SpellCheckAppContext.cs` — tray text/icon from `BuildChannel`; tray menu reorganized with version row + Check/Update Now items wired to `UpdateService`.
- `native/UniversalSpellCheck/UI/SettingsPage.xaml(.cs)` → `src/UI/SettingsPage.xaml(.cs)` — add update banner + Update Now button bound to `UpdateService.StateChanged`.
- `CLAUDE.md`, `README.md` (new), all `docs/*.md`.

**Created:**
- `src/BuildChannel.cs` — channel constants, `IsDev`, hotkey codes, mutex name, app-data folder name.
- `src/UpdateService.cs` — unified update flow, state machine, periodic timer, GitHub Releases via Velopack.
- `.github/workflows/release.yml` — tag-triggered build, pack, publish.
- `archive/ahk-legacy/README.md`.

**Moved (no edits):**
- AHK script, Python proxy, log viewer, old version files, `.githooks/` → `archive/ahk-legacy/`.

## Reusing Existing Code

- **Single-instance mutex pattern** at `Program.cs:37-46` is kept as-is; only the literal name string is replaced with `BuildChannel.MutexName`.
- **Tray menu construction** in `SpellCheckAppContext.BuildMenu()` (`SpellCheckAppContext.cs:65-72`) is the natural insertion point; preserve its `ContextMenuStrip` pattern.
- **`AppPaths` static accessor pattern** stays — every caller already goes through it, so swapping the underlying folder name in one place propagates everywhere.
- **`LoadingOverlayForm`** is unrelated to update UI and stays untouched. Update banner lives in the existing `SettingsPage.xaml` dashboard, not a new window.

## Verification

After implementation, end-to-end checks:

**Local Dev flavor:**
1. `dotnet run -c Dev --project src/UniversalSpellCheck.csproj` launches app.
2. Tray tooltip reads "Universal Spell Check (Dev)"; tray icon visibly tinted.
3. Press **Ctrl+Alt+D** on selected text → spell check fires; **Ctrl+Alt+U** does nothing in this process.
4. Inspect `%LocalAppData%\UniversalSpellCheck.Dev\` — settings + API key live there. Inspect `%LocalAppData%\UniversalSpellCheck\logs\spellcheck-{today}.jsonl` — Dev log entries appear with `"channel":"dev"` and `"app_version":"0.0.0-dev"`. Run Prod simultaneously, trigger a spell check, and confirm both Prod (`"channel":"prod"`) and Dev entries land in the same daily file.
5. Tray "Check for Updates" → returns immediately with no network call (Dev short-circuit).

**Prod release flow:**
1. `git tag v0.1.0 && git push --tags`.
2. `release.yml` workflow succeeds in GitHub Actions; Release `v0.1.0` page has `Setup.exe`, `*-full.nupkg`, and `RELEASES` assets.
3. Download `Setup.exe` on a clean machine (or VM); install runs without admin prompt.
4. App launches; tray tooltip reads "Universal Spell Check"; **Ctrl+Alt+U** works; **Ctrl+Alt+D** does nothing.
5. Tray menu version row shows `v0.1.0`. Both Dev and Prod can be running simultaneously without mutex collision.

**Auto-update happy path:**
1. Prod app running on `v0.1.0`. Push tag `v0.1.1`; wait for workflow.
2. In running app, click tray "Check for Updates" → version row flips to "v0.1.0 — Update available (0.1.1)"; **Update Now** entry appears; dashboard shows banner.
3. Click **Update Now** → app restarts on `v0.1.1` within seconds.
4. Push tag `v0.1.2`. Without clicking anything, wait ≤4h (or restart the app) → on next launch app is running `v0.1.2`.
5. Stale download recovery: simulate a half-finished download, then publish a newer version → next check deletes the old downloaded package and fetches the newest.

**Repo cleanup:**
1. Root listing shows: `src/`, `archive/`, `docs/`, `tests/`, `benchmark_runs/`, `fine_tune_runs/`, `replacements.json`, `README.md`, `CLAUDE.md`, `DESIGN.md`, `Untitled-1.md`, `AGENTS.md`, `.github/`, `.gitignore`, `.env`.
2. `git log --follow archive/ahk-legacy/Universal\ Spell\ Checker.ahk` shows the full pre-move history (rename detected by Git).
3. `dotnet build src/UniversalSpellCheck.csproj` succeeds from the new location.
4. `tests/` still runs (`pytest tests/`) — they reference `replacements.json` at root, which has not moved.
