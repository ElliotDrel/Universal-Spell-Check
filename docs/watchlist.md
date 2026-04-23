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
- Current `script_version` — mismatched `script_version` is the first stale-reload check before assuming runtime is current.

**Affects more than Notepad.** Any app with repeated copy timeouts, menu activation, dropped pastes, or clipboard weirdness may need per-app handling.

**Candidate mitigations if it recurs:** send `{Esc}` before retries to clear menu state; increase Notepad retry spacing; use app-specific `SendEvent "^c"` / `SendEvent "^v"` for Notepad.

## Replacement cache
See `replacements-and-logging.md` → Watchlist section (repeated reload failures, same-size fast-edit edge case, read-while-writing risk).

## Script version verification
Bump `scriptVersion` before any commit touching `Universal Spell Checker.ahk` AND before asking for a reload/manual retest — the next log entry proves which build ran. Pre-commit must reject commits that include this file when the staged `scriptVersion` is not a numeric string or does not increase relative to `HEAD`.
