# Native Windows App Replacement Spec

## Summary

This document defines the recommended replacement for the current AutoHotkey-based Universal Spell Checker. The recommendation is to build a native Windows tray app in `C# + .NET + WinForms` with a single always-running process and a persistent OpenAI client. For v1, the goal is intentionally narrow: replicate the current base loop of hotkey activation, selected-text capture, spellcheck request, and in-place text replacement.

The document is detailed enough to hand to another engineer for implementation. It includes the reasoning behind the recommendation, the architectural model, the MVP behavior, the phased path to later parity, the interfaces to introduce, and the expected test scenarios.

## Implementation Status As Of 2026-04-27

The native app has been implemented through the Phase 5 selective-parity pass in [native/UniversalSpellCheck](</C:/Users/2supe/All Coding/Universal Spell Check/native/UniversalSpellCheck>). It is still a replacement candidate, not the primary app. The current AutoHotkey app remains intact and should not be deleted or deprecated yet.

The native app currently uses `Ctrl+Alt+Y` for testing so it can run beside the existing AutoHotkey app on `Ctrl+Alt+U`.

### Completed

#### Phase 0: hard-loop spike

Completed:

- created a C#/.NET WinForms project at [native/UniversalSpellCheck](</C:/Users/2supe/All Coding/Universal Spell Check/native/UniversalSpellCheck>)
- proved the resident native process can receive a global hotkey
- proved selected plain text can be copied from the active app through the clipboard
- proved selected text can be replaced in place through clipboard paste
- used a dummy local transform first, before adding OpenAI
- verified the loop manually with `Ctrl+Alt+Y`

Important finding:

- `Ctrl+Alt+U` was already owned by the AutoHotkey app, so the native test hotkey was changed to `Ctrl+Alt+Y`.

#### Phase 1: minimal tray app shape

Completed:

- hidden WinForms startup through `ApplicationContext`
- tray icon with `Open Settings`, `Open Logs Folder`, and `Quit`
- explicit hotkey unregister on shutdown
- single-instance guard so duplicate launches do not create competing hotkey owners
- coordinator-owned pipeline with a non-queueing `SemaphoreSlim(1, 1)` guard
- minimal local file logging under `%LOCALAPPDATA%\UniversalSpellCheck\native-spike-logs\`

Important finding:

- the first refactor regressed capture because `Ctrl+C` was sent while the test hotkey keys were still physically down
- adding a hotkey-release wait before copy fixed the issue

#### Phase 2: OpenAI request path and secret storage

Completed:

- settings window for entering an OpenAI API key
- DPAPI current-user encrypted API key storage at `%LOCALAPPDATA%\UniversalSpellCheck\apikey.dat`
- app-lifetime `HttpClient`
- OpenAI Responses API request path
- fixed default model: `gpt-4.1`
- no model-selection UI
- request failure paths restore the original clipboard and do not paste over the selected text
- missing-key path opens Settings instead of crashing

Observed proof:

- missing-key attempt logged `error_code=missing_api_key`
- API key save logged `apikey_saved`
- successful request logged `replace_succeeded`

#### Phase 3: daily-driver reliability pass

Completed:

- active foreground app/window diagnostics
- capture timing
- request timing
- paste timing
- copy-attempt count
- request-attempt count
- HTTP status code logging on request failures
- one retry for transient OpenAI failures
- minimal tray busy state while a request is running
- no-selection behavior confirmed non-destructive
- rapid repeated hotkeys confirmed non-queueing

Observed proof from `phase3-2026-04-27.log`:

- VS Code selected text corrected successfully
- Chrome text field corrected successfully
- no-selection test logged `capture_failed` with `reason="Copied selection was empty."`
- rapid repeated hotkeys logged multiple `guard_rejected reason=already_running` events and only one replacement

Representative timings:

| App | Input length | Output length | Total duration | Capture | Request | Paste |
|---|---:|---:|---:|---:|---:|---:|
| VS Code | 118 | 114 | 2820 ms | 344 ms | 2313 ms | 125 ms |
| Chrome | 22 | 27 | 1229 ms | 297 ms | 791 ms | 157 ms |
| VS Code | 115 | 114 | 2072 ms | 344 ms | 1572 ms | 140 ms |
| VS Code | 114 | 114 | 1972 ms | 860 ms | 969 ms | 141 ms |

#### Phase 4: replacement candidate and cutover decision

Completed:

- created [native/UniversalSpellCheck/CUTOVER.md](</C:/Users/2supe/All Coding/Universal Spell Check/native/UniversalSpellCheck/CUTOVER.md>)
- documented native-vs-AHK behavior comparison
- documented current native test evidence
- documented missing features
- documented run instructions
- documented rollback path
- published a self-contained Windows executable at [native/UniversalSpellCheck/publish/UniversalSpellCheck.exe](</C:/Users/2supe/All Coding/Universal Spell Check/native/UniversalSpellCheck/publish/UniversalSpellCheck.exe>)

Decision:

- do not replace the AHK app yet
- continue daily-driver testing on `Ctrl+Alt+Y`
- rollback remains immediate: quit the native tray app and continue using AHK on `Ctrl+Alt+U`

#### Phase 5: selective parity porting

Completed:

- ported `replacements.json` post-processing
- replacements are flattened from canonical-to-variants into variant-to-canonical pairs
- replacements are sorted longest-first
- replacements are case-sensitive
- `https?://...` URLs are protected before replacement and restored afterward
- replacements reload when file metadata changes
- ported prompt-leak guard for echoed `instructions: ...`
- prompt-leak guard strips leading `text input:` after removing leaked instructions
- updated OpenAI prompt shape to match the AHK-style `instructions:` / `text input:` pattern
- added post-processing diagnostics:
  - `postprocess_duration_ms`
  - `replacements_count`
  - `urls_protected`
  - `prompt_leak_triggered`
  - `prompt_leak_removed_chars`
