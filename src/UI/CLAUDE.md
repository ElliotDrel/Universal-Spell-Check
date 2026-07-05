# src/UI/ — WPF Dashboard

## Grounding

WPF dashboard for the tray app. Opened via the `Open Dashboard` tray menu item. Two pages: **Home** (`ActivityPage` — paginated activity feed with inline diffs) and **Settings** (named API keys, model, updates, log folder, replacements). Visual quality matters here — this is the user-facing surface beyond the hotkey itself. The cream tone, card borders, and font stack are intentional and easy to drift on.

## Read first

> **Always read `DESIGN.md` (repo root) before any visual change.** It is the canonical visual contract: colors, fonts, spacing, layout. Then check root `CLAUDE.md` for routing and `docs/architecture.md` § WPF dashboard for pagination and log wiring.

## What's here

```text
UI/
|-- Styles.xaml
|-- Components.xaml          # IconButton, HoverTextButton, cards, nav, buttons
|-- SmoothScrollViewer.cs    # Trackpad smooth scroll
|-- InlineTextDiff.cs        # Line + character diff
|-- FeedActionIcons.cs       # Copy / check / more-vertical icons
|-- MainWindow.xaml
|-- MainWindow.xaml.cs
|-- Pages/
|   |-- ActivityPage.xaml    # Activity header, stats bar, FeedItems + loading indicator
|   |-- ActivityPage.xaml.cs # NativeActivityLogReader, row builders
|   |-- SettingsPage.xaml
|   `-- SettingsPage.xaml.cs
`-- Fonts/                   # Bundled Instrument + JetBrains Mono (embedded in csproj)
```

## Runtime wiring

- The csproj keeps `<UseWindowsForms>true</UseWindowsForms>` for the tray icon, hotkey window, and loading overlay, **and** adds `<UseWPF>true</UseWPF>` for this dashboard.
- `SpellCheckAppContext.ShowSettings()` opens `MainWindow.xaml` in-process.
- `LoadingOverlayForm` (WinForms) stays separate — it's transient spell-check feedback, not dashboard UI.

## Activity feed (data + UX)

- **Logs:** `%LocalAppData%\UniversalSpellCheck.Data\logs\spellcheck-*.jsonl` (unified across channels). Filter via per-line `channel` when needed for tooling; the feed shows all successful runs.
- **Reader:** `NativeActivityLogReader.ReadEntries(30, cursor)` + `ReadAllTimeStats()` in `ActivityPage.xaml.cs`.
- **Threading:** file reads and all-time stats run off-dispatcher. The UI renders one 30-entry page, yields through a completed layout pass, then loads another page only if the measured viewport is still empty or the user scrolls.
- **Diff cost:** inline diff renders first; side-by-side diff is lazy. LCS work is bounded so a large historical entry cannot freeze the dashboard.
- **UI:** Flat rows (time + model | diff | hover actions), expandable per-row timing breakdowns, day headers (`TODAY` / `YESTERDAY` / date), all-time stats bar, bottom spinner while paginating.
- **Refresh:** `FeedItems.Children.Clear()` — does not remove `LoadingIndicator` or `EmptyState` hosts.

## Settings (data)

- `SettingsPage.xaml.cs` manages named API keys through `SettingsStore`, switches the active key for the next request, opens the native log folder, and opens `replacements.json`. Only masked key identifiers belong in UI; never log or display full keys.

Model selection is persisted through `SettingsStore` and applies to the next request. GPT-4.1 is the default; GPT-5.4 mini is optional. Hotkey capture remains intentionally disabled.

## Visual verification (after any UI change)

1. **Layout authority:** `DESIGN.md` § Home (Activity) — flat feed + top stats bar (not the two-column `home.png` layout).
2. **Tone reference:** `docs/design-mockups/home.png` and `settings.png` for cream, borders, typography.
3. Confirm the cream tone has not drifted gray.
4. Check font loading. Cambria/Segoe UI fallback on headers means the bundled font is missing.
5. Card borders on Settings should be subtle but visible.

## Manual checks (Activity)

1. Open **Home** — stats show all-time numbers; feed shows newest entries first.
2. Scroll to bottom — spinner appears; older entries append; day headers dedupe across pages.
3. Click **↻ Refresh** — feed and stats reload; scroll-to-load still works afterward.
4. Confirm each row shows the exact model ID used for that spell check beneath its timestamp.
5. Hover a row — ghost background and copy/timing/⋮ icons visible (⋮ appears only for changed text; timing appears only when telemetry exists).
6. Click diff body or copy — corrected text on clipboard; checkmark feedback on copy icon.
7. Click the timing clock — the pipeline breakdown expands beneath the row; click again to collapse it.
8. On a `text_changed` row, ⋮ → toggle inline vs side-by-side diff.
9. Trackpad scroll feels smooth; mouse wheel scrolls normally.
10. Run the Release `--dashboard-smoke` mode against the real log corpus; it must exit 0 without rendering more than the first page or tripping the dispatcher watchdog.

---

## Keeping this file current

When pages, runtime wiring, or deferred-control state changes, update this file in the same edit. **If you notice this file or `DESIGN.md` no longer matches what the dashboard actually does, stop and flag it to the user with a proposed fix** — visual drift compounds quickly.
