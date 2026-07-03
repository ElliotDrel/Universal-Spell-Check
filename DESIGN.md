# Design System — Universal Spell Check (Native Dashboard)

> **Scope:** This spec covers the WPF dashboard window for the native C#/.NET tray app under `src/`. The AHK production script (Universal Spell Checker.ahk) is archived and unaffected. The dashboard opens from the tray icon and provides an activity feed, settings, and (later) deeper analytics.

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
| Display | Instrument Serif | Regular | 32px | Page headers ("Activity", "Settings") |
| Display Stat | Instrument Serif | Regular | 28px | Big stat numbers ("1,247", "412ms") |
| Section | Instrument Serif | Regular | 18px | Subsection headers ("Hotkey", "Model") |
| Body L | Instrument Sans | Regular | 15px | Form labels, primary body |
| Body | Instrument Sans | Regular | 13px | Default UI text |
| Body Muted | Instrument Sans | Regular | 13px | Descriptions, captions (color: TextMuted) |
| Caption | Instrument Sans | Regular | 11px | Stat labels under numbers |
| Mono | JetBrains Mono | Regular | 12px | Activity feed timestamps, hotkey chips, file paths, diff content |

**Font sourcing:** Instrument Serif, Instrument Sans, JetBrains Mono are all OFL-licensed via Google Fonts. Bundle as embedded resources under `src/UI/Fonts/`. Do **not** rely on Google Fonts CDN — desktop apps should be offline-capable.

## Spacing

- **Base unit:** 4px
- **Density:** Comfortable (not tight, not airy)
- **Scale:** `xs(4)  sm(8)  md(16)  lg(24)  xl(32)  2xl(48)  3xl(64)`
- **Card padding:** 24px
- **Page padding:** 32px (top/sides), 32px between major sections
- **Sidebar item padding:** 8px vertical, 12px horizontal
- **Feed row vertical rhythm:** 4px padding inside row chrome; 2px margin between rows; hairline divider under each row
- **Day section gap:** 28px above non-first day headers; 4px above first day header in feed

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

### Feed row (activity feed)
Flat list on the canvas (no card wrapper). One row per successful `spellcheck_detail` entry:

```
[12:34 PM]  [inline diff body — click to copy]     [copy] [⋮]
─────────────────────────────────────────────────────────────
```

- **Timestamp column:** JetBrains Mono 12px (`MonoSmall`), max width 58px, top-aligned
- **Diff body:** character-level inline diff on changed lines (strikethrough + `DiffMinus*` for deletions, green insert for additions); unchanged lines render plain. Rows with `text_changed: false` show output text only.
- **Actions:** outline copy + vertical-dots icons (`FeedActionIcons`); **opacity 0 until row hover**; row background `HoverGhost` on hover
- **Copy:** click diff body or copy icon → clipboard gets corrected text; copy icon shows checkmark for 1.5s on success
- **⋮ menu** (only when `text_changed`): toggle **Inline diff** vs **Side by side** (per-line char highlights in split columns)
- Hairline `Border` divider under each row

Legacy GitHub-style stacked `-` / `+` lines are superseded by inline diff; keep minus/plus *colors* for delete/insert segments.

### Day section header
Uppercase Caption label: `TODAY`, `YESTERDAY`, or formatted date (`MMMM d` / `MMMM d, yyyy`). Hairline divider below label. Inserted when the feed crosses a calendar day (including across paginated loads).

### Stats bar (Activity, all-time)
Full-width horizontal bar below the page header (not a sidebar card). Four equal columns separated by 1px `Border` hairlines:

| CHECKS | CORRECTIONS | ACCURACY | DAY STREAK |
|--------|-------------|----------|------------|

- Big numbers: `DisplayStat` (Instrument Serif 28px)
- Labels: Caption (11px, `TextMuted`)
- **All-time** values from a lightweight scan of every `spellcheck-*.jsonl` file (not “today only”)
- Streak uses `Accent` (terracotta)
- Accuracy shows `—` when checks = 0

### Loading indicator (pagination)
Centered at the bottom of the feed while older pages load: rotating dashed ring (18px) + “Loading earlier…” in `BodyMuted`. Hidden when idle.

### Settings section
- Card with section header (Instrument Serif 18px) at top
- Below header: 1+ rows
- Each row: label (Body L) + description (Body Muted) on left, control on right
- Multiple rows in same section divided by 1px `Border` hairline

### Toggle switch
Standard pill toggle. Track 32x18px, knob 14px circle. Off: track `#E8E2D6`. On: track `#1A1A1A`. Knob: white.

## Three Screens

### Home (Activity)
Sidebar nav label remains **Home**; page title is **Activity** (Instrument Serif 32px). Ghost **↻ Refresh** button top-right.

**Layout (top to bottom):**
1. Page header row (`Activity` + Refresh)
2. All-time stats bar (four columns — see Stats bar above)
3. Scrollable feed (`SmoothScrollViewer`, scrollbar hidden): day-grouped rows, infinite scroll

**Feed behavior:**
- Reads the shared log corpus: `%LocalAppData%\UniversalSpellCheck\logs\spellcheck-*.jsonl`
- Newest successful `spellcheck_detail` entries first; **30 entries per page**; loads older pages when scrolled near the bottom (~120px) or when content does not fill the viewport
- Trackpad: smooth per-frame scroll (`SmoothScrollViewer`); mouse wheel: native WPF scroll