- publish output now contains only `UniversalSpellCheck.exe`, with `.pdb` excluded
- added a bottom-center `Spell check loading...` overlay that appears while the coordinator is busy and hides after success or failure

Observed proof from `phase5-2026-04-27.log`:

- replacements loaded successfully: `replacements_reloaded count=103`
- normal request path still succeeds after adding post-processing
- prompt-leak guard did not trigger on normal runs, as expected

Remaining verification gap:

- the app loaded replacements, but the latest manual run logged `replacements_count=0`; a targeted replacement test such as `open ai and github` still needs to prove the replacement pass actively changes output.
- the loading overlay was manually approved visually, but should remain part of future regression checks for success, capture failure, and request failure paths.

### Current Run And Build Commands

Development run:

```powershell
dotnet run --project native\UniversalSpellCheck\UniversalSpellCheck.csproj
```

Publish:

```powershell
dotnet publish native\UniversalSpellCheck\UniversalSpellCheck.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o native\UniversalSpellCheck\publish
```

Published executable:

```powershell
native\UniversalSpellCheck\publish\UniversalSpellCheck.exe
```

Current local app data paths:

```text
%LOCALAPPDATA%\UniversalSpellCheck\apikey.dat
%LOCALAPPDATA%\UniversalSpellCheck\native-spike-logs\
```

### Current Status Summary

The native app has proven the core plain-text flow:

1. select text
2. press global hotkey
3. copy selected text
4. call OpenAI with a persistent in-process client
5. post-process output
6. paste replacement text back into the original app

It is ready for continued daily-driver testing. It is not yet approved to take over `Ctrl+Alt+U` or replace the AHK app.

## Current State And Learnings

### What the current app actually is

The current product is not just "a spellcheck script." It is a Windows-wide text manipulation tool with a very specific interaction model:

1. User selects text in any app.
2. User presses `Ctrl+Alt+U`.
3. The app captures that selection.
4. The app sends the text to OpenAI.
5. The app pastes the corrected text back into the original app.

