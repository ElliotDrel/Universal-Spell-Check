# CLAUDE.md - Master System Resolver

> **CRITICAL MANDATE:** Do not invent logic or guess how to execute tasks in this repo. Before editing code, answering detailed questions, or running a skill, match the user's intent to the routing table below and **read the referenced doc FIRST**. Do not rely on default assumptions or memory from prior sessions - docs evolve.

---

## Project Overview

**Universal Spell Check** - a Windows-wide AI spell checker. The C#/.NET WinForms tray app under `src/` is the product: select text, press the hotkey, corrected text replaces the selection in place. The legacy AutoHotkey path is archived under `.archive/ahk-legacy/` for history only.

**Core value:** spell checking must feel instant and invisible - select, hotkey, done. Speed is the product.

**Stack:** C#/.NET 10 WinForms tray app + WPF dashboard (`src/`). Velopack for auto-update + GitHub Releases for distribution. OpenAI Responses API. Windows only. Python 3 used only for fine-tune dataset tooling under `tests/`. Builds with `dotnet build src/UniversalSpellCheck.csproj`.

**Channels:**
- **Prod** (`Release` config) — Ctrl+Alt+U, mutex `UniversalSpellCheck`, settings under `%LocalAppData%\UniversalSpellCheck\`. Auto-updates from GitHub Releases.
- **Dev** (`Dev` config, `DEV` compile constant) — Ctrl+Alt+D, mutex `UniversalSpellCheck.Dev`, settings under `%LocalAppData%\UniversalSpellCheck.Dev\`, tray icon orange-tinted. Never auto-updates; updated via `git pull` + `dotnet run -c Dev`.

**Logs are unified across channels:** both write to `%LocalAppData%\UniversalSpellCheck\logs\spellcheck-{date}.jsonl`. Every line is stamped with `channel`, `app_version`, and `pid` so downstream tooling can filter while keeping the corpus together for fine-tune use.

**Primary files:** `src/Program.cs` is the entrypoint. `src/BuildChannel.cs` holds all channel-conditional constants. `src/UpdateService.cs` is the single unified update flow used by every UI surface (launch / periodic timer / tray / dashboard). `replacements.json` at repo root is copied next to the exe at publish time.

**Tone when collaborating:** speed first, simplicity second, minimal UI/overhead third. Treat every added abstraction or fallback as a cost.

---

## Repo Map

```text
Universal Spell Check/
|-- src/                              # C#/.NET WinForms + WPF app (the product)
|   |-- UniversalSpellCheck.csproj    # Configurations: Debug | Release | Dev (DEV constant)
|   |-- Program.cs                    # VelopackApp.Run() first, then mutex + tray bootstrap
|   |-- BuildChannel.cs               # Prod/Dev constants: hotkey, mutex, paths, version
|   |-- AppPaths.cs                   # Settings/API isolated per channel; LOGS shared
|   |-- DiagnosticsLogger.cs          # JSONL append with channel/app_version/pid stamping
|   |-- HotkeyWindow.cs               # Win32 RegisterHotKey, hotkey from BuildChannel
|   |-- SpellCheckAppContext.cs       # Tray lifetime, menu (version + Check/Update Now)
|   |-- UpdateService.cs              # Unified update flow (Launch/Periodic/ManualTray/ManualDashboard)
|   |-- SpellcheckCoordinator.cs      # Capture/request/post-process/paste pipeline
|   |-- OpenAiSpellcheckService.cs    # Persistent HttpClient + Responses API
|   |-- TextPostProcessor.cs          # replacements.json + prompt-leak guard
|   |-- LoadingOverlayForm.cs         # Bottom-center loading progress bar
|   `-- UI/                           # WPF dashboard (MainWindow + Pages + Styles/Components)
|
|-- .github/workflows/release.yml     # Tag v*.*.* → publish + vpk pack + GitHub Release
|-- replacements.json                 # Post-processing brand/casing replacements (copied to publish)
|-- .archive/ahk-legacy/              # AHK + Python proxy + old version files (history only)
|-- logs/                             # Legacy AHK logs (no longer written to)
|-- benchmark_runs/, fine_tune_runs/  # Dated dataset/eval runs
|-- tests/                            # Pytest suites for Python fine-tune tooling
|-- docs/                             # Focused context docs - load via routing table below
|-- DESIGN.md                         # WPF dashboard visual design — read before visual changes
`-- CLAUDE.md                         # This file - routing table, not an encyclopedia
```

---

## 1. Task Routing - load the right doc before acting

| Intent | Read this first |
|---|---|
| Editing API payloads, switching models, anything about temperature/reasoning/verbosity | `docs/model-config.md` |
| Native app architecture, tray lifetime, hotkey, loading overlay | `docs/architecture.md` |
| Channel separation, hotkey mapping, app-data isolation, version stamping | `src/BuildChannel.cs` (canonical source) |
| Auto-update flow, Velopack, release pipeline | `src/UpdateService.cs`, `.github/workflows/release.yml` |
| Replacements system, prompt-leak guard, JSONL log fields | `docs/replacements-and-logging.md` |
| Debugging a bug, verification standards, runtime diagnostics | `docs/debugging-principles.md` |
| Clipboard/hotkey issues, loading overlay checks, cache edge cases | `docs/watchlist.md` |
| Naming, style, error-handling patterns, comments, C#/Python conventions | `docs/conventions.md` |
| Legacy AHK script behavior (only when reviving the fallback) | `.archive/ahk-legacy/` |
| Native dashboard UI / WPF / visual design / colors / fonts / mockups | `DESIGN.md` (always read before any visual change) |

If the task spans multiple areas, load each relevant doc before writing code.

---

## 2. Hard Rules (non-negotiable)

1. **Channels are owned by `BuildChannel`.** Never hardcode a hotkey, mutex name, app-data folder, or display string. Add the constant to `BuildChannel.cs` and consume it.
2. **Logs are shared, settings are isolated.** `AppPaths.LogDirectory` always returns the shared path; `AppDataDirectory` always uses `BuildChannel.AppDataFolder`. Do not split logs by channel — the unified corpus is required for fine-tune work, and per-line `channel`/`app_version` stamps are the filter.
3. **One update flow.** Every update entry point (launch, periodic, tray, dashboard) calls `UpdateService.CheckAsync(UpdateTrigger)`. Do not add parallel update code paths.
4. **Releases ship via tag.** A semver `v*.*.*` tag triggers `.github/workflows/release.yml`. Do not run `vpk` manually for production releases.
5. **Never mix reasoning + standard params.** Standard GPT uses `temperature`; reasoning models use `reasoning.effort`. See `docs/model-config.md`.
6. **Debug before fixing.** When root cause is unclear, add logging first, analyze, then fix. No guessing patches.
7. **Native retests require rebuild/relaunch.** A code change is not running until the process is stopped and rebuilt. Prod owns Ctrl+Alt+U; Dev owns Ctrl+Alt+D — they can run side by side.

---

## 3. Proactive Behavior

- After changes, review diffs for bugs without waiting for the user to ask.
- When file structure, flow, or config changes, update the relevant `docs/*.md` immediately - do not bloat this file.
- Ask clarifying questions up front when intent or scope is ambiguous (which channel? which model? which commit?).
