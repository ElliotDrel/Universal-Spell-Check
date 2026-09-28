# Runtime Watchlist

Items that have historically broken or have non-obvious behavior worth re-reading before touching.

---

## Clipboard.* requires the STA thread — never call it from Task.Run

WinForms `Clipboard.*` throws `ThreadStateException` on MTA thread-pool threads. The v0.3.0 hot-path split moved the original-clipboard restore into the fire-and-forget `FinalizeAsync` (`Task.Run` → MTA): every **failed** run with a clipboard backup crashed finalize, so (a) the user's clipboard was never restored and (b) the entire `run_completed` + `spellcheck_detail` record was lost — failed runs became invisible and the post-0.3.0 "0% failure rate" was partly an illusion. Only a bare `finalize_failed error_type=ThreadStateException` line remained.

**Current shape:** the restore runs in `RunAsync` on the STA UI thread right after the hot path returns (failed runs only — successful runs keep the corrected text on the clipboard); `FinalizeAsync` does logging only and logs `finalize_failed` with status, active process, and full stack. If you move any `Clipboard.*` call, check which thread it lands on. A `finalize_failed` line in the logs means a run vanished — treat it as a missing-telemetry bug, not noise.

---

## Capture-failure forensics (open investigation: Notepad / outlook.com bursts)

"Clipboard did not change after Ctrl+C." hit **46% of Notepad runs** (and outlook.com-in-Chrome complaints) across the first 5 weeks of logs. It fails in session bursts — several consecutive hotkey presses all dead, other sessions 100% fine — which retries never recover. Ruled out: admin-elevated Notepad (user confirmed), overlay focus steal (WS_EX_NOACTIVATE), old clipboard lock contention (fixed v0.2.0, commit bc46808). **Root cause not yet proven.**

Since commit 808febb every capture failure carries per-attempt forensics in the `capture_failed` event inside `spellcheck_detail.events[]`:

- `seq_before` / `seq_at_timeout` — clipboard sequence numbers; unchanged means the target app never executed the copy.
- `mods_at_send` / `mods_at_timeout` — physical Ctrl/Alt/Shift/Win/hotkey-key state when Ctrl+C was injected; a modifier still `1` at send means the app saw Ctrl+Alt+C, not Ctrl+C.
- `fg_at_timeout` — `exe=… elevated=0|1|access_denied`; `elevated=1` or `access_denied` means UIPI silently dropped the injected keystrokes.

When the next burst happens, read these fields before theorizing. Reproduce with `--grep-detail` or `--event spellcheck_detail --app <exe>`.

---

## Velopack bootstrap order

`VelopackApp.Build().Run()` must be the **very first line of `Main`**, before mutex acquisition, WPF initialization, or any other startup code. Velopack installs first-run hooks and restart-after-update logic that must fire before the app does anything else. Moving it down breaks silent updates and first-run setup. This is a hard constraint, not a style preference.

Immediately after Velopack returns, run `AppPaths.EnsureDataMigration()` before constructing a logger or settings service. Velopack owns `%LocalAppData%\UniversalSpellCheck`; a manual reinstall of v0.6.1 replaced that directory and permanently deleted the historical JSONL corpus that had been stored beneath it. Durable Prod data now belongs in `%LocalAppData%\UniversalSpellCheck.Data`, while Dev settings remain in `%LocalAppData%\UniversalSpellCheck.Dev`. Never point `AppDataDirectory` or `LogDirectory` back at the unsuffixed installer root.

The migration is copy-only and checkpointed. It preserves any surviving legacy files, merges same-day logs without duplicate lines, and does not delete the source. Each startup rechecks files newer than the previous checkpoint so activity written by v0.6.1 during the rollout window is still captured. The checkpoint records migration start time, ensuring writes that race a migration are picked up on the next launch. `data_migration_completed`, `data_migration_skipped`, or `data_migration_failed` is the first native diagnostic written after migration.

---

## Clipboard / hotkey timing in `SpellcheckCoordinator`

`ClipboardLoop.CaptureSelectionAsync()` waits for hotkey keys to physically release before sending Ctrl+C. Watch for:

- `capture_failed reason="Clipboard did not change after Ctrl+C."` — the key-release wait may have been too short, or the target app didn't respond to Ctrl+C.
- `capture_failed reason="Copied selection was empty."` when text was actually selected — same timing issue, or the app uses a non-clipboard copy path.
- Unexpectedly high `clipboard_ms`.
- Repeated `copy_attempts=2`.
- A different paste target than capture target is expected when the user switches apps during the API request. Do not classify it as a paste or capture failure.

