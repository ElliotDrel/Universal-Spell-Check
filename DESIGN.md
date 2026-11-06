# Design System — Universal Spell Check (Native Dashboard)

> **Scope:** This spec covers the native dashboard window of the Rust tray app (`rust/src/dashboard.rs`, native Win32 controls via `native-windows-gui`). It opens from the tray icon and provides an activity feed, settings, and (later) deeper analytics.

## Product Context

- **What this is:** A Windows desktop dashboard for an AI-powered spell-check tray utility. Opens from the system tray; lets the user see what the spell-checker has corrected, customize the hotkey, and manage settings.
- **Who it's for:** Power users who keep the tray app running all day. Primarily the developer themselves (solo tool).
- **Project type:** Windows-native dashboard window inside an existing tray app.
- **Memorable thing:** "This is serious software." Editorial typography signals craft. The dashboard should feel like a tool a power user respects — not a SaaS template.

## Mockups

The original April 2026 mockups (Home, Settings, deferred Insights) were removed from the tree on 2026-11-06 and remain in git history. This spec is the layout authority.

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

**Font sourcing:** Instrument Serif, Instrument Sans, JetBrains Mono are all OFL-licensed via Google Fonts. They are bundled in `rust/assets/` and loaded from embedded memory. Do **not** download fonts at runtime — the app must work offline.

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
[12:34 PM]  [inline diff body — click to copy]     [copy] [clock] [⋮]
[gpt-4.1]
─────────────────────────────────────────────────────────────
```

- **Metadata column:** timestamp in JetBrains Mono 12px (`MonoSmall`) with the exact model ID beneath it in Caption text; max width 96px, top-aligned
- **Diff body:** character-level inline diff on changed lines (strikethrough + `DiffMinus*` for deletions, green insert for additions); unchanged lines render plain. Rows with `text_changed: false` show output text only.
- **Actions:** outline copy, timing clock, and vertical-dots icons (`FeedActionIcons`); **opacity 0 until row hover**; row background `HoverGhost` on hover
- **Original text:** changed rows expose **See original** in the existing `⋮` diff-options menu, alongside inline and side-by-side views. It replaces the displayed diff in place with the uncorrected source and an `ORIGINAL TEXT - NOT SPELL-CHECKED` marker. The row's copy icon, copy tooltip, and click-to-copy body all target that original text until **See corrected** restores the normal view.
- **Copy:** click diff body or copy icon → clipboard gets the text currently shown; copy icon shows checkmark for 1.5s on success
- **Timing clock:** shown when the log entry contains timing telemetry; toggles a compact breakdown panel beneath the diff with clipboard, AI request phases, post-processing, paste, and total duration
- **⋮ menu** (only when `text_changed`): choose **Inline diff**, **Side by side** (per-line char highlights in split columns), or **See original**
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
3. Scrollable feed (scrollbar hidden): day-grouped rows, infinite scroll

**Feed behavior:**
- Reads the shared log corpus: `%LocalAppData%\UniversalSpellCheck.Data\logs\spellcheck-*.jsonl`
- Newest successful `spellcheck_detail` entries first; **30 entries per page**; loads older pages when scrolled near the bottom (~120px) or when content does not fill the viewport
- Wheel input from child controls is forwarded to the feed so scrolling works anywhere over it

**Empty state:** Centered `BodyMuted`: “No spell checks yet. Press your hotkey to get started.” Stats still show zeros / em dash.

### Settings
**Layout:** Page header "Settings" (Instrument Serif 32px) → vertical stack of section cards:

1. **Hotkey** — current hotkey shown as keyboard chips. Runtime hotkey changes are deferred.
2. **Model** — choose `gpt-4.1` (default) or `gpt-5.4-mini`; changes apply to the next request.
3. **Replacements** — "Edit list" button → opens replacements editor; shows entry count
4. **Startup** — "Start on login" toggle
5. **API key** — masked password input with "Save" button; helper text "Stored encrypted for this Windows user with DPAPI"
6. **Logs** — developer logging toggle (off by default), short local capture description, log path in Mono + "Open folder" ghost button

### Insights (deferred to v2)
The mockup (in git history) shows: 4 stat cards top row, 30-day usage bar chart, model performance table, top corrections list. Build only after the activity feed proves valuable enough to warrant deeper analytics.

## SAFE choices (category baseline)
- Sidebar nav with icon + label
- Timestamp + content activity feed pattern
- Settings as a full page within the dashboard (not a separate window)

## RISKS (deliberate departures)
- **Instrument Serif headers** — Windows utilities almost always use Segoe UI Variable. Going serif costs implementation friction (font bundling) and feels less "Microsoft-native." Gain: memorable, doesn't look generic.
- **Cream over Mica gray** — Windows defaults to flat white or system Mica. Cream is warmer. Cost: stands apart from the OS chrome. Gain: instantly identifiable as this app.
- **JetBrains Mono for timestamps only** — body font everywhere else. Cost: extra bundled font. Gain: log feels like a real log file.

## Implementation notes

- The dashboard runs on its own native UI thread so rendering and log reads never delay the hotkey (`docs/architecture.md`).
- Feed: newest successful `spellcheck_detail` entries first, 30 rows before background statistics, more on scroll. Text previews are bounded; copying a row copies the full output.
- Diffs: inline character diff by default. Side-by-side uses separate native rich-edit controls, created only when selected and reset before reuse. Append styled text with `EM_REPLACESEL`, never NWG's append, which replaces all text and erases earlier styles.
- Minimum window size is 900×600 logical pixels, scaled to the active DPI.

### Testing the design
After visual changes, verify in a Dev build (`cargo build --release`, then launch with `--dashboard`):
1. **Home** shows the flat feed with the top stats bar and the **Activity** header.
2. The canvas is cream, not gray or pure white.
3. Instrument Serif/Sans are loaded (no Cambria/Segoe fallback for headers).
4. The inline diff is readable: muted red/green, not saturated.
5. Scrolling to the bottom loads older entries; refresh reloads and pagination still works.
6. Ctrl+Alt+D still corrects text while the dashboard is open.

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
| 2026-07-05 | Activity rows expose persisted timing telemetry | A hover clock lazily expands the clipboard, AI request, post-processing, paste, and total durations without adding hot-path work. |
| 2026-10-18 | Native Rust dashboard replaces WPF | The Rust port shipped native Win32 controls on a dedicated UI thread; the C# app was retired on 2026-11-06. The visual system above is unchanged. |

## App identity

The app mark combines a white lowercase a and checkmark on a rounded square: forest green for Prod, golden yellow for Dev. Source PNGs and multi-resolution Windows ICOs live in `rust/assets/`. The same channel mark appears in the tray, dashboard title/taskbar, sidebar, executable, and packaged Windows shortcuts. The prior generic Windows icon and runtime orange overlay are superseded.

Prod uses Elliot's corrected logo-no-outline.png, moved from Downloads to `src/Assets/logo.png` on 2026-10-11 and to `rust/assets/logo.png` on 2026-11-06. It supersedes the generated green image and its background-removal variants. The PNG is preserved unchanged; its Windows ICO uses transparent square padding to preserve the artwork's aspect ratio. Dev retains the approved yellow PNG and ICO unchanged. Both ICOs contain 16, 20, 24, 32, 48, 64, 128, and 256 pixel sizes.