That core loop is implemented in [Universal Spell Checker.ahk](</C:/Users/2supe/All Coding/Universal Spell Check/Universal Spell Checker.ahk:1>). The current architecture also includes a persistent local Python proxy in [spellcheck-server.pyw](</C:/Users/2supe/All Coding/Universal Spell Check/spellcheck-server.pyw:1>) to keep network connections warm and reduce per-request overhead.

### What makes this app hard

The difficult part of the product is not rendering a desktop window. The difficult part is reliable system-wide text capture and replacement across arbitrary Windows applications.

That includes:

- global hotkey registration
- clipboard timing
- selection capture
- active-window variability
- paste reliability
- app-specific quirks
- Windows-specific input behavior

This matters because it changes the architecture decision. A desktop app shell is easy. A robust Windows text integration utility is not.

### What we learned from the current repo

The repo already contains the important signals:

- The product value is speed and invisibility, not UI richness.
- The current flow is clipboard-centric.
- The local proxy exists because warm connections materially help latency.
- Reliability issues cluster around capture/paste behavior, not around rendering UI.

The current architecture summary in [docs/architecture.md](</C:/Users/2supe/All Coding/Universal Spell Check/docs/architecture.md:1>) is consistent with that reading.

### What we learned from looking at `t3code`

The `t3code` repo is useful, but only at the architectural principle level.

Their desktop app works because:

- Electron hosts the desktop shell.
- A local backend process is spawned and managed by the shell.
- The backend handles the real work.
- The UI is mostly a local web app pointed at that backend.

That pattern is visible in:

- [AGENTS.md](</C:/Users/2supe/repo-search-storage/t3code/AGENTS.md:24>)
- [apps/desktop/src/main.ts](</C:/Users/2supe/repo-search-storage/t3code/apps/desktop/src/main.ts:1>)
- [apps/desktop/src/preload.ts](</C:/Users/2supe/repo-search-storage/t3code/apps/desktop/src/preload.ts:1>)

But `t3code` does not solve the same problem. It is not a system-wide Windows text replacement tool. It does not need to own selection capture, clipboard timing, or text reinsertion across arbitrary applications. So the important takeaway is not "use Electron." The important takeaway is:

- separate UI shell from runtime logic
- keep long-lived local state/processes where that improves responsiveness
- structure the app around a durable local runtime instead of per-action startup cost

That principle transfers. Their technology stack does not.

## Final Recommendation

Build a native Windows tray app using:

- `C#`
- `.NET`
- `WinForms`

Use a single process for v1. Do not keep the Python proxy as a separate component. Do not use Electron as the primary implementation stack.

### Why this is the best fit

This stack is recommended because it gives the shortest path to a functional replacement with the least technical mismatch:

- native Windows API access
- straightforward global hotkey support
- direct clipboard interop
- mature tray-app support (`NotifyIcon` is built in)
- less process complexity than a desktop shell plus separate helper in v1
- easier debugging for OS-level behavior than a web-runtime-based app

### Why not Electron

Electron is not wrong in the abstract. It is wrong for the core problem if used as the main implementation strategy.

Electron would still leave the hard part unsolved:

- system-wide text capture
- reliable reinsertion
- app-specific Windows behavior
- clipboard and input edge cases

So Electron adds UI/runtime overhead without removing the OS integration work that actually matters here.

### Why not WPF or WinUI 3

WPF and WinUI 3 are both heavier UI frameworks than this app needs. The app's risk is not visual polish or page composition. The risk is utility behavior and Windows integration. The entire UI surface is a tray context menu plus a small settings window — WinForms handles that with no XAML, no MVVM ceremony, and a built-in `NotifyIcon`. WPF's payoff (rich visual UI, data binding) is explicitly out of scope. WinForms is the lower-friction and better-proven choice for a small tray utility whose core difficulty is not the UI layer.

### Why not keep the Python proxy

