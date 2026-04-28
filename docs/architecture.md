# Architecture & File Layout

## Active files
- **native/UniversalSpellCheck/**: C#/.NET WinForms main app. Uses `Ctrl+Alt+U`.
- **Universal Spell Checker.ahk**: Legacy/fallback AHK script with `modelModule` selector (`gpt-4.1`, `gpt-5.1`, `gpt-5-mini`). `scriptVersion` is a manual integer-like string near the top; bump before any commit that touches this file and before asking the user to reload/retest.
- **replacements.json**: Post-processing replacement pairs — `{ "canonical": ["variant1", ...] }`.
- **spellcheck-server.pyw**: Local proxy. Required. Script hard-fails if it cannot be started or recovered.
- **generate_log_viewer.py**: Reads `logs/*.jsonl` → `logs/viewer.html`. Run `python generate_log_viewer.py` (add `--open`).
- **export_openai_finetune_dataset.py**: Builds fine-tune datasets from live logs.
- **NATIVE_APP_FUTURE_TODO.md**: Root-level future work list for native cutover, reliability, parity, packaging, and rich-text/app-specific work.

## Native app layout
- `native/UniversalSpellCheck/Program.cs`: Single-instance app entrypoint; duplicate launches show a message and exit. Also bootstraps a `System.Windows.Application` (`ShutdownMode.OnExplicitShutdown`) and merges `UI/Styles.xaml` + `UI/Components.xaml` into `Application.Resources` before `Application.Run` so all WPF resource lookups have a top-level fallback. Hosts `--dashboard-smoke` mode which constructs the dashboard, pumps `DispatcherFrame`s, and fails on any `Dispatcher.UnhandledException`.
- `native/UniversalSpellCheck/SpellCheckAppContext.cs`: Tray lifetime, menu, settings window, busy text, and loading overlay ownership. Auto-opens the dashboard once on startup via `Application.Idle` so UI failures surface immediately rather than only when the user pulls the tray menu.
- `native/UniversalSpellCheck/HotkeyWindow.cs`: Win32 `RegisterHotKey` wrapper for `Ctrl+Alt+U`.
- `native/UniversalSpellCheck/ClipboardLoop.cs`: Clipboard-first capture/paste with hotkey-release wait, copy sentinel, bounded copy retry, and clipboard restore on failure.
- `native/UniversalSpellCheck/SpellcheckCoordinator.cs`: Serialized pipeline: capture -> request -> post-process -> paste. Uses non-queueing `SemaphoreSlim(1, 1)`.
- `native/UniversalSpellCheck/OpenAiSpellcheckService.cs`: App-lifetime `HttpClient`, fixed `gpt-4.1` request, request retry, response parsing, and failure categories.
- `native/UniversalSpellCheck/TextPostProcessor.cs`: Native `replacements.json` port plus prompt-leak guard.
- `native/UniversalSpellCheck/SettingsStore.cs`: DPAPI current-user API-key storage used by the dashboard and request path.
- `native/UniversalSpellCheck/UI/`: WPF dashboard opened from the tray. Shows recent successful native spell-checks from `spellcheck_detail` logs and provides API-key/log/replacements controls.
- `native/UniversalSpellCheck/LoadingOverlayForm.cs`: Borderless topmost bottom-center `Spell check loading...` progress bar shown without activation while the API request is running.
- `native/UniversalSpellCheck/README.md`: Native run/publish/manual-test instructions.
- `native/UniversalSpellCheck/CUTOVER.md`: Native-vs-AHK comparison, test evidence, missing features, and rollback path.

## Startup behavior
- AHK no longer has a Windows Startup shortcut.
- Native has a Windows Startup shortcut at:
```text
%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\Universal Spell Check Native.lnk
```
- The shortcut points to the published native executable:
```text
native\UniversalSpellCheck\publish\UniversalSpellCheck.exe
```

## Training data layout
- `fine_tune_runs/` — one dated folder per fine-tune run; contains train/val JSONL, finetune_job.json, benchmark.json, summary.md.
- `benchmark_runs/` — one dated folder per standalone benchmark run.

## Fine-tune refresh
```powershell
python export_openai_finetune_dataset.py --source logs --weeks 8 --max-per-bucket 15
```

## Legacy (reference only, do not use as template)
- `Universal Spell Checker - SEND TEXT instead of ctr+v.ahk` — minimal `SendText()` variant, no logging/post-processing.
- `Universal Spell Checker App/`, `Universal Spell Check - V1/V2/V3/V3.5.ahk`, `spellcheck*.js` — abandoned.

## Text processing flow

### AHK production flow
1. User selects text, presses Ctrl+Alt+U.
2. Replacements loaded once at startup; reparsed only when `replacements.json` modified-time or size changes.
3. At startup, script requires healthy proxy. Recovery ladder: 3 attempts with 5s / 30s / 60s readiness budgets; full shutdown+restart between attempts 2 and 3; `ExitApp` on exhaustion.
4. Default apps use the fast single-copy path. `notepad.exe` waits for hotkey release, retries `Ctrl+C` up to 3 times, and aborts paste if `Ctrl+Alt+U` never fully releases.
5. `GetClipboardText()` reads clipboard, preferring HTML (strips formatting) → Unicode → ANSI.
6. Pre-request proxy health check; reruns recovery ladder if proxy died mid-session.
7. Sends to local proxy → OpenAI Responses API via warm connection pool.
8. Parse: regex primary → Map-based fallback.
9. `ApplyReplacements()` fixes brand/term casing.
10. `StripPromptLeak()` strips echoed instruction blocks.
11. Paste via Ctrl+V or `SendText()` depending on `sendTextApps`.

### Native main-app flow
1. User selects text, presses Ctrl+Alt+U.
2. `HotkeyWindow` raises the app-level event from Win32 `WM_HOTKEY`.
3. `SpellcheckCoordinator` tries `SemaphoreSlim.WaitAsync(0)`; overlapping requests are rejected, not queued.
4. `ClipboardLoop` waits for hotkey keys to release, writes a sentinel to the clipboard, sends `Ctrl+C`, and polls for selected Unicode text.
5. After capture succeeds, tray text changes to checking state and `LoadingOverlayForm` shows the bottom-center loading bar without stealing foreground focus.
6. If capture fails, original clipboard data is restored and no paste occurs.
7. `OpenAiSpellcheckService` sends the fixed `gpt-4.1` Responses API request through a persistent `HttpClient`.
8. `TextPostProcessor` applies `replacements.json` with URL protection and strips leaked `instructions:` / `text input:` prompt text.
9. Before paste, the coordinator verifies the foreground process still matches the original target app. If focus moved, it restores the clipboard, logs `paste_failed`, and does not paste into the wrong app.
10. `ClipboardLoop` writes the final replacement to the clipboard and sends `Ctrl+V`.
11. Logs capture timing, request timing, post-process timing, paste timing, attempts, active app, paste target app, failure category, and a detail record with raw/base data to `%LOCALAPPDATA%\UniversalSpellCheck\native-spike-logs\`.
12. Loading overlay hides in `finally`, even on request/paste failure.

### Native dashboard flow
1. App startup auto-opens the dashboard once (via `Application.Idle`); the user can also reopen it from the tray menu's `Open Dashboard`.
2. `SpellCheckAppContext` opens the in-process WPF `UI/MainWindow`. Resources (`Styles.xaml`, `Components.xaml`) resolve through the `System.Windows.Application` instance created at startup; without that anchor `DynamicResource` lookups crashed with `'{DependencyProperty.UnsetValue}' is not a valid value for property ...'`.
3. Home reads the current native log file and renders successful `spellcheck_detail` entries as diff rows.
4. Settings saves the OpenAI API key through `SettingsStore`, opens the log folder, and opens `replacements.json`.
5. Hotkey capture, model switching, startup toggling, and a full replacements editor are deferred and must not be fake-wired.

## Design principles
- Speed first — every op optimized for latency.
- Self-contained .ahk files; only external dep is `replacements.json`.
- Direct clipboard manipulation for instant replacement.
- One file supports all target models via top-level selector.
- Native app is the main hotkey owner; AHK remains a fallback/reference path.
- Native feature work should preserve the proven plain-text loop before adding richer compatibility.

## Constraints
- Windows only, AutoHotkey v2.0 required.
- OpenAI API key with Responses API access.
- AHK has no build step.
- Native app requires .NET SDK for development; publish as self-contained single-file EXE:
```powershell
dotnet publish native\UniversalSpellCheck\UniversalSpellCheck.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o native\UniversalSpellCheck\publish
```
