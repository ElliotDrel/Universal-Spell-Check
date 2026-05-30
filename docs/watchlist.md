# Runtime Watchlist

Items that have historically broken or have non-obvious behavior worth re-reading before touching.

---

## Velopack bootstrap order

`VelopackApp.Build().Run()` must be the **very first line of `Main`**, before mutex acquisition, WPF initialization, or any other startup code. Velopack installs first-run hooks and restart-after-update logic that must fire before the app does anything else. Moving it down breaks silent updates and first-run setup. This is a hard constraint, not a style preference.

---

## Clipboard / hotkey timing in `SpellcheckCoordinator`

`ClipboardLoop.CaptureSelectionAsync()` waits for hotkey keys to physically release before sending Ctrl+C. Watch for:

- `capture_failed reason="Clipboard did not change after Ctrl+C."` — the key-release wait may have been too short, or the target app didn't respond to Ctrl+C.
- `capture_failed reason="Copied selection was empty."` when text was actually selected — same timing issue, or the app uses a non-clipboard copy path.
- Unexpectedly high `capture_duration_ms`.
- Repeated `copy_attempts=2`.
- `paste_failed` with `expected_process` ≠ `actual_process` — the target app lost focus during the API request; this is not a capture failure, do not treat it as one.

Before adding app-specific timing rules: reproduce with a named target app and inspect the log timing fields. Don't adjust constants blindly.

---

## Clipboard history exclusion (Win+V)

**Goal:** the corrected text must always be retained in Windows clipboard history; the captured incorrect text should be kept out.

- **Corrected text in history (priority, guaranteed):** the corrected text is written via WinForms `Clipboard.SetText` with no exclusion tag and is the final clipboard state, so the OS history monitor captures it. Do **not** tag the corrected write as transient — that would drop it from history. (Nothing can force inclusion if the user has clipboard history disabled globally; that's an OS setting.)
- **Incorrect text out of history (best-effort):** `ClipboardLoop.ExcludeTextFromHistory` runs right after capture, on the STA thread. It takes clipboard ownership (`OpenClipboard(ownerHwnd)` → `EmptyClipboard` → re-place text → set `CanIncludeInClipboardHistory=0` + `CanUploadToCloudClipboard=0`). The owner HWND must be a **real window** (the hotkey window) — `OpenClipboard(NULL)` + `EmptyClipboard` sets the owner to NULL and makes `SetClipboardData` fail.
- This **races the OS history snapshot** (the source Ctrl+C already produced one untagged update). It wins in practice — this mirrors the proven legacy AHK `SetClipboardHistoryPolicy`. The WinRT `Clipboard.DeleteItemFromHistory` scrub is **not** an option: `GetHistoryItemsAsync` returns `AccessDenied` unless the calling app is foreground, and this tray app never holds focus during a run.
- Every run logs `captured_text_history_excluded=true|false` and an always-on `history_exclude_detail="text=… include=… upload=… cf_include=<id> cf_upload=<id> owner=0x…"`. `include=ok` means the incorrect text was tagged out of history; `include=fail(win32=…)` / `upload=fail(win32=…)` give the exact Win32 error to debug from. `open_clipboard_failed` / `empty_clipboard_failed` mean another process held the clipboard or the owner HWND was invalid.

---

## Loading overlay UI-thread marshalling

`SetBusy` is called from the async spell-check pipeline (not the UI thread). `LoadingOverlayForm.Handle` is force-created on the UI thread in `SpellCheckAppContext`'s constructor so `InvokeRequired` is meaningful. If you move or defer handle creation, `InvokeRequired` may return `false` on a background thread and crash.

`loading_overlay_failed` in logs means `ShowNearTaskbar()` or `Hide()` threw — check that handle creation is still happening before `Application.Run`.

See commits 52b5e27 and 77239cc for the specific WPF-in-WinForms crash sequence this fixed.

---

## WPF-in-WinForms: `System.Windows.Application` must exist

WinForms `Application.Run` does not create `Application.Current` for WPF. Without a `System.Windows.Application` instance:
- `DynamicResource` lookups walk the visual/logical tree and find nothing at the root, resolving to `DependencyProperty.UnsetValue`.
- This crashes at layout time: `'{DependencyProperty.UnsetValue}' is not a valid value for property 'Background'`.
- The `pack://application:,,,/` scheme is also only registered by the Application instance.

**Fix:** `new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }` instantiated before `Application.Run`, with `Styles.xaml` and `Components.xaml` merged into `app.Resources`.

If you see `wpf_resources_failed` in logs at startup, the dashboard will not work at all. If you see `dashboard_open_failed` or `ui_dispatcher_unhandled` with `DependencyProperty.UnsetValue`, check this anchor before chasing template or binding rewrites.

---

## WPF smoke tests must pump the Dispatcher

`window.Show(); window.Close();` returns before layout, template expansion, and resource resolution run. A smoke test that does only this is a false positive. The `--dashboard-smoke` mode pumps `DispatcherFrame`s for several seconds and hooks `Dispatcher.UnhandledException`. If a smoke test "passes" but the user still sees a crash, distrust the test first.

---

## Hotkey re-registration

`HotkeyWindow.Register()` calls `RegisterHotKey` with `BuildChannel.HotkeyModifiers` and `BuildChannel.HotkeyVk`. If registration fails (throws `Win32Exception`), another process already owns that hotkey combination. Prod owns Ctrl+Alt+U (`0x55`); Dev owns Ctrl+Alt+D (`0x44`). They can coexist. If registration fails for Prod, check whether a legacy AHK instance is still running. Dev failure means another Dev process is already up (check mutex first — that should have caught it).

---

## Mutex collision between channels

`BuildChannel.MutexName` is `"UniversalSpellCheck"` for Prod and `"UniversalSpellCheck.Dev"` for Dev. The mutex is acquired before `SpellCheckAppContext` is created. A second launch of the same channel shows a message box and exits 0 — it cannot get to hotkey registration. Distinct mutex names are what allow Prod and Dev to run side-by-side.

---

## Replacement cache edge cases

- **Same-size fast-edit** — cache key is `mtime + size`. A very fast edit that doesn't change file size can be missed until a subsequent save with a changed size.
- **Read-while-writing** — if the editor writes in two flushes, `TextPostProcessor` may read a partial file and cache it under the final metadata. Potential fix: capture metadata before and after the read, only accept if both match.

Watch `replacements_reload_failed` and `replacements_missing` in logs.

---

## UpdateService skips non-installed builds

`UpdateService.CheckAsync` returns early in two cases — both are correct, neither is a bug:
- Dev channel: `update_check_skipped reason=dev_or_uninstalled` (the entire update flow is disabled in Dev).
- Prod build run via `dotnet run` (no Velopack install metadata): `update_check_skipped reason=not_installed`.

---

## Dashboard auto-open on startup

`SpellCheckAppContext` hooks `Application.Idle` to auto-open the dashboard once on startup. This surfaces WPF failures immediately rather than requiring the user to find the tray menu. Check `dashboard_auto_open_attempt` and subsequent `dashboard_open step=` entries in the log if the dashboard doesn't appear. Step values: `construct` → `show` → `activate` → `done`. A missing `done` with an error after `construct` means the `MainWindow` constructor or XAML initialization threw.
