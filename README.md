# Universal Spell Check

A Windows-wide AI spell checker. Select text anywhere, press **Ctrl+Alt+U**, and the corrected version replaces your selection in place. Lives in the system tray; the OpenAI Responses API does the work.

## Install

Download the latest `Setup.exe` from the [Releases](https://github.com/ElliotDrel/Universal-Spell-Check/releases) page and run it. No admin prompt, no code signing — Windows SmartScreen may warn on first install. The app self-updates from GitHub Releases on launch and every ~4 hours.

You'll need an OpenAI API key. Open the dashboard from the tray icon and paste it in.

## Use

1. Select text in any app.
2. Press **Ctrl+Alt+U**.
3. Wait for the loading bar; corrected text replaces your selection.

The tray icon menu has version info, "Check for Updates", "Open Dashboard", and "Open Logs Folder".

## Repo layout

```
src/                       # The product — C# .NET 10 WinForms + WPF
.github/workflows/         # Tag-triggered Velopack release pipeline
replacements.json          # Brand/casing post-processing rules
docs/                      # Architecture, conventions, debugging notes
archive/ahk-legacy/        # The original AutoHotkey implementation (history)
tests/                     # Python fine-tune dataset tooling
```

## Development

```powershell
# Run a Dev build alongside the installed Prod app (Ctrl+Alt+D, separate settings)
dotnet run --project src/UniversalSpellCheck.csproj -c Dev

# Build a release-style local artifact
dotnet publish src/UniversalSpellCheck.csproj -c Release -r win-x64 --self-contained false
```

Prod and Dev settings are isolated (`%LocalAppData%\UniversalSpellCheck\` vs `UniversalSpellCheck.Dev\`). Logs are intentionally unified at `%LocalAppData%\UniversalSpellCheck\logs\spellcheck-{date}.jsonl` with per-line `channel` and `app_version` stamps.

## Release

Push a semver tag:

```bash
git tag v0.1.0
git push --tags
```

The GitHub Actions workflow builds the project, runs `vpk pack`, and uploads `Setup.exe` + delta packages to the GitHub Release. Installed Prod copies pick it up on the next periodic check or launch.

## License

Personal project. No license declared.
