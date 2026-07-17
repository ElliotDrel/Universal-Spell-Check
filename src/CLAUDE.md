# src/ — Native App (the product)

## Grounding

This is **the product**: a C#/.NET 10 WinForms tray app + WPF dashboard. Select text → press hotkey → AI corrects it in place. **Speed is the product** — every ms and every added abstraction is a cost. Two channels (Prod = Ctrl+Alt+U, Dev = Ctrl+Alt+D) run side-by-side; all channel constants live in `BuildChannel.cs`.

## Read first

> Before editing code here, read root `CLAUDE.md` for routing + hard rules. For architecture (tray lifetime, hotkey, pipeline, overlay), read `docs/architecture.md`. For visual/WPF dashboard work, read `DESIGN.md` and `src/UI/CLAUDE.md`.

## Run (Dev channel)

```powershell
dotnet run --project src/UniversalSpellCheck.csproj -c Dev
```

Hotkey: Ctrl+Alt+D. Settings: `%LocalAppData%\UniversalSpellCheck.Dev\`. Logs (shared): `%LocalAppData%\UniversalSpellCheck.Data\logs\`.

## Run (Release / Prod-like local build)

```powershell
dotnet publish src/UniversalSpellCheck.csproj -c Release -r win-x64 --self-contained false -o publish
publish\UniversalSpellCheck.exe
```

Hotkey: Ctrl+Alt+U. Settings: `%LocalAppData%\UniversalSpellCheck.Data\`.

## Manual Acceptance Checks

1. Launch with no saved API key, trigger the hotkey, and verify Settings opens without replacing selected text.
2. Add two named API keys in Settings. Verify each shows only a masked identifier, switching the active key applies on the next hotkey request, and Dev retains its own separate list from Prod.
3. Select misspelled text in Notepad, press the hotkey, verify corrected text replaces the selection.
4. Select misspelled text in a browser textarea, press the hotkey, verify replacement.
5. During a request, verify the bottom-center loading overlay appears immediately ("Copying text..."), briefly shows "Sending to AI..." while the request body is written, shows "Waiting for AI..." while awaiting the response, then "Pasting...". Verify the right-aligned elapsed timer starts at `0.0s`, counts up throughout the run, and stops when the overlay disappears. The overlay must not steal focus, and its text must always remain on one line.
6. Press the hotkey with no selected text; verify stale clipboard text is not pasted.
7. Press the hotkey twice rapidly; verify only one replacement attempt runs (`guard_rejected reason=already_running` in log).
8. Select `open ai and github`, press the hotkey, verify output contains `OpenAI` and `GitHub`; confirm `replacements_count > 0` in log.
9. Quit from the tray menu; verify the hotkey stops firing.
10. Run Prod and Dev simultaneously; verify both appear in the tray with distinct icons and hotkeys, and both write entries to the same daily log file with correct `channel=` stamps.
11. With Windows Clipboard History enabled (Settings → System → Clipboard, or Win+V), correct a misspelled selection, then open Win+V. Verify the **corrected** text is in history and the original **incorrect** text is **not** (best-effort — see `docs/watchlist.md` § Clipboard history exclusion). Confirm `captured_text_history_excluded=true` in the log.
12. In Windows Terminal or VS Code's terminal, select soft-wrapped text containing a URL and file path. Verify terminal wrapping is normalized while both literals remain byte-identical, and confirm `target_formatting.rule_id=terminal` plus the legacy `terminal_normalization` counters in `spellcheck_detail`.
13. Start a correction, then switch to another window owned by the same executable before the request completes. Verify no paste occurs and the original clipboard is restored. An owned IME/autocomplete window with the same root owner must not cause a false rejection.

## Top-of-mind reminders

- A code change is not running until the process is stopped and rebuilt. **Always rebuild + relaunch before retesting.**
- Never hardcode hotkey/mutex/path/version strings. Add the constant to `BuildChannel.cs`.
- All update entry points must call `UpdateService.CheckAsync(UpdateTrigger)` — no parallel update paths.

---

## Keeping this file current

If the run commands, channel constants, manual checks, or Top-of-mind reminders drift from the code, fix this file in the same change. **If you notice drift while doing other work — stale step, removed file, wrong hotkey — flag it to the user and propose the fix.** Do not silently work around it.
