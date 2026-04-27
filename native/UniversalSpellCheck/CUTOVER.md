# Phase 4 Cutover Evaluation

Status as of 2026-04-27: native app is the main hotkey owner; AHK remains a fallback/reference path.

## Current Native App State

The native app is a single-process C#/.NET WinForms tray utility. It now uses `Ctrl+Alt+U`.

Implemented:

- resident tray app with single-instance guard
- global hotkey through `RegisterHotKey`
- clipboard-first plain-text capture and paste
- hotkey-release wait before `Ctrl+C`
- non-queueing re-entry guard
- OpenAI Responses API request path with app-lifetime `HttpClient`
- `gpt-4.1` request payload matching the current repo default model
- DPAPI current-user encrypted API key storage
- basic settings window
- active app, timing, copy-attempt, request-attempt, and failure-category logs
- one retry for transient request failures
- replacements engine with URL protection
- prompt leak guard for echoed instruction text
- non-activating bottom-center `Spell check loading...` overlay while requests are in progress
- paste-target foreground-process check before sending `Ctrl+V`
- rollback by quitting the native app and continuing to use the AHK script

Not implemented:
- HTML/rich-text capture or reinsertion
- Google Docs/contenteditable compatibility work
- model selection UI
- current AHK JSONL log schema and log viewer compatibility
- startup-on-login
- AHK-specific proxy recovery ladder, because the native app does not use the Python proxy

## Side-By-Side Behavior

| Area | Current AHK app | Native main app |
|---|---|---|
| Process model | AHK script plus required Python proxy | one WinForms process plus .NET runtime |
| Hotkey | `Ctrl+Alt+U` | `Ctrl+Alt+U` |
| Selection capture | clipboard-first, with app-specific timing workarounds | clipboard-first, with hotkey-release wait and bounded copy retry |
| Request path | local proxy forwards to OpenAI | in-process persistent `HttpClient` calls OpenAI |
| Model | configurable selector, default `gpt-4.1` | fixed `gpt-4.1` |
| API key | `.env` / environment loaded by AHK | DPAPI encrypted `apikey.dat` |
| Replacement | clipboard paste, with existing AHK edge-case logic | clipboard paste only |
| Diagnostics | rich JSONL logs and viewer | focused native spike log |
| In-progress feedback | AHK tooltip behavior | tray busy text plus non-activating bottom-center loading overlay |
| Feature parity | current production behavior | plain-text MVP only |

## Native Test Evidence

Evidence source:

```text
%LOCALAPPDATA%\UniversalSpellCheck\native-spike-logs\phase3-2026-04-27.log
```

Observed successful runs:

| App | Input length | Output length | Total duration | Capture | Request | Paste | Notes |
|---|---:|---:|---:|---:|---:|---:|---|
| VS Code | 118 | 114 | 2820 ms | 344 ms | 2313 ms | 125 ms | corrected selected text |
| Chrome | 22 | 27 | 1229 ms | 297 ms | 791 ms | 157 ms | corrected browser text field |
| VS Code | 115 | 114 | 2072 ms | 344 ms | 1572 ms | 140 ms | corrected selected text |
| VS Code | 114 | 114 | 1972 ms | 860 ms | 969 ms | 141 ms | rapid hotkey test completed once |

Observed failure-handling runs:

- no selected text: `capture_failed` with `reason="Copied selection was empty."`
- rapid repeated hotkeys: multiple `guard_rejected reason=already_running`, followed by one `replace_succeeded`

## Cutover Decision

Do not replace the AHK app yet.

The native app has proven the core plain-text loop and now owns `Ctrl+Alt+U`. It still has not proven full feature parity or broad app coverage.

Required before real cutover:

- test repeated Notepad use, Chrome/Edge textareas, VS Code/editor fields, and at least one app the user commonly writes in
- confirm the loading overlay appears after copy succeeds, does not steal focus, and hides on success or failure
- confirm replacements actively fire with a targeted replacement test
- decide whether AHK-style JSONL compatibility matters for log viewer/workflow continuity

Defer until after cutover decision:

- model selection UI
- rich-text/HTML paste
- Google Docs/contenteditable-specific behavior
- start-on-login
- full viewer integration

## User-Facing Run Instructions

Development run:

```powershell
dotnet run --project native\UniversalSpellCheck\UniversalSpellCheck.csproj
```

Published EXE run:

```powershell
native\UniversalSpellCheck\publish\UniversalSpellCheck.exe
```

Use the tray menu:

- `Open Settings` to save or replace the OpenAI API key
- `Open Logs Folder` to inspect native logs
- `Quit` to stop the native app and unregister `Ctrl+Alt+U`

## Rollback

Rollback is immediate:

1. Quit the native tray app.
2. Continue using the existing AHK app on `Ctrl+Alt+U`.
3. If needed, delete only the native app's local settings/logs:

```text
%LOCALAPPDATA%\UniversalSpellCheck\apikey.dat
%LOCALAPPDATA%\UniversalSpellCheck\native-spike-logs\
```

Do not delete or deprecate `Universal Spell Checker.ahk` during this phase.
