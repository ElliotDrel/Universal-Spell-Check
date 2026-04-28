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

The native app uses `Ctrl+Alt+U` and has its own hotkey-release wait before sending `Ctrl+C`.

Watch for:
- `capture_failed ... reason="Clipboard did not change after Ctrl+C."`
- `capture_failed ... reason="Copied selection was empty."` when text was actually selected
- unexpectedly high `capture_duration_ms`
- repeated `copy_attempts=2`
- app focus changing between selection and paste

Known behavior:
- `Ctrl+Alt+U` should be owned by the native app. If registration fails, check for a running legacy AHK spell checker.
- Native overlapping invocations should log `guard_rejected reason=already_running`.
- The loading overlay should show only after text capture succeeds, should not activate/focus itself, and should disappear after success or failure.
- `paste_failed` with different expected/actual processes means the target app lost focus before paste; do not treat that as a no-selection failure.

Candidate mitigations if native capture regresses:
- verify the app was relaunched from the newly published EXE, not an older process
- inspect the latest native log before changing timing constants
- add app-specific capture/paste behavior only after a named target app reproduces the failure
- keep Notepad/browser textarea as baseline regression tests

## Native dashboard (WPF)

Watch for:
- `dashboard_open_failed` or `ui_dispatcher_unhandled` with `error="'{DependencyProperty.UnsetValue}' is not a valid value for property '...'"` — almost always a regression in the WPF bootstrap (no `System.Windows.Application` instance, missing resource key, or merged dictionaries not loaded into `Application.Resources`).
- `wpf_resources_failed` at startup — `Styles.xaml` / `Components.xaml` did not load via the pack URI; the dashboard will not work until this is fixed.
- A `dashboard-smoke-*.log` that says `dashboard_smoke_ok` while the user still sees a crash — confirm the smoke mode is actually pumping `DispatcherFrame`s, not just `Show()`+`Close()`.
- `loading_overlay_failed` — the busy overlay couldn't show/hide. Check that `_loadingOverlay.Handle` is force-created on the main thread so `InvokeRequired` is meaningful.

The dashboard is auto-opened on startup; if it does not appear within a few seconds of launch, check the latest `phase*-YYYY-MM-DD.log` for `dashboard_open step=` and `dashboard_auto_open_attempt`.

## Replacement cache
See `replacements-and-logging.md` watchlist section (repeated reload failures, same-size fast-edit edge case, read-while-writing risk).

For native replacements, also watch `replacements_reloaded`, `replacements_reload_failed`, `replacements_count`, and `urls_protected` in `%LOCALAPPDATA%\UniversalSpellCheck\native-spike-logs\`.

## Script version verification
Bump `scriptVersion` before any commit touching `Universal Spell Checker.ahk` AND before asking for a reload/manual retest - the next log entry proves which build ran. Pre-commit must reject commits that include this file when the staged `scriptVersion` is not a numeric string or does not increase relative to `HEAD`.

Native changes do not require `scriptVersion` bumps, but they do require rebuilding/publishing and relaunching the native EXE before manual retest.
