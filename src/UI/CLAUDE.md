# src/UI/ — WPF Dashboard

## Grounding

WPF dashboard for the tray app. Opened via the `Open Dashboard` tray menu item. Two pages: Activity (renders successful `spellcheck_detail` log entries as diff rows) and Settings (API key, log folder, replacements). Visual quality matters here — this is the user-facing surface beyond the hotkey itself. The cream tone, card borders, and font stack are intentional and easy to drift on.

## Read first

> **Always read `DESIGN.md` (repo root) before any visual change.** It is the canonical visual contract: colors, fonts, spacing, mockups. Then check root `CLAUDE.md` for routing and `docs/architecture.md` for how this folder plugs into the tray app lifetime.

## What's here

```text
UI/
|-- Styles.xaml
|-- Components.xaml
|-- MainWindow.xaml
|-- MainWindow.xaml.cs
|-- Pages/
|   |-- ActivityPage.xaml
|   |-- ActivityPage.xaml.cs
|   |-- SettingsPage.xaml
|   `-- SettingsPage.xaml.cs
`-- Fonts/
    `-- README.md
```

## Runtime wiring

- The csproj keeps `<UseWindowsForms>true</UseWindowsForms>` for the tray icon, hotkey window, and loading overlay, **and** adds `<UseWPF>true</UseWPF>` for this dashboard.
- `SpellCheckAppContext.ShowSettings()` opens `MainWindow.xaml` in-process.
- `LoadingOverlayForm` (WinForms) stays separate — it's transient spell-check feedback, not dashboard UI.

## Current data sources

- `ActivityPage.xaml.cs` reads the current native log file and renders successful `spellcheck_detail` entries as diff rows.
- `SettingsPage.xaml.cs` saves the API key through `SettingsStore`, opens the native log folder, and opens `replacements.json`.

Deferred controls are intentionally **disabled** instead of fake-wired: hotkey capture, model switching, startup toggle, replacements editor. Do not stub these — leave them disabled until real wiring lands.

## Visual verification (after any UI change)

1. Compare against `docs/design-mockups/home.png` and `docs/design-mockups/settings.png`.
2. Confirm the cream tone has not drifted gray.
3. Check font loading. Cambria/Segoe UI fallback means the bundled font is missing — investigate before shipping.
4. Card borders should be subtle but visible.

---

## Keeping this file current

When pages, runtime wiring, or deferred-control state changes, update this file in the same edit. **If you notice this file or `DESIGN.md` no longer matches what the dashboard actually does, stop and flag it to the user with a proposed fix** — visual drift compounds quickly.
