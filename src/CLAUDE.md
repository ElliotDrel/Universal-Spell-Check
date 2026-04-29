# Universal Spell Check — src/

C#/.NET 10 WinForms + WPF tray app. This is the product.

## Run (Dev channel)

```powershell
dotnet run --project src/UniversalSpellCheck.csproj -c Dev
```

Hotkey: Ctrl+Alt+D. Settings isolated to `%LocalAppData%\UniversalSpellCheck.Dev\`. Logs shared at `%LocalAppData%\UniversalSpellCheck\logs\`.

## Run (Release / Prod-like local build)

```powershell
dotnet publish src/UniversalSpellCheck.csproj -c Release -r win-x64 --self-contained false -o publish
publish\UniversalSpellCheck.exe
```

Hotkey: Ctrl+Alt+U. Settings at `%LocalAppData%\UniversalSpellCheck\`.

## Channels

Prod and Dev can run side-by-side. They use distinct hotkeys, mutex names, and AppData folders. Both write logs to the same shared directory. See `src/BuildChannel.cs` for all channel constants.

## Manual Acceptance Checks

1. Launch with no saved API key, trigger the hotkey, and verify Settings opens without replacing selected text.
2. Save a valid API key in Settings.
3. Select misspelled text in Notepad, press the hotkey, verify corrected text replaces the selection.
4. Select misspelled text in a browser textarea, press the hotkey, verify replacement.
5. During a request, verify the bottom-center loading overlay appears after copy, does not steal focus, and disappears after replacement or failure.
6. Press the hotkey with no selected text; verify stale clipboard text is not pasted.
7. Press the hotkey twice rapidly; verify only one replacement attempt runs (`guard_rejected reason=already_running` in log).
8. Select `open ai and github`, press the hotkey, verify output contains `OpenAI` and `GitHub`; confirm `replacements_count > 0` in log.
9. Quit from the tray menu; verify the hotkey stops firing.
10. Run Prod and Dev simultaneously; verify both appear in the tray with distinct icons and hotkeys, and both write entries to the same daily log file with correct `channel=` stamps.