The proxy exists today because the AHK script cannot naturally own a persistent, well-behaved HTTP client with the same ergonomics as a native application. A C# app can. Once the app is native, there is no reason to preserve a second local process for the networking layer in v1.

Instead:

- keep one persistent `HttpClient`
- keep the app resident in memory
- reuse connections inside the app process

That preserves the performance principle while simplifying the architecture.

## Product Goal

The v1 product goal is narrow:

Replicate the current base behavior of the app without introducing broader platform ambition or new product features.

That means:

- global hotkey
- capture selected text
- spellcheck through OpenAI
- replace selected text in place
- basic user configuration
- basic diagnostics

It does not mean full current parity on every behavior from day one.

## Scope Decisions

### In scope for v1

- tray/background native Windows app
- default hotkey `Ctrl+Alt+U`
- plain-text selection capture via clipboard flow
- OpenAI request execution with persistent `HttpClient`
- plain-text paste-back replacement
- API key entry and storage
- basic local logging
- basic error notifications
- re-entry guard so only one active spellcheck runs at a time

### Explicitly out of scope for v1

- HTML/rich-text reinsertion parity
- Google Docs formatting parity
- prompt leak guard parity
- replacements engine parity
- app-specific paste method rules
- model-switching UI (use the current default model only)
- history/stats UI
- viewer integration parity
- complete JSONL log schema parity
- SendText alternate output modes
- multi-process architecture

These are not rejected permanently. They are deferred so the implementation can first replace the core function.

## Architecture Overview

The app should be structured as one WinForms shell with clearly separated services.

### High-level runtime model

- one resident process
- one tray app
- one persistent network client
- one serialized spellcheck execution pipeline

### Subsystems

#### 1. AppShell

Responsibilities:

- startup
- tray icon
- settings window
- application lifetime
- notification surface

Implementation notes:

- app launches hidden (no main form shown — `Application.Run(new ApplicationContext())` pattern)
- tray icon via `NotifyIcon` with a `ContextMenuStrip`
- tray menu should include at minimum:
  - `Open Settings`
  - `Open Logs Folder`
  - `Quit`

#### 2. HotkeyService

Responsibilities:

- register and unregister the global hotkey
- raise an app-level event when the hotkey is pressed

Implementation notes:

- default hotkey is `Ctrl+Alt+U`
- use the Win32 `RegisterHotKey` API against a hidden message-only window (`HWND_MESSAGE` parent), and listen for `WM_HOTKEY` in that window's `WndProc`
- do not use polling and do not pull in a third-party hotkey package for v1
- hotkey may be fixed in code for the first implementation pass; configurability can come later

#### 3. SpellcheckCoordinator

Responsibilities:

- own the full hotkey-triggered pipeline
- ensure at most one request runs at once
- orchestrate capture, request, replace, and logging

Pipeline:

1. Try to acquire the re-entry guard.
2. If already held, ignore the invocation.
3. Capture selected text.
4. Validate that text exists.
5. Send it to `SpellcheckService`.
6. Receive corrected text.
7. Replace the selection through `ClipboardReplaceService`.
8. Log outcome and duration.
9. Release the guard.

Re-entry guard implementation:

- use `SemaphoreSlim(1, 1)` with `WaitAsync(0)` (non-blocking try-acquire)
- if acquisition fails, exit immediately — do not queue
- always release in a `finally` block

#### 4. ClipboardCaptureService

Responsibilities:

- capture currently selected text from the active application

Implementation notes:

- v1 should use the same conceptual approach as the current AHK implementation:
  - trigger copy
  - read clipboard
  - extract plain text
- v1 should target plain text only
- empty-selection behavior should fail fast with a clear message

#### 5. SpellcheckService

Responsibilities:

- build the request prompt
- send request to OpenAI
- parse the result into a replacement string

Implementation notes:

- create a single `HttpClient` during app lifetime
- reuse it for every request
- configure sensible request timeout
- keep the prompt behavior aligned with current spellcheck intent
- use the current default model — no model selection UI in v1
- keep the implementation small and deterministic

