# Design System — Universal Spell Check (Native Dashboard)

> **Scope:** This spec covers the WPF dashboard window for the native C#/.NET tray app at `native/UniversalSpellCheck/`. The AHK production script (Universal Spell Checker.ahk) is unaffected. The dashboard opens from the tray icon and provides activity log, settings, and (later) deeper analytics.

## Product Context

- **What this is:** A Windows desktop dashboard for an AI-powered spell-check tray utility. Opens from the system tray; lets the user see what the spell-checker has corrected, customize the hotkey, and manage settings.
- **Who it's for:** Power users who keep the tray app running all day. Primarily the developer themselves (solo tool).
- **Project type:** Windows-native dashboard window inside an existing tray app.
- **Memorable thing:** "This is serious software." Editorial typography signals craft. The dashboard should feel like a tool a power user respects — not a SaaS template.

## Approved Mockups

| Screen | Status | File |
|---|---|---|
| Home (Activity feed) | Approved | `docs/design-mockups/home.png` |
| Settings | Approved | `docs/design-mockups/settings.png` |
| Insights (deferred to v2) | Reference only | `docs/design-mockups/insights-deferred.png` |

Generated via gstack `/design-consultation`. Originals at `~/.gstack/projects/ElliotDrel-Universal-Spell-Check/designs/design-system-20260427/`.

## Aesthetic Direction

- **Direction:** Editorial Light. Warm cream canvas, mixed serif + sans typography, generous whitespace, soft rounded cards.
- **Decoration level:** Minimal. Typography and whitespace do the work. No gradients, no icon circles, no decorative blobs.
- **Mood:** Refined, crafted, calm. Reference: Wispr Flow desktop app — cream tones, editorial type, hairline borders.

## Color Tokens

| Token | Hex | Role |
|---|---|---|
| `Canvas` | `#F5F1EA` | Page background (warm cream — not gray, not white) |
| `Surface` | `#FFFFFF` | Card backgrounds |
| `Border` | `#E8E2D6` | Hairline 1px borders on cards and dividers |
| `TextPrimary` | `#1A1A1A` | Primary text |
| `TextMuted` | `#7B7268` | Muted text (warm gray, not cool — keeps the warmth) |
| `ActionPrimary` | `#1A1A1A` | Primary button background (white text) |
| `Accent` | `#C4623E` | Terracotta — used **only** for streak / positive indicator |
| `DiffMinusBg` | `#FBE9E5` | Activity feed: removed-line background |
| `DiffMinusText` | `#B8453B` | Activity feed: removed-line text |
| `DiffPlusBg` | `#E8F0E0` | Activity feed: added-line background |
| `DiffPlusText` | `#3F6B26` | Activity feed: added-line text |
| `StatusSuccess` | `#5A8C3F` | Success state |
| `StatusError` | `#B8453B` | Error state |
| `StatusWarning` | `#C4823E` | Warning state |

**Dark mode:** not in v1 scope.

## Typography

| Role | Family | Weight | Size | Used for |
|---|---|---|---|---|
| Display | Instrument Serif | Regular | 32px | Page headers ("Settings", "Today") |
| Display Stat | Instrument Serif | Regular | 28px | Big stat numbers ("1,247", "412ms") |
| Section | Instrument Serif | Regular | 18px | Subsection headers ("Hotkey", "Model") |
| Body L | Instrument Sans | Regular | 15px | Form labels, primary body |
| Body | Instrument Sans | Regular | 13px | Default UI text |
| Body Muted | Instrument Sans | Regular | 13px | Descriptions, captions (color: TextMuted) |
| Caption | Instrument Sans | Regular | 11px | Stat labels under numbers |
| Mono | JetBrains Mono | Regular | 12px | Activity feed timestamps, hotkey chips, file paths, diff content |

**Font sourcing:** Instrument Serif, Instrument Sans, JetBrains Mono are all OFL-licensed via Google Fonts. Bundle as embedded resources in the WPF project (see `native/UniversalSpellCheck/UI/Fonts/README.md`). Do **not** rely on Google Fonts CDN — desktop apps should be offline-capable.

## Spacing

- **Base unit:** 4px
- **Density:** Comfortable (not tight, not airy)
- **Scale:** `xs(4)  sm(8)  md(16)  lg(24)  xl(32)  2xl(48)  3xl(64)`
- **Card padding:** 24px
- **Page padding:** 32px (top/sides), 32px between major sections
- **Sidebar item padding:** 8px vertical, 12px horizontal
- **Diff row vertical gap:** 16px between diffs in the activity feed

