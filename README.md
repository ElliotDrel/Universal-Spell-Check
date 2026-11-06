# Universal Spell Check

A Windows-wide AI spell checker. Select text anywhere, press **Ctrl+Alt+U**, and the corrected version replaces your selection in place. Lives in the system tray; the OpenAI Responses API does the work.

## Install

Download the latest `Setup.exe` from the [Releases](https://github.com/ElliotDrel/Universal-Spell-Check/releases) page and run it. No admin prompt, no code signing — Windows SmartScreen may warn on first install. The app checks GitHub Releases on launch and every ~4 hours. It installs a newer version automatically during startup. Background checks prepare updates for the next restart.

Technical users can restart and update from PowerShell:

```powershell
& "$env:LOCALAPPDATA/UniversalSpellCheck/current/UniversalSpellCheck.exe" --restart
```

The command sends one brief notice and restarts the app. Startup checks and installs any available update automatically; a failed update notifies you and keeps the current version usable. `--update` remains an alias. The launcher acknowledges the request; restart and installation complete asynchronously.


You'll need an OpenAI API key. Open the dashboard from the tray icon, add a named key, and select it. You can store multiple keys and switch the active key without restarting.

## Use

1. Select text in any app.
2. Press **Ctrl+Alt+U**.
3. Wait for the loading bar; corrected text replaces your selection.

The tray icon menu has version info, "Check for Updates", "Open Dashboard", and "Open Logs Folder".

## Repo layout

```
rust/                 # The app: native Rust tray, correction pipeline and dashboard
bench/                # End-to-end bench cases, recorded answers, perf budget, compare script
docs/                 # Architecture, formatting, logging, testing, model config
replacements.json     # Brand and casing fixes applied after the model
.agents/skills/       # Agent workflows: deploy, read-logs, formatting-troubleshoot
.github/workflows/    # CI with performance gates, tag-triggered release
_archive/             # Retired AHK app and fine-tune tooling (history only)
```

## Development

```powershell
# Dev build alongside the installed Prod app (Ctrl+Alt+D, separate settings)
cargo run --manifest-path rust/Cargo.toml --release

# Tests for both channels
cargo test --manifest-path rust/Cargo.toml
cargo test --manifest-path rust/Cargo.toml --no-default-features

# End-to-end bench: real hotkey, copy, correction and paste into a test editor
rust/target/release/universal-spell-check.exe --bench

# Release-style local folder
./rust/package.ps1 -Output rust/dist/prod
```

Prod and Dev settings are isolated (`%LocalAppData%\UniversalSpellCheck.Data\` vs `%LocalAppData%\UniversalSpellCheck.Dev\`). Logs are unified at `%LocalAppData%\UniversalSpellCheck.Data\logs\spellcheck-{date}.jsonl` with per-line `channel`, `app_version` and `pid` stamps. `%LocalAppData%\UniversalSpellCheck\` is reserved for Velopack installation files. See `docs/testing.md` for the bench and the CI performance gate.

## Release

Use the repository's deploy skill. A version tag on clean, verified `main` triggers the Rust release workflow: locked production build, regressions, startup smoke, Velopack packaging, fresh-installer execution on a disposable CI runner, then publication. The tag must match `rust/Cargo.toml`. Push the intended `main` commit and only its release tag together.

Rust releases retain the existing package ID, `UniversalSpellCheck.exe` name, installation path and update feed. Existing C# installations upgrade through their built-in restart/update flow; settings and encrypted keys remain under `%LocalAppData%\UniversalSpellCheck.Data`. New installs do not require .NET. Installer-owned program files are replaced during updates; user data remains outside that directory. Keep any shared .NET runtime installed for other applications.

## License

MIT. See [LICENSE](LICENSE).