**Empty state:** Centered `BodyMuted`: “No spell checks yet. Press your hotkey to get started.” Stats still show zeros / em dash.

**Mockup note:** `docs/design-mockups/home.png` is the original two-column reference (tone, cream, typography). The shipped layout is a flat Wispr Flow–style feed with a top stats bar — treat this spec as the layout authority.

### Settings
**Layout:** Page header "Settings" (Instrument Serif 32px) → vertical stack of section cards:

1. **Hotkey** — current hotkey shown as keyboard chips. Runtime hotkey changes are deferred.
2. **Model** — choose `gpt-4.1-mini` or `gpt-5.4-mini`; changes apply to the next request.
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
`src/UniversalSpellCheck.csproj` enables both `<UseWindowsForms>true</UseWindowsForms>` (tray, hotkey, loading overlay) and `<UseWPF>true</UseWPF>` (dashboard). Mixed mode is supported in .NET on Windows.

### File layout
```
src/UI/
├── Styles.xaml              # Design tokens (colors, brushes, fonts, type styles)
├── Components.xaml          # Card, buttons, IconButton, HoverTextButton, nav styles
├── SmoothScrollViewer.cs    # Trackpad smooth scroll; hidden vertical scrollbar
├── InlineTextDiff.cs        # Line + character diff for feed rendering
├── FeedActionIcons.cs       # Outline copy / check / more-vertical vectors
├── MainWindow.xaml          # Shell: sidebar + Frame content host + update banner
├── MainWindow.xaml.cs
└── Pages/
    ├── ActivityPage.xaml    # Stats bar + paginated feed
    ├── ActivityPage.xaml.cs # NativeActivityLogReader + row builders
    ├── SettingsPage.xaml
    └── SettingsPage.xaml.cs
```

Bundled fonts live under `src/UI/Fonts/` (Instrument Serif/Sans, JetBrains Mono). See `src/UI/CLAUDE.md` for font verification steps.

### Wiring into existing tray app
`SpellCheckAppContext.cs` opens the WPF `MainWindow` from the tray's `Open Dashboard` menu item. The WPF window's lifecycle is owned by the tray context; quit disposes it with the tray app.

The WinForms `LoadingOverlayForm` (bottom-of-screen progress overlay) stays as-is — it's transient and the WinForms implementation is already correct.

### Data binding (Activity)
`ActivityPage` uses `NativeActivityLogReader` in `ActivityPage.xaml.cs` (not MVVM binding):

- **Feed:** `ReadEntries(pageSize, cursor)` walks daily `spellcheck-*.jsonl` files newest-first (newest line in each file first). Cursor tracks file index + line index. Page size = 30.
- **Stats:** `ReadAllTimeStats()` scans all log files on a worker thread for successful runs (`status=success`); accuracy = corrections / checks; day streak from calendar days with at least one success.
- **Responsiveness:** log I/O never runs on the WPF dispatcher. Initial rendering is one page; viewport fill is measured after layout and loads at most one page per dispatcher turn. Hidden side-by-side diffs are created lazily, and diff matrix size is capped for unusually large text.
- **Refresh:** clears `FeedItems` only (preserves empty state + loading indicator hosts); reloads stats + first page.
- No file watcher — user refreshes or scrolls to load more. Dashboard is not the hot path.

Settings saves the API key and model through `SettingsStore`, opens the log folder, and opens `replacements.json`. Hotkey capture remains deferred.

### Testing the design
After visual changes, verify in this order:
1. Open **Home** — flat feed, top stats bar, **Activity** header (not two-column mockup layout).
2. Cream `Canvas` — not gray or pure white.
3. Instrument Serif/Sans loaded (no Cambria/Segoe fallback for headers).
4. Inline diff readable — muted red/green, not saturated.
5. Hover: row ghost background + copy/⋮ icons appear.
6. Scroll to bottom — spinner + older entries; **Refresh** — feed reloads and pagination still works.
7. Compare tone to `docs/design-mockups/home.png` and `settings.png` where layout still applies.

## Decisions Log

| Date | Decision | Rationale |
|---|---|---|
| 2026-04-27 | Initial design system created via /design-consultation | Mockups generated and approved by user. Reference: Wispr Flow visual language. |
| 2026-04-27 | Light editorial over dark | User initially said "dark like Wispr Flow" but Wispr Flow screenshots showed light/cream — went with the visual reference over the description. |
| 2026-04-27 | WPF over WinForms for dashboard | WinForms cannot render this aesthetic without heavy custom drawing. WPF supports it natively. Tray + hotkey stay WinForms. |
| 2026-04-27 | Activity feed uses GitHub-style diffs | User-requested. Each spell-check correction shows as red minus / green plus rows, not before-arrow-after. Reads as a log of changes. |
| 2026-04-27 | Nav scope: Home + Settings only for v1 | Insights deferred — build the activity feed first, fold deeper analytics in later if needed. |
| 2026-05-27 | Flat Activity feed + all-time stats bar | Wispr Flow–style layout: no feed card, inline char diff, hover actions, paginated infinite scroll (30/page), `SmoothScrollViewer` for trackpad. Mockup `home.png` remains tone reference only. |
| 2026-05-27 | Inline diff replaces stacked +/- rows | Character-level highlights within lines; side-by-side view optional per row via ⋮ menu. |
