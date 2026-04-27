# Native App Future Todo

This is the future work list for the C#/.NET WinForms native replacement in `native/UniversalSpellCheck`.

Current status as of 2026-04-27: the native app is a working plain-text replacement candidate on `Ctrl+Alt+Y`. The AutoHotkey app remains the primary app on `Ctrl+Alt+U`.

## Immediate Verification

- Prove replacements actively fire with a targeted test such as `open ai and github`.
- Check the Phase 5 log and confirm `replacements_count` is greater than `0`.
- Test daily writing apps beyond VS Code and Chrome.
- Keep collecting failure logs before adding app-specific behavior.

## Before Cutover To Ctrl+Alt+U

- Daily-drive the native app on `Ctrl+Alt+Y` long enough to catch real failures.
- Test repeated Notepad usage.
- Test Chrome and Edge textareas.
- Test VS Code/editor fields.
- Test the main apps where text is actually written day to day.
- Confirm no required workflow depends on rich text preservation.
- Decide whether the native app should take over `Ctrl+Alt+U`.
- Decide whether the AHK app should remain available as a fallback after hotkey cutover.

## Reliability Work

- Improve paste validation if logs show pasted text sometimes does not land.
- Add app-specific capture/paste rules only after a named app failure is reproduced.
- Track target-window changes between capture and paste if wrong-window paste is observed.
- Add clearer user notification for invalid key, timeout, rate limit, and server failure.
- Consider using the OpenAI `Retry-After` header for rate limits instead of a fixed retry delay.
- Add a small diagnostics command or log summary view if raw logs become hard to inspect.

## Feature Parity Candidates

- Model selection UI for `gpt-4.1`, `gpt-5.1`, and `gpt-5-mini`.
- AHK-style JSONL log schema compatibility.
- Log viewer integration or native log viewer.
- Start-on-login preference.
- Hotkey configuration UI.
- More complete settings persistence.
- Optional update/version display.

## Rich Text And App-Specific Compatibility

- Reproduce formatting loss in a named target app before adding rich-text logic.
- Investigate HTML clipboard capture and reinsertion only for apps where plain text is not enough.
- Investigate contenteditable/browser editor behavior after plain textarea reliability is stable.
- Investigate Google Docs only as a named compatibility target, not as a default requirement.
- Keep plain-text Notepad and browser textarea behavior as the baseline regression test for any rich-text changes.

## Release And Packaging

- Decide whether `native/UniversalSpellCheck/publish/UniversalSpellCheck.exe` should be checked in, distributed separately, or built on demand.
- Add a clean release script if publishing becomes routine.
- Add version display and release notes before handing the app to anyone else.
- Consider signing the executable if it becomes a real daily app.
- Decide whether settings/log folders should move from `native-spike-logs` to a production path.

## Cleanup And Documentation

- Rename "Native Spike" wording once the app is no longer experimental.
- Update `native/UniversalSpellCheck/CUTOVER.md` after more daily-driver evidence.
- Keep `Universal Spell Checker.ahk` untouched until cutover is explicitly accepted.
- Archive or clearly label old planning docs after the native app becomes primary.

## Deferred Unless Proven Necessary

- Full AHK feature parity.
- Multi-process architecture.
- Electron shell.
- WPF or WinUI rewrite.
- Rich history/stats UI.
- Google Docs-specific work before a reproduced failure.
- Replacing the Python proxy for the AHK app.