## Layout

- **Window size:** Default 1100x720, resizable, min 900x600
- **Sidebar:** 240px fixed width, canvas-colored (no separate background — separation is whitespace + nav item state)
- **Main content:** flex-fill, 32px padding
- **Card border-radius:** 14px (consistent across all cards)
- **Card border:** 1px solid `Border`, no shadow

### Sidebar contents

```
[Brand: "UNIVERSAL SPELL CHECK"]   ← uppercase Instrument Sans, tracked, top
[Home]                              ← icon + label, 36px tall, active state = solid pill
[Settings]
                                    ← flex spacer
[Help]                              ← bottom group
[Version 1.0]                       ← muted caption
```

**Nav scope for v1:** `Home` and `Settings` only. `Insights` mockup exists as forward reference; defer until activity feed proves out.

## Components

### Card
White rounded surface (14px radius), 1px `Border`, 24px padding. The base component for all content groupings.

### Primary button
- Background: `ActionPrimary` (#1A1A1A)
- Text: white, Instrument Sans 13px medium
- Padding: 8px vertical, 16px horizontal
- Border-radius: 8px
- Hover: brightness 110% (very subtle)

### Ghost button
- Background: transparent
- Border: 1px `Border`
- Text: `TextPrimary` Instrument Sans 13px
- Hover: background `#EFEAE0`

### Keyboard chip (for hotkey display)
Used to render `[Ctrl] + [Alt] + [U]`:
- Background: `#FFFFFF`
- Border: 1px `Border`
- Border-radius: 6px
- Padding: 2px 8px
- Font: JetBrains Mono 11px
- Text color: `TextPrimary`
- Plus signs between chips: `TextMuted`

### Diff row (activity feed)
Each spell-check correction is rendered as two stacked lines:
```
- their going to the store          ← red bg, red text, leading minus
+ they're going to the store        ← green bg, green text, leading plus
```
- Each line: 6px vertical padding, 12px horizontal padding, 4px border-radius
- Lines stacked tight (no gap between - and + lines of the same diff)
- Above each diff (or to the right): timestamp in Mono `TextMuted`, model label in `TextMuted`
- Between diffs: 16px gap

### Stat card (Home, top-right)
Small grid (2 columns x 2-3 rows). Each cell:
- Big number in Instrument Serif (24-28px)
- Label in Caption (11px, `TextMuted`)
- Streak number in `Accent` (terracotta) when applicable

### Settings section
- Card with section header (Instrument Serif 18px) at top
- Below header: 1+ rows
- Each row: label (Body L) + description (Body Muted) on left, control on right
- Multiple rows in same section divided by 1px `Border` hairline

### Toggle switch
Standard pill toggle. Track 32x18px, knob 14px circle. Off: track `#E8E2D6`. On: track `#1A1A1A`. Knob: white.

## Three Screens

### Home (Activity)
**Layout:** Page header "Today" (Instrument Serif 32px) → two-column main:
- **Left (flex):** Activity feed card. Stack of diff rows. Each row shows old text in red diff style and new text in green diff style, with timestamp + model label nearby.
- **Right (280px):** Stats card. Today's checks count, corrections count, accuracy, streak (terracotta).

**Empty state:** When no checks today, show centered Instrument Sans muted text: "No spell checks yet today. Press your hotkey to get started." Never show empty stats — always render zero state with `0 / 0 / —`.

### Settings
**Layout:** Page header "Settings" (Instrument Serif 32px) → vertical stack of section cards:

1. **Hotkey** — current hotkey shown as keyboard chips. Runtime hotkey changes are deferred.
2. **Model** — display current model (`gpt-4.1`). Runtime model selection is deferred.
3. **Replacements** — "Edit list" button → opens replacements editor; shows entry count
4. **Startup** — "Start on login" toggle
5. **API key** — masked password input with "Save" button; helper text "Stored encrypted for this Windows user with DPAPI"
6. **Logs** — log path in Mono + "Open folder" ghost button

### Insights (deferred to v2)
Mockup at `docs/design-mockups/insights-deferred.png`. Shows: 4 stat cards top row, 30-day usage bar chart, model performance table, top corrections list. Build only after the activity feed proves valuable enough to warrant deeper analytics.

## SAFE choices (category baseline)
- Sidebar nav with icon + label
- Timestamp + content activity feed pattern
- Settings as a full page within the dashboard (not a separate window)

## RISKS (deliberate departures)
- **Instrument Serif headers** — Windows utilities almost always use Segoe UI Variable. Going serif costs implementation friction (font bundling) and feels less "Microsoft-native." Gain: memorable, doesn't look generic.
- **Cream over Mica gray** — Windows defaults to flat white or system Mica. Cream is warmer. Cost: stands apart from the OS chrome. Gain: instantly identifiable as this app.
- **JetBrains Mono for timestamps only** — body font everywhere else. Cost: extra bundled font. Gain: log feels like a real log file.

## Implementation Notes (WPF)

### csproj setup
Add to `native/UniversalSpellCheck/UniversalSpellCheck.csproj` PropertyGroup:
```xml
<UseWPF>true</UseWPF>
```
Keep `<UseWindowsForms>true</UseWindowsForms>` — the tray icon (`NotifyIcon`) and global hotkey (`HotkeyWindow`) stay WinForms; the dashboard window is WPF. Mixed mode is supported in .NET on Windows.

### File layout
```
native/UniversalSpellCheck/UI/
├── Styles.xaml            # Design tokens (colors, brushes, fonts, type styles)
├── Components.xaml        # Card, Primary button, Ghost button, Keyboard chip styles
├── Fonts/
│   ├── README.md          # How to bundle Instrument Serif/Sans + JetBrains Mono
│   ├── InstrumentSerif-Regular.ttf   # (download from fonts.google.com)
│   ├── InstrumentSans-Regular.ttf
│   └── JetBrainsMono-Regular.ttf
├── MainWindow.xaml        # Shell: sidebar + content host
├── MainWindow.xaml.cs
└── Pages/
    ├── ActivityPage.xaml
    ├── ActivityPage.xaml.cs
    ├── SettingsPage.xaml
    └── SettingsPage.xaml.cs
```

### Wiring into existing tray app
`SpellCheckAppContext.cs` opens the WPF `MainWindow` from the tray's `Open Dashboard` menu item. The WPF window's lifecycle is owned by the tray context; quit disposes it with the tray app.

The WinForms `LoadingOverlayForm` (bottom-of-screen progress overlay) stays as-is — it's transient and the WinForms implementation is already correct.

### Data binding
Activity page reads from `DiagnosticsLogger`'s current log file (`%LOCALAPPDATA%\UniversalSpellCheck\native-spike-logs\phase5-YYYY-MM-DD.log`) and renders successful `spellcheck_detail` entries as diff rows. A richer observable collection or file watcher is deferred; speed is the product, but the dashboard is not the hot path.

Settings currently saves the API key through `SettingsStore`, opens the log folder, and opens `replacements.json`. Hotkey capture, model switching, startup toggling, and a replacements editor are deferred so v1 does not show fake controls.

### Testing the design
After scaffolding, verify the design in this order:
1. Open the dashboard window — does it match `docs/design-mockups/home.png`?
2. Color palette correct? Compare cream tone — easy to drift toward gray.
3. Typography rendering — Instrument Serif loaded? If you see Cambria/Georgia, fonts didn't bundle.
4. Card border visible but subtle? If invisible, increase border alpha. If heavy, reduce.
5. Diff rows readable? Red and green should not be saturated — keep them muted.

## Decisions Log

| Date | Decision | Rationale |
|---|---|---|
| 2026-04-27 | Initial design system created via /design-consultation | Mockups generated and approved by user. Reference: Wispr Flow visual language. |
| 2026-04-27 | Light editorial over dark | User initially said "dark like Wispr Flow" but Wispr Flow screenshots showed light/cream — went with the visual reference over the description. |
| 2026-04-27 | WPF over WinForms for dashboard | WinForms cannot render this aesthetic without heavy custom drawing. WPF supports it natively. Tray + hotkey stay WinForms. |
| 2026-04-27 | Activity feed uses GitHub-style diffs | User-requested. Each spell-check correction shows as red minus / green plus rows, not before-arrow-after. Reads as a log of changes. |
| 2026-04-27 | Nav scope: Home + Settings only for v1 | Insights deferred — build the activity feed first, fold deeper analytics in later if needed. |