#### 6. ClipboardReplaceService

Responsibilities:

- write corrected text to clipboard
- paste the text back into the foreground app

Implementation notes:

- v1 should use clipboard-based paste only
- later phases can add richer modes if needed

#### 7. SettingsStore

Responsibilities:

- persist local settings
- store API key securely

Settings to support:

- API key
- optional start-on-login preference
- optional hotkey preference (deferred — not required for v1)

Storage expectations:

- use per-user app data for non-secret settings (`%LOCALAPPDATA%\UniversalSpellCheck\settings.json`)
- store the API key using **DPAPI** (`System.Security.Cryptography.ProtectedData.Protect` with `DataProtectionScope.CurrentUser`) — write the encrypted blob to a separate file (e.g. `apikey.dat`) so it never appears in plain text in `settings.json`

#### 8. DiagnosticsLogger

Responsibilities:

- log request lifecycle events
- provide enough data for debugging

V1 logging fields:

- timestamp
- status
- input length
- output length
- duration
- active app if available
- error message if failed

V1 logging should be intentionally smaller than the current AHK JSONL schema.

## Proposed Interfaces

These interfaces should exist in the implementation so the app can evolve without rewriting the whole composition.

### `IHotkeyService`

```csharp
public interface IHotkeyService
{
    event EventHandler? HotkeyPressed;
    void Register();
    void Unregister();
}
```

### `ICaptureService`

```csharp
public interface ICaptureService
{
    Task<CaptureResult> CaptureSelectionAsync(CancellationToken cancellationToken);
}
```

`CaptureResult`:

```csharp
public sealed class CaptureResult
{
    public bool Success { get; init; }
    public string? Text { get; init; }
    public string? FailureReason { get; init; }
}
```

### `ISpellcheckService`

```csharp
public interface ISpellcheckService
{
    Task<SpellcheckResult> SpellcheckAsync(string inputText, CancellationToken cancellationToken);
}
```

`SpellcheckResult`:

```csharp
public sealed class SpellcheckResult
{
    public bool Success { get; init; }
    public string? OutputText { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public long DurationMs { get; init; }
}
```

### `IReplaceService`

```csharp
public interface IReplaceService
{
    Task<ReplaceResult> ReplaceSelectionAsync(string replacementText, CancellationToken cancellationToken);
}
```

`ReplaceResult`:

```csharp
public sealed class ReplaceResult
{
    public bool Success { get; init; }
    public string? FailureReason { get; init; }
}
```

### `ISettingsStore`

```csharp
public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
    string? LoadApiKey();
    void SaveApiKey(string apiKey);
}
```

## Detailed V1 Behavior

### Startup behavior

On app launch:

1. Initialize logging.
2. Load settings.
3. Initialize persistent `HttpClient`.
4. Register global hotkey.
5. Show no main window.
6. Create tray icon and menu.

If API key is missing:

- the app should still run
- the first spellcheck attempt should fail with a clear message, or settings should prompt the user to enter the key

### Hotkey flow

When the user presses `Ctrl+Alt+U`:

1. Coordinator tries to acquire the re-entry guard.
2. If acquisition fails, exit the new invocation.
3. Capture service copies selected text and reads clipboard plain text.
4. If no text is captured, show a short failure message and stop.
5. Spellcheck service sends request to OpenAI.
6. If request fails, show a short failure message and stop.
7. Replace service writes output text to clipboard and pastes it back.
8. Log the run.
9. Release the guard.

### User feedback

For v1, use minimal feedback only:

- optional small in-progress indicator
- short error notifications (`NotifyIcon.ShowBalloonTip` is sufficient)
- no large workflow UI

The product should still feel invisible by default.

## OpenAI Request Model

### Model

Use the current default model from the existing AHK app. No model-switching UI in v1.

### Request behavior

The native app should preserve the current instruction intent:

