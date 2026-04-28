# Universal Spell Check Native Spike

Phase 5 native Windows app for the replacement plan. The app now uses
`Ctrl+Alt+U` as the main spell-check hotkey.

## Scope

This intentionally does only:

- run as a single-instance resident WinForms tray app
- open a WPF dashboard from the tray menu
- register `Ctrl+Alt+U` globally
- copy selected plain text through the clipboard
- wait for the test hotkey keys to be released before sending `Ctrl+C`
- spellcheck captured text through OpenAI `gpt-4.1`
- replace the selection only after a successful request
- ignore overlapping hotkey presses
- store the API key encrypted with Windows DPAPI current-user protection
- log active app, capture timing, request timing, paste timing, copy attempts, request attempts, and failure categories
- retry one transient OpenAI failure before failing without pasting
- show a minimal tray busy state and non-activating bottom-center loading overlay while a request is running
- apply `replacements.json` post-processing with URL protection
- strip leaked instruction prompt text before paste

It intentionally does not include model selection, rich text, full JSONL viewer compatibility, or broad AHK feature parity.

## Run

```powershell
dotnet run --project native\UniversalSpellCheck\UniversalSpellCheck.csproj
```

## Publish

```powershell
dotnet publish native\UniversalSpellCheck\UniversalSpellCheck.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o native\UniversalSpellCheck\publish
```

Run the published executable:

```powershell
native\UniversalSpellCheck\publish\UniversalSpellCheck.exe
```

## Startup

The native app is the startup spellchecker. Windows Startup contains:

```text
%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\Universal Spell Check Native.lnk
```

The old AHK startup shortcut was deleted.

The dashboard auto-opens once on startup so any UI errors are visible immediately. After that, use the tray icon menu to reopen the dashboard, open the logs folder, or quit the app.

Logs are written to:

```text
%LOCALAPPDATA%\UniversalSpellCheck\native-spike-logs\
```

Each run writes concise event lines plus a `spellcheck_detail` line containing the base data needed for later backfill/reformatting: input text, output text, raw model output, raw response, request payload, token counts, timings, active app/window, paste target app/window, replacements, prompt-leak details, status, and errors.

The app exits duplicate launches instead of starting a second hotkey owner.

Settings are written to:

```text
%LOCALAPPDATA%\UniversalSpellCheck\settings.json
%LOCALAPPDATA%\UniversalSpellCheck\apikey.dat
```

`apikey.dat` is encrypted for the current Windows user through DPAPI and is not
plain text.

## Manual Acceptance Checks

1. Launch the app with no saved API key, trigger `Ctrl+Alt+U`, and verify Settings opens without replacing the selected text.
2. Save a valid API key in Settings.
3. Select misspelled text in Notepad, press `Ctrl+Alt+U`, and verify corrected text replaces the selection.
4. Select misspelled text in one browser textarea, press `Ctrl+Alt+U`, and verify corrected text replaces the selection.
5. During a request, verify the bottom-center `Spell check loading...` overlay appears after copy succeeds, does not steal focus, and disappears after replacement or failure.
6. Press `Ctrl+Alt+U` with no selected text and verify stale clipboard text is not pasted.
7. Press `Ctrl+Alt+U` twice rapidly and verify only one replacement attempt runs.
8. Select `open ai and github`, press `Ctrl+Alt+U`, and verify replacements produce `OpenAI` / `GitHub`; confirm `replacements_count` in the native log.
9. Quit from the tray menu and verify the hotkey stops firing.

## Cutover

See [CUTOVER.md](CUTOVER.md) for the Phase 4 comparison, missing-feature list,
run instructions, and rollback path.
