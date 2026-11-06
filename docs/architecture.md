# Architecture

One resident process per channel. `main.rs` runs Velopack bootstrap, then `app.rs` creates the
tray, the overlay, the dashboard thread, the update worker and one correction `Worker`
(`runtime.rs`). Native controls come from the vendored `native-windows-gui`; clipboard, input,
DPAPI and WinHTTP are direct Win32 calls. No webview or renderer is loaded.

## Startup

1. `VelopackApp::build().run()` must stay the first thing in `main`. Moving it breaks first-run hooks and restart-after-update. Implicit staged-package application is disabled so CLI forwarding cannot install before acknowledgement.
2. Legacy-data migration runs before any logger or settings read. It is copy-only and checkpointed, merges same-day logs without duplicates, and never deletes the source. It exists because a reinstall once replaced the installer root and destroyed the log corpus stored there.
3. A single-instance mutex per channel. A second launch exits. `--restart` (alias `--update`) instead forwards a channel-specific registered message to the running instance and exits once acknowledged; Dev rejects both flags.
4. The hotkey is registered with `RegisterHotKey` (Ctrl+Alt+U Prod, Ctrl+Alt+D Dev). Start-on-login is a Settings toggle that writes the `HKCU\...\Run` key; Dev never registers it.
5. `--startup-smoke` builds the real tray and exits. CI runs it because processing tests cannot catch tray construction failures (a constructor-order crash once shipped and killed every launch).

## A correction (`Worker::run` in `runtime.rs`)

One worker thread owns a clipboard-listener window and runs one correction at a time. A hotkey
during a run logs `guard_rejected reason=already_running` and is dropped, never queued.

1. **Backup**: snapshot every clipboard format the user had, including duplicated GDI/metafile handles.
2. **Capture**: wait for the physical hotkey modifiers to release, send Ctrl+C in one `SendInput` batch, wait for the clipboard sequence to change, then read Unicode text plus, independently, HTML, RTF and the Google Docs native MIME. Optional formats are best-effort: an oversized or unreadable one never aborts usable text. This is the only moment the source markup exists.
3. **History exclusion**: re-place the captured (incorrect) text tagged out of Win+V history and cloud clipboard. Best-effort and racing the OS snapshot; it wins in practice. The corrected text is written untagged so it does stay in history.
4. **Prepare**: terminal normalization for terminal processes, literal protection, payload build (`docs/formatting.md`).
5. **Request**: one app-lifetime WinHTTP session with proxy discovery and HTTP/2. Each attempt has a 30 s deadline; one retry after 500 ms on 408, 429 and 5xx.
6. **Post-process**: strip an echoed input or prompt leak, apply replacements, restore source line-end spacing, restore protected literals. A missing or duplicated placeholder fails the run without pasting.
7. **Rich mapping**: rebuild CF_HTML (and the Docs native slice) around the correction when it can be verified; otherwise paste corrected Unicode text.
8. **Paste**: write the clipboard, wait the 50 ms publication settle, wait for modifiers again, send Ctrl+V to whatever window is active now. Switching apps during the request is expected and not an error; capture and paste targets are both logged.
9. **Finish**: on failure, restore the user's clipboard (only after the sequence changed). On success the corrected text stays on the clipboard. Then write `run_completed` and one `spellcheck_detail`.

Unchanged text still pastes. Overlay phases (`Copying text...`, `Sending to AI...`,
`Waiting for AI...`, `Pasting...`) are sent over the bus. The overlay hides as soon as the hot
path returns, before any slow clipboard restore, so it never sticks after a failure.

## Traps that broke before

- NWG reserves raw event-handler IDs through `0xFFFF`. Use `style::raw_handler_id`; a low literal panics at window setup.
- `SendInput` returning means input is queued, not consumed. Never open the clipboard right after injecting Ctrl+V: a reader can hold it while the target pastes, leaving the selection unchanged. Developer readback runs before injection with one non-waiting open.
- NWG's high-DPI helper leaked a screen DC per query; the vendored patch releases it (`rust/tests/dpi_resources.rs`). Keep the vendor override until upstream ships the fix.
- Use embedded PE icon resources. NWG `source_bin` needs its optional image decoder and panics without it.
- Capture failures (`Clipboard did not change after Ctrl+C`) come in session bursts in some apps (historically Notepad and outlook.com). Each carries per-attempt forensics in `spellcheck_detail.events[]` (sequence numbers, modifier state, foreground elevation). Read those before theorizing; elevated targets silently drop injected keys (UIPI).

## Updates (`update.rs`)

One `Updater` worker serializes every check: launch, every four hours, tray, dashboard and
restart. Launch checks download and apply a newer release, then relaunch. Background and manual
checks only prepare a verified download for the next restart. Restart applies a pending package
(in memory or the SDK's cached one) before falling back to a plain restart, so a downloaded update
still installs when the feed is unreachable. Dev and uninstalled builds skip updates
(`update_check_skipped`). Releases come from `release.yml` on a version tag.

## Dashboard (`dashboard.rs`)

Runs on its own native UI thread, so it cannot delay the hotkey. It reads the shared log corpus:
the first 30 activity rows load before background statistics, more load on scroll, text previews
are bounded, and diffs have a bounded work budget. Side-by-side diffs use separate rich-edit
controls appended with `EM_REPLACESEL` (NWG's append replaces all text and erases styles). The
minimum tracking size is 900×600 logical pixels scaled to DPI. Settings manages named DPAPI keys
(saved atomically, a corrupt file is preserved, never overwritten), the model, start-on-login and
developer logging. Visual rules are in `DESIGN.md`.