- fix grammar and spelling
- preserve formatting as much as the plain-text pathway allows
- do not add or remove content unnecessarily

For v1, the main requirement is predictable output text that can replace the selection directly.

### Network strategy

- one persistent `HttpClient`
- reused for all requests
- do not create a new client per hotkey press
- set timeout at the service layer

This replaces the reason the Python proxy exists today.

## Storage And Paths

### App data

The app should use a per-user local application data directory for:

- settings file (`%LOCALAPPDATA%\UniversalSpellCheck\settings.json`)
- encrypted API key blob (`%LOCALAPPDATA%\UniversalSpellCheck\apikey.dat`)
- local logs (`%LOCALAPPDATA%\UniversalSpellCheck\logs\`)
- optional future cache/history

### Secret storage

The API key should not be hardcoded and should not live in plain text in the same settings file.

Use **DPAPI** (`System.Security.Cryptography.ProtectedData.Protect` with `DataProtectionScope.CurrentUser`):

- the encrypted blob is bound to the current Windows user account
- write the protected bytes to `apikey.dat` next to `settings.json`
- decrypt on read with `ProtectedData.Unprotect`
- the key is never committed to the repo and never written in plaintext

This satisfies:

- ordinary user can use the app normally
- developer does not need to hand-manage environment variables
- key is not committed into repo-managed configuration

## Distribution

### Packaging

Publish as a self-contained single-file executable so end users do not need to install .NET separately:

```
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true
```

Goals:

- one `.exe` to drop on a machine and run
- no "is .NET installed" friction
- preserves the install-and-go feel of the current AHK script

AOT compilation is out of scope for v1 (WinForms + DPAPI + dynamic JSON serialization makes AOT painful). Self-contained single-file is enough.

## Build Plan

This plan intentionally starts with the riskiest truth: can a native app own the Windows selection loop better than the AHK script? The first milestone is a hard-loop spike, not a polished app. It proves hotkey activation, selected-text capture, and replacement with a local dummy transform before any OpenAI, settings, DPAPI, detailed logging, or service-interface polish.

### Phase 0: Hard-Loop Spike

Goal:

Answer the core technical question as fast as possible:

Can a C# app reliably run `Ctrl+Alt+U -> copy selection -> replace selection` in Notepad and one browser textarea?

Deliverables:

- minimal C# WinForms project in a clearly named repo subfolder, such as `native/UniversalSpellCheck`
- minimal resident process, tray icon, or hidden form; whichever gets a message loop working fastest
- `Ctrl+Alt+U` registration through `RegisterHotKey`
- selected plain-text capture through the clipboard
- selection replacement through the clipboard
- local dummy transform only, such as appending `[checked]` or replacing with a known string
- minimal console/debug/file logging only where needed to debug the spike

Acceptance:

- Notepad selected text is captured and replaced by the dummy transform
- one browser textarea is captured and replaced by the dummy transform
- pressing the hotkey with no selected text does not paste stale clipboard contents
- pressing the hotkey twice rapidly does not run overlapping replacement attempts
- app can be exited cleanly enough for repeated manual testing

Stop conditions:

- do not add OpenAI
- do not add settings UI
- do not add DPAPI
- do not add detailed JSONL diagnostics
- do not add broad service-interface scaffolding unless the spike code is already becoming hard to reason about
- do not chase app-specific behavior beyond Notepad and one browser textarea

Decision after Phase 0:

- If the hard loop is reliable enough, keep the code and evolve it.
- If the hard loop is unreliable, stop and diagnose the Windows input problem before building any app surface.
- If C# does not materially improve the loop over AHK, reconsider whether a rewrite is worth it.

### Phase 1: Minimal App Shape

Goal:

Turn the successful spike into a small maintainable tray app without changing the proven capture/replace behavior.

Deliverables:

- hidden startup using `ApplicationContext`
- tray icon with `Open Logs Folder` and `Quit`
- explicit hotkey unregister on shutdown
- small coordinator around the proven hard-loop code
- non-queueing re-entry guard if the spike used an ad hoc guard
- simple local log file for hotkey, capture, paste, and guard failures
- project README or build notes with the run command

Acceptance:

- Phase 0 behavior still passes unchanged
- app launches without showing a main window
- tray `Quit` exits cleanly
- logs are available when capture or paste fails
- the app can be run repeatedly during development without stale background processes

Stop conditions:

- do not add OpenAI until the refactored app still passes the hard-loop tests
- do not add settings UI yet
- do not port AHK parity features

### Phase 2: OpenAI Request Path And Secret Storage

Goal:

Replace the local dummy transform with the real spellcheck request while keeping the process single, resident, and observable.

Deliverables:

- API key settings UI
- API key stored separately with DPAPI `CurrentUser` protection
- single app-lifetime `HttpClient`
- request timeout and cancellation path
- current-default-model request builder
- response parser that returns a direct replacement string
- clear user notification for missing key, invalid key, timeout, and request failure
- logs for model, input length, output length, request duration, and failure category

Acceptance:

- missing API key does not crash the app and gives a clear failure
- invalid API key does not paste over the original selection
- valid key spellchecks selected Notepad text in place
- valid key spellchecks one browser textarea in place
- a request failure leaves the user's original selected text unchanged
- repeated requests reuse the same running app process and do not start a helper process

Stop conditions:

- do not add model selection UI in this phase
- do not port replacements, prompt-leak guard, HTML handling, or rich logs yet

### Phase 3: Daily-Driver Reliability Pass

Goal:

Turn the MVP into something that can replace the AHK app for normal daily plain-text use by addressing only observed capture, paste, timeout, and feedback failures.

Deliverables:

- clipboard timing stabilization based on Phase 1 and Phase 2 logs
- active process/app name capture where practical
- bounded retry policy for transient network failures
- clearer in-progress and failure notifications
- startup recovery behavior for bad settings and failed hotkey registration
- diagnostics that distinguish capture failure, request failure, parse failure, and paste failure
- manual regression checklist covering the acceptance apps

Acceptance:

- Notepad passes repeated spellcheck attempts without menu/keytip interference
- browser textarea passes repeated spellcheck attempts
- no-selection hotkey remains non-destructive
- rapid double invocation remains non-queueing
- timeout and network failure paths are logged and do not paste stale output
- the app can be quit and relaunched without losing settings

Stop conditions:

- do not chase Google Docs or rich-content parity here unless plain text daily use is already stable
- do not introduce a second process unless a measured reliability problem requires isolation

### Phase 4: Replacement Candidate And Cutover Decision

Goal:

Decide whether the native app can become the primary daily app, and identify the smallest set of AHK-era features that must be ported before cutover.

Deliverables:

- side-by-side comparison against the current AHK flow
- latency comparison for common text lengths
- failure-mode comparison from logs
- list of AHK features still missing, grouped as required-before-cutover or defer
- user-facing run instructions for the native app
- rollback path to the AHK script

Acceptance:

- common Notepad and browser textarea use is at least as reliable as the AHK app
- latency is acceptable enough that the app still feels invisible
- no required daily workflow depends on a deferred feature
- the user can switch back to the AHK app if the native app fails

Stop conditions:

- do not delete or deprecate the AHK script during this phase
- do not claim parity unless the missing-feature list is empty or explicitly accepted

### Phase 5: Selective Parity Porting

Goal:

Port only the existing features that prove necessary after the native plain-text app is usable.

Candidate work, in likely order:

- replacements engine
- prompt leak guard
- richer logs or viewer export compatibility
- model selection UI
- app-specific capture/paste rules
- start-on-login preference

Acceptance:

- every ported feature has a concrete reason: observed regression, required workflow, or measurable support/debugging value
- each feature has its own rollback path and focused test case
- baseline hotkey/capture/request/paste behavior remains unchanged

Stop conditions:

- do not port a feature solely because the AHK app has it
- do not expand settings UI beyond features that are actually used

### Phase 6: Rich Text And App-Specific Compatibility

Goal:

Handle important apps or content types where plain text is not enough.

Candidate work:

- richer clipboard formats
- HTML-aware reinsertion
- contenteditable-specific behavior
- Google Docs or other targeted compatibility work
- app-specific paste method selection

Acceptance:

- each compatibility target is named before implementation
- the failure is reproduced and logged before a fix is added
- the fix does not degrade plain-text Notepad and browser textarea behavior

Stop conditions:

- do not start this phase before the native plain-text app is stable
- do not make rich text the default path until it is proven safer than plain text

## Risks And Tradeoffs

### Risk 1: Clipboard flow remains the true constraint

Even in native C#, clipboard-based replacement still inherits some of the same classes of timing and app-compatibility issues as the AHK version. The recommendation is still correct because it simplifies the stack and gives better control, but it does not remove the fundamental difficulty of cross-app text manipulation.

### Risk 2: Trying to reach full parity too early

If implementation tries to copy every existing AHK behavior into the first native version, complexity will spike immediately and the migration will slow down. That is the main failure mode to avoid.

### Risk 3: Overbuilding the UI

A polished UI is not the product's primary difficulty. Building heavy history/statistics/settings surfaces too early will consume time without proving the native replacement.

### Risk 4: Premature deep Windows integration work

It may become necessary later to go beyond the clipboard-first strategy for certain apps. That should be informed by observed failures, not assumed on day one.

## Test Plan

### Core scenarios

1. Select misspelled text in Notepad.
2. Press `Ctrl+Alt+U`.
3. Verify the selected text is replaced with corrected text.

1. Select misspelled text in a browser textarea.
2. Press `Ctrl+Alt+U`.
3. Verify replacement occurs in place.

1. Press hotkey with no selected text.
2. Verify app fails fast and does not paste stale content.

1. Press hotkey twice rapidly.
2. Verify only one request runs and no duplicate paste occurs.

1. Configure an invalid API key.
2. Trigger spellcheck.
3. Verify clear failure behavior and logging.

1. Restart the app.
2. Verify settings persist and hotkey remains active.

### Acceptance criteria for v1

- App launches as a tray/background utility.
- Default hotkey works globally.
- Plain-text selection capture works in Notepad.
- Plain-text replacement works in Notepad.
- Plain-text browser textarea flow works in at least one mainstream browser.
- Request failures do not result in accidental text replacement.
- No second local helper process is required for the request path.
- App ships as a self-contained single-file `.exe`.

## Implementation Notes For The Developer

### What to preserve from the current app

Preserve the product behavior, not the exact implementation shape.

That means:

- keep the same user loop
- keep the same invisibility principle
- keep warm network behavior
- keep low startup friction

It does not mean:

- keep AutoHotkey
- keep the Python proxy
- keep every existing log field
- keep every existing fallback behavior

### What to be conservative about

- Do not let the app perform overlapping spellcheck requests.
- Do not assume clipboard operations are synchronous enough without validation.
- Do not add feature surface until the base capture/replace loop is proven.
- Do not introduce a second process unless the first native process clearly cannot own the behavior.

### What success looks like

Success is not "feature parity with all current edge cases."

Success is:

- the native app can replace the AHK app for the common daily flow
- it is structurally simpler
- it keeps the performance model you care about
- it creates a base that can be hardened incrementally

## Final Decision

The app to build is:

- a native Windows tray app
- written in `C# + .NET + WinForms`
- using a single always-running process
- with a persistent in-process OpenAI client
- using `RegisterHotKey` against a message-only window for the global hotkey
- using DPAPI (`CurrentUser` scope) for API-key storage
- using `SemaphoreSlim(1, 1)` for the re-entry guard
- shipped as a self-contained single-file `.exe`
- implementing a clipboard-first spellcheck replacement loop

That is the simplest, fastest, and most technically coherent replacement for the current AutoHotkey app.
