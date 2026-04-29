# Project Overview

Full project context. Routed from root `CLAUDE.md` when overview, stack, or repo map is needed.

## What this is

**Universal Spell Check** — a Windows-wide AI spell checker. The C#/.NET WinForms tray app under `src/` is the product: select text, press the hotkey, corrected text replaces the selection in place. The legacy AutoHotkey path is archived under `.archive/ahk-legacy/` for history only.

**Core value:** spell checking must feel instant and invisible — select, hotkey, done. Speed is the product.

## Stack

- C#/.NET 10 WinForms tray app + WPF dashboard (`src/`)
- Velopack for auto-update + GitHub Releases for distribution
- OpenAI Responses API
- Windows only
- Python 3 used only for fine-tune dataset tooling under `tests/`
- Build: `dotnet build src/UniversalSpellCheck.csproj`

## Channels

- **Prod** (`Release` config) — Ctrl+Alt+U, mutex `UniversalSpellCheck`, settings under `%LocalAppData%\UniversalSpellCheck\`. Auto-updates from GitHub Releases.
- **Dev** (`Dev` config, `DEV` compile constant) — Ctrl+Alt+D, mutex `UniversalSpellCheck.Dev`, settings under `%LocalAppData%\UniversalSpellCheck.Dev\`, tray icon orange-tinted. Never auto-updates; updated via `git pull` + `dotnet run -c Dev`.

**Logs are unified across channels:** both write to `%LocalAppData%\UniversalSpellCheck\logs\spellcheck-{date}.jsonl`. Every line is stamped with `channel`, `app_version`, and `pid` so downstream tooling can filter while keeping the corpus together for fine-tune use.

All channel constants live in `src/BuildChannel.cs` — that file is the canonical source. Never hardcode channel-conditional values elsewhere.

## Primary files

- `src/Program.cs` — entrypoint. `VelopackApp.Run()` first, then mutex + tray bootstrap.
- `src/BuildChannel.cs` — Prod/Dev constants: hotkey, mutex, paths, version, display strings.
- `src/AppPaths.cs` — settings/API isolated per channel; logs shared.
- `src/UpdateService.cs` — single unified update flow (Launch / Periodic / ManualTray / ManualDashboard).
- `src/SpellcheckCoordinator.cs` — capture/request/post-process/paste pipeline.
- `src/OpenAiSpellcheckService.cs` — persistent HttpClient + Responses API.
- `src/TextPostProcessor.cs` — `replacements.json` + prompt-leak guard.
- `src/HotkeyWindow.cs` — Win32 `RegisterHotKey`, hotkey from `BuildChannel`.
- `src/SpellCheckAppContext.cs` — tray lifetime, menu (version + Check/Update Now).
- `src/LoadingOverlayForm.cs` — bottom-center loading progress bar.
- `src/UI/` — WPF dashboard (MainWindow + Pages + Styles/Components).
- `replacements.json` (repo root) — copied next to the exe at publish time.

## Repo map

```text
Universal Spell Check/
|-- src/                              # C#/.NET WinForms + WPF app (the product)
|   |-- UniversalSpellCheck.csproj    # Configurations: Debug | Release | Dev (DEV constant)
|   |-- Program.cs
|   |-- BuildChannel.cs
|   |-- AppPaths.cs
|   |-- DiagnosticsLogger.cs          # JSONL append with channel/app_version/pid stamping
|   |-- HotkeyWindow.cs
|   |-- SpellCheckAppContext.cs
|   |-- UpdateService.cs
|   |-- SpellcheckCoordinator.cs
|   |-- OpenAiSpellcheckService.cs
|   |-- TextPostProcessor.cs
|   |-- LoadingOverlayForm.cs
|   `-- UI/                           # WPF dashboard (MainWindow + Pages + Styles/Components)
|
|-- .github/workflows/
|   |-- release.yml                   # Tag v*.*.* -> publish + vpk pack + GitHub Release
|   `-- claude-agents-sync.yml        # Agent definitions sync
|-- replacements.json                 # Post-processing brand/casing replacements (copied to publish)
|-- .archive/ahk-legacy/              # AHK + Python proxy + old version files (history only)
|-- logs/                             # Legacy AHK logs (no longer written to)
|-- benchmark_runs/                   # Dated benchmark outputs (gitignored content; structure preserved)
|-- fine_tune_runs/                   # Dated fine-tune dataset/eval outputs
|-- tests/                            # Python fine-tune dataset + benchmark tooling
|-- docs/                             # Focused context docs - load via root CLAUDE.md routing
|-- DESIGN.md                         # WPF dashboard visual design - read before visual changes
`-- CLAUDE.md                         # Root resolver - routing table, not encyclopedia
```

## Tone when collaborating

Speed first, simplicity second, minimal UI/overhead third. Treat every added abstraction or fallback as a cost. Concise, action-oriented responses. Decide fast.
