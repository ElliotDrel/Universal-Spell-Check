# Runtime Watchlist

## Clipboard / hotkey timing (Notepad and similar apps)
Watch for `Ctrl+Alt+U` being physically down when the script tries follow-up `Ctrl+C` / `Ctrl+V`.

**Symptoms:**
- Notepad menu/keytip overlays appearing.
- Clipboard wait timeouts.
- Copy succeeding only on attempt 2 or 3.
- Paste aborted because hotkey never fully released.

**Log signals to check:**
- `Clipboard copy attempt ... hotkey keys still physically down before Ctrl+C`
- `... released before Ctrl+C`
- `... still down after release wait`
- `Paste hotkey-release wait ...`
- Current `script_version` - mismatched `script_version` is the first stale-reload check before assuming runtime is current.

**Affects more than Notepad.** Any app with repeated copy timeouts, menu activation, dropped pastes, or clipboard weirdness may need per-app handling.

**Candidate mitigations if it recurs:** send `{Esc}` before retries to clear menu state; increase Notepad retry spacing; use app-specific `SendEvent "^c"` / `SendEvent "^v"` for Notepad.

## Native clipboard / hotkey timing

The native app uses `Ctrl+Alt+Y` for testing and has its own hotkey-release wait before sending `Ctrl+C`.

Watch for:
- `capture_failed ... reason="Clipboard did not change after Ctrl+C."`
- `capture_failed ... reason="Copied selection was empty."` when text was actually selected
- unexpectedly high `capture_duration_ms`
- repeated `copy_attempts=2`
- app focus changing between selection and paste

Known behavior:
- `Ctrl+Alt+U` is still owned by the AHK app until cutover.
- Native overlapping invocations should log `guard_rejected reason=already_running`.
- The loading overlay should show during request processing and disappear after success or failure.

Candidate mitigations if native capture regresses:
- verify the app was relaunched from the newly published EXE, not an older process
- inspect the latest native log before changing timing constants
- add app-specific capture/paste behavior only after a named target app reproduces the failure
- keep Notepad/browser textarea as baseline regression tests

## Replacement cache
See `replacements-and-logging.md` watchlist section (repeated reload failures, same-size fast-edit edge case, read-while-writing risk).

For native replacements, also watch `replacements_reloaded`, `replacements_reload_failed`, `replacements_count`, and `urls_protected` in `%LOCALAPPDATA%\UniversalSpellCheck\native-spike-logs\`.

## Script version verification
Bump `scriptVersion` before any commit touching `Universal Spell Checker.ahk` AND before asking for a reload/manual retest - the next log entry proves which build ran. Pre-commit must reject commits that include this file when the staged `scriptVersion` is not a numeric string or does not increase relative to `HEAD`.

Native changes do not require `scriptVersion` bumps, but they do require rebuilding/publishing and relaunching the native EXE before manual retest.
