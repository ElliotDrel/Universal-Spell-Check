# Dashboard UI (WPF)

This folder contains the WPF dashboard window described in `DESIGN.md` at the repo root. It is wired into the running tray app through the `Open Dashboard` tray menu item.

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

The project keeps `<UseWindowsForms>true</UseWindowsForms>` for the tray icon, hotkey window, and loading overlay, and adds `<UseWPF>true</UseWPF>` for this dashboard.

`SpellCheckAppContext.ShowSettings()` opens `UI/MainWindow.xaml` in-process. The WinForms `LoadingOverlayForm` stays separate because it is transient spell-check feedback, not dashboard UI.

## Current data

`ActivityPage.xaml.cs` reads the current native log file and renders successful `spellcheck_detail` entries as diff rows. `SettingsPage.xaml.cs` saves the API key through `SettingsStore`, opens the native log folder, and opens `replacements.json`.

Deferred controls are intentionally disabled instead of fake-wired: hotkey capture, model switching, startup toggle, and the replacements editor.

## Visual verification

Once running:

1. Compare against `docs/design-mockups/home.png` and `docs/design-mockups/settings.png`.
2. Check that the cream tone has not drifted gray.
3. Check font loading. Cambria/Segoe UI fallback means the bundle is still missing.
4. Check that card borders are subtle but visible.