Before adding app-specific timing rules: reproduce with a named target app and inspect the log timing fields. Don't adjust constants blindly. Every capture failure now logs per-attempt forensics — see § Capture-failure forensics at the top of this file.

---

## Target-formatting cold start vs. warmed timing

`TerminalFormattingRule` uses three `RegexOptions.Compiled` expressions. Real Dev verification showed
one-time first-use cost after each process start: `after_copy_format_ms` was 183 ms in one new process
and 91 ms in another. Subsequent terminal runs in the same process logged 0 ms (whole-millisecond
timing resolution). This is consistent with compiled-regex/JIT startup rather than steady-state rule
cost.

When measuring a new formatting rule:

- Record the first matched run after process start and at least three warmed runs separately.
- Do not report the cold value as steady-state or discard it entirely; both affect perceived speed.
- Compare `target_formatting.operations` and exact transformed text so a fast no-op cannot look like a
  successful performance result.
- If cold cost becomes user-visible or repeats after startup, investigate source-generated regexes or
  off-hot-path prewarming based on measurements. Do not add speculative warming work to unmatched
  startup paths.

---

## Clipboard history exclusion (Win+V)

**Goal:** the corrected text must always be retained in Windows clipboard history; the captured incorrect text should be kept out.

- **Corrected text in history (priority, guaranteed):** the corrected text is written via WinForms `Clipboard.SetText` with no exclusion tag and is the final clipboard state, so the OS history monitor captures it. Do **not** tag the corrected write as transient — that would drop it from history. (Nothing can force inclusion if the user has clipboard history disabled globally; that's an OS setting.)
- **Incorrect text out of history (best-effort):** `ClipboardLoop.ExcludeTextFromHistory` runs right after capture, on the STA thread. It takes clipboard ownership (`OpenClipboard(ownerHwnd)` → `EmptyClipboard` → re-place text → set `CanIncludeInClipboardHistory=0` + `CanUploadToCloudClipboard=0`). The owner HWND must be a **real window** (the hotkey window) — `OpenClipboard(NULL)` + `EmptyClipboard` sets the owner to NULL and makes `SetClipboardData` fail.
- This **races the OS history snapshot** (the source Ctrl+C already produced one untagged update). It wins in practice — this mirrors the proven legacy AHK `SetClipboardHistoryPolicy`. The WinRT `Clipboard.DeleteItemFromHistory` scrub is **not** an option: `GetHistoryItemsAsync` returns `AccessDenied` unless the calling app is foreground, and this tray app never holds focus during a run.
- Every run logs `captured_text_history_excluded=true|false` and an always-on `history_exclude_detail="text=… include=… upload=… cf_include=<id> cf_upload=<id> owner=0x…"`. `include=ok` means the incorrect text was tagged out of history; `include=fail(win32=…)` / `upload=fail(win32=…)` give the exact Win32 error to debug from. `open_clipboard_failed` / `empty_clipboard_failed` mean another process held the clipboard or the owner HWND was invalid.

---

## Clipboard fidelity — a correction flattens the selection's formatting

The clipboard path reads `CF_UNICODETEXT` and `CF_HTML`, and writes both when rich reconstruction verifies. Plain-text fallback still cannot express bold/italic/underline, link hrefs, font family/size/color, list bullets and numbering, headings, blockquotes, or tables (which arrive tab-separated).

The visible case is **paragraph spacing**. Measured against Chrome on 2026-07-20 by driving it over CDP and dumping both clipboard flavors:

| Markup | `text/plain` | Gap survives |
|---|---|---|
| `<p style="margin:0 0 1em">A</p><p …>B</p>` | `A\r\n\r\nB` | yes |
| `<div>A</div><div><br></div><div>B</div>` | `A\r\n\r\nB` | yes |
| `<div>A</div><div>B</div>` | `A\r\nB` | n/a |
| `<div style="margin-bottom:1em">A</div><div …>B</div>` | `A\r\nB` | **no** |

A block boundary emits one newline, `</p>` emits two, an empty block contributes its own, and CSS margin emits nothing. So a paragraph gap produced by margin on a `<div>` is gone before our app sees the text — the run logs the flattened version as `input_text` and looks entirely healthy.

**This is not app-specific and a `TargetFormatting` rule cannot fix it.** The information is destroyed by the browser before `AfterCopy` runs, and rules may not touch the clipboard. Do not accept a Gmail/Slack/Notion "spacing rule" as a fix; it would be guessing whether `A\nB` meant tight lines or paragraphs. The rich-text solution lives in `RichTextClipboard`: exact ProseMirror paragraph/list handling first, then diff alignment of corrected characters onto the source HTML's text nodes. Unchanged tags, links, and list markup survive. If a structural edit cannot be mapped, the app pastes corrected Unicode text instead. Rich formatting may degrade, but a completed correction never stops solely because rich formatting failed. The former `rich_text_guard` paste refusal was removed on 2026-09-28 after a real Dev run hit it.

Recent log evidence (2026-09-24 through 2026-09-27): mixed text/list selections at 2026-09-24 13:51 and 2026-09-27 21:57, inline spans at 2026-09-27 16:18, and an explicit ordered list at 2026-09-27 22:00 all reported `rich_text.reason=unsupported_fragment` while the old path still pasted Unicode text. The 2026-09-27 21:59 multi-paragraph selection used HTML, but the model removed spaces at two paragraph edges. The corrected text and HTML now retain those spaces together. Timestamps are local (`America/Indianapolis`).

At 2026-09-28 09:48, ChatGPT copied a selected heading (`test`) and horizontal rule in `CF_HTML` but omitted the heading from `CF_UNICODETEXT`. Two Dev runs used plain-text fallback and pasted a correction without that heading, deleting it from the editor. Text-node alignment now preserves HTML nodes absent from Unicode and inserts their text into the Unicode replacement, including when rich alignment declines. The horizontal rule survives when ChatGPT consumes the HTML flavor; plain text cannot represent it. The same runs exposed a URL-protection regex that consumed the whole Markdown link as a bare URL, letting the model add an extra closing bracket; Markdown links are now protected as one literal. These findings and fixes are from the 2026-09-28 09:48:26 and 09:48:38 Dev `spellcheck_detail` entries.

The 09:48:26 log replay now selects `aligned_html` and restores the omitted heading in Unicode. The 09:48:38 replay with its original malformed model output still uses Unicode fallback, but also restores the omitted heading; replaying it with the corrected link punctuation selects `aligned_html`. Live ChatGPT paste behavior still needs a Dev retest.

---

## Activity feed pagination order

On 2026-09-28, the shared logs contained 29 successful spell checks between 2026-09-27 2:27 PM and 9:59 PM (America/Indianapolis), even though the dashboard appeared to jump between those times. `NativeActivityLogReader` returned entries newest first, but `ActivityPage.AppendEntries` reversed both day groups and rows inside each subsequent 30-entry page. The runs were on disk; the history display was out of order. Keep both initial and appended pages in descending order, and run `--dashboard-smoke` against the real corpus to verify chronology across at least two pages. No log backfill is needed for this incident.

---

## Loading overlay UI-thread marshalling

`SetPhase` is called from the async spell-check pipeline (not a UI thread). `OverlayHost` owns a dedicated STA background thread with its own message loop; the form and its Win32 handle are created on that thread at startup and every `SetPhase` is queued onto it via `BeginInvoke`, returning immediately. If you move or defer form/handle creation off that thread, the marshalling breaks and the overlay crashes or silently stops updating.

Phase-to-visibility contract: `Copying` shows the form, `Done` hides it, and `Sending`/`Waiting`/`Pasting` only swap the label text. `Sending` ends when the request body has been written; the much longer response wait must display `Waiting for AI...`. `Done` is dispatched in `RunAsync` immediately after the hot path returns — deliberately **before** the original-clipboard restore, which can block for seconds on failed runs while the OS renders the original clipboard formats. Moving the hide after the restore (e.g., back into `FinalizeAsync`) re-introduces an overlay that sticks on screen after failures.

See commits 52b5e27 and 77239cc for the specific WPF-in-WinForms crash sequence the original marshalling fixed.

---

## WPF-in-WinForms: `System.Windows.Application` must exist

WinForms `Application.Run` does not create `Application.Current` for WPF. Without a `System.Windows.Application` instance:
- `DynamicResource` lookups walk the visual/logical tree and find nothing at the root, resolving to `DependencyProperty.UnsetValue`.
- This crashes at layout time: `'{DependencyProperty.UnsetValue}' is not a valid value for property 'Background'`.
- The `pack://application:,,,/` scheme is also only registered by the Application instance.

**Fix:** `new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }` instantiated before `Application.Run`, with `Styles.xaml` and `Components.xaml` merged into `app.Resources`.

If you see `wpf_resources_failed` in logs at startup, the dashboard will not work at all. If you see `dashboard_open_failed` or `ui_dispatcher_unhandled` with `DependencyProperty.UnsetValue`, check this anchor before chasing template or binding rewrites.

---

## Tray app startup crash — construct order in `SpellCheckAppContext..ctor`

v0.7.0 shipped a `NullReferenceException` that killed the app on **every** launch, on **both** channels. `_notifyIcon` was built with `ContextMenuStrip = BuildMenu()`, and `BuildMenu()` → `BuildVersionLine()` dereferences `_updateService.State` — but `_updateService` was assigned several lines later. The constructor threw before the tray icon or hotkey ever registered, so the app "just never started" and, because startup dies before the update check runs, an already-updated copy cannot self-heal via a later release (it needs a manual reinstall or DLL patch). The bug is channel-independent: Dev would have crashed identically on its first launch — it was simply never started before the Prod tag.

Rules:

- In the constructor, a field that `BuildMenu()`/`BuildVersionLine()` reads (today: `_updateService`) must be assigned **before** the `_notifyIcon` block. Event wiring and the initial `CheckAsync` stay after `BuildMenu()` because `OnUpdateStateChanged` touches `_versionItem`, which `BuildMenu()` creates. Don't reorder one without the other.
- Never send a change to a Prod tag without launching it once on **some** channel. `dotnet run -c Dev` exercises the same constructor.
- The `--startup-smoke` mode (`Program.RunStartupSmoke`) constructs the real `SpellCheckAppContext` and self-exits; any constructor exception → exit 1. It is a required, auto-run gate in `release.yml` between Publish and Pack — a startup-crashing build can no longer be packed. `--dashboard-smoke` does **not** cover this: it only builds the WPF `MainWindow`, never the tray context.

Run locally with `UniversalSpellCheck.exe --startup-smoke` (exit 0 = ok). It registers the channel hotkey, so close a running instance of that channel first or it fails on `RegisterHotKey`.

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

## Activity feed pagination must never recurse before layout

The May 2026 infinite-scroll implementation called `MaybeFillViewport()` directly from `LoadNextPage()`'s `finally` block. WPF had not performed a layout pass, so `ScrollableHeight` remained zero. The method recursively loaded every remaining page on the dispatcher. With 1,587 history entries, startup stopped painting and processing hotkeys for more than a minute; Windows eventually classified the process as `AppHangB1` and closed it. The `dashboard_open step=done` log was misleading because the hang began later in the page's `Loaded` event.

Permanent rules:

- Log reads and all-time statistics scans run through `Task.Run`; do not put filesystem work in a WPF `Loaded`, scroll, or click handler.
- Initial rendering is exactly one bounded page (currently 30 entries).
- Viewport-fill checks run at `DispatcherPriority.ContextIdle`, after measurement, and may schedule only one page per dispatcher turn.
- Never call a page-loading method from its own cleanup path. `_isLoadingMore` prevents overlapping reads; a generation token prevents stale refresh results from mutating current UI state.
- Do not eagerly create hidden alternate views. The side-by-side diff is constructed only when selected.
- Any dynamic-programming diff must cap `n * m`; large inputs use a linear fallback.
- Treat a responsive tray and hotkey as part of dashboard acceptance testing. They share the startup UI thread.

Verification after any activity-feed or startup change:

1. Build Release.
2. Run `UniversalSpellCheck.exe --dashboard-smoke` with the real `%LocalAppData%\UniversalSpellCheck.Data\logs` corpus.
3. Require exit code 0. The smoke test enforces a 10-second hard watchdog, a 5-second first-page deadline, at most 30 initial entries, no unsolicited second page after deferred layout, and a bounded large-text diff.
   The smoke dispatcher's frame sentinel runs at `ApplicationIdle`, below the production `ContextIdle` viewport callback. Raising the sentinel priority would starve the callback and make the deferred-pagination assertion a false positive.
4. Launch Dev normally and verify the window paints immediately, Ctrl+Alt+D is processed, scrolling loads older entries, refresh remains responsive, and no `activity_load_failed` event appears.

---

## Dashboard auto-open on startup

`SpellCheckAppContext` hooks `Application.Idle` to auto-open the dashboard once on startup. This surfaces WPF failures immediately rather than requiring the user to find the tray menu. Check `dashboard_auto_open_attempt` and subsequent `dashboard_open step=` entries in the log if the dashboard doesn't appear. Step values: `construct` → `show` → `activate` → `done`. A missing `done` with an error after `construct` means the `MainWindow` constructor or XAML initialization threw.
