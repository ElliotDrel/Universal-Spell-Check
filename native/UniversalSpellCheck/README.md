# Universal Spell Check Native Spike

Phase 5 native Windows spike for the replacement plan. The core loop was proven
manually with `Ctrl+Alt+Y`; this pass hardens the Phase 2 OpenAI path for
daily-driver plain-text testing and ports selected AHK parity features.

## Scope

This intentionally does only:

- run as a single-instance resident WinForms tray app
- register `Ctrl+Alt+Y` globally
- copy selected plain text through the clipboard
- wait for the test hotkey keys to be released before sending `Ctrl+C`
- spellcheck captured text through OpenAI `gpt-4.1`
- replace the selection only after a successful request
- ignore overlapping hotkey presses
- store the API key encrypted with Windows DPAPI current-user protection
- log active app, capture timing, request timing, paste timing, copy attempts, request attempts, and failure categories
- retry one transient OpenAI failure before failing without pasting
- show a minimal tray busy state while a request is running
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

Use the tray icon menu to open logs or quit the app.
Use `Open Settings` from the tray icon menu to save your OpenAI API key.

Logs are written to:

```text
%LOCALAPPDATA%\UniversalSpellCheck\native-spike-logs\
```

The app exits duplicate launches instead of starting a second hotkey owner.

Settings are written to:

```text
%LOCALAPPDATA%\UniversalSpellCheck\settings.json
%LOCALAPPDATA%\UniversalSpellCheck\apikey.dat
```

`apikey.dat` is encrypted for the current Windows user through DPAPI and is not
plain text.

## Manual Acceptance Checks

1. Launch the app with no saved API key, trigger `Ctrl+Alt+Y`, and verify Settings opens without replacing the selected text.
2. Save a valid API key in Settings.
3. Select misspelled text in Notepad, press `Ctrl+Alt+Y`, and verify corrected text replaces the selection.
4. Select misspelled text in one browser textarea, press `Ctrl+Alt+Y`, and verify corrected text replaces the selection.
5. Press `Ctrl+Alt+Y` with no selected text and verify stale clipboard text is not pasted.
6. Press `Ctrl+Alt+Y` twice rapidly and verify only one replacement attempt runs.
7. Quit from the tray menu and verify the hotkey stops firing.

## Cutover

See [CUTOVER.md](CUTOVER.md) for the Phase 4 comparison, missing-feature list,
run instructions, and rollback path.
