# Universal Spell Check

A Windows-wide AI spell checker. Select text anywhere, press **Ctrl+Alt+U**, and the corrected version replaces your selection in place. Lives in the system tray; the OpenAI Responses API does the work.

## Install

Download the latest `Setup.exe` from the [Releases](https://github.com/ElliotDrel/Universal-Spell-Check/releases) page and run it. No admin prompt, no code signing — Windows SmartScreen may warn on first install. The app checks GitHub Releases on launch and every ~4 hours, downloads an available update, and offers a one-click restart to install it.

You'll need an OpenAI API key. Open the dashboard from the tray icon, add a named key, and select it. You can store multiple keys and switch the active key without restarting.

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
.archive/ahk-legacy/       # The original AutoHotkey implementation (history)
tests/                     # Native tests plus Python fine-tune and benchmark tooling
```

## Development

```powershell
# Run a Dev build alongside the installed Prod app (Ctrl+Alt+D, separate settings)
dotnet run --project src/UniversalSpellCheck.csproj -c Dev

# Build a release-style local artifact
dotnet publish src/UniversalSpellCheck.csproj -c Release -r win-x64 --self-contained false
```

Prod and Dev settings are isolated (`%LocalAppData%\UniversalSpellCheck.Data\` vs `%LocalAppData%\UniversalSpellCheck.Dev\`). Logs are intentionally unified at `%LocalAppData%\UniversalSpellCheck.Data\logs\spellcheck-{date}.jsonl` with per-line `channel`, `app_version`, and `pid` stamps. `%LocalAppData%\UniversalSpellCheck\` is reserved for Velopack installation files.

## Release

Push a semver tag:

```bash
git tag v0.1.0
git push --tags
```

The GitHub Actions workflow builds the project, runs `vpk pack`, and uploads `Setup.exe` + delta packages to the GitHub Release. Installed Prod copies pick it up on the next periodic check or launch.

## License

Personal project. No license declared.
