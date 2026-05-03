# Universal Spell Check — Product

## Register

**product** — this is a tool that serves the user's work, not a marketing surface. Design serves the product, not the other way around.

## What it is

A Windows-wide AI spell checker that lives in the system tray. The user selects text in any app, presses a global hotkey, and the selection is replaced in place with a corrected version.

The **WPF dashboard** is the secondary surface: opened from the tray, it lets the user verify what the AI has been doing, manage the API key, edit custom replacements, and configure runtime behavior.

The hotkey is the product. The dashboard exists to make the hotkey trustworthy.

## Users

- **Primary:** the developer (solo tool, dogfooded daily). A power user who keeps the tray app running all day across writing, code, email, chat.
- **Future:** other writing-heavy power users on Windows who want spell check that works *everywhere*, not just inside Word/Gmail.

The user is technical, opinionated, fast, and allergic to fake controls and vanity metrics.

## Product purpose

The dashboard is **visit-driven**, not daily-driven. The user opens it when they want to see something specific, not as a habit. Three reasons to open it, in priority order:

1. **Insight (primary)** — *"What has the spell checker been doing? What got corrected, when, and what patterns am I seeing?"* History of corrections is the spine of the dashboard. This includes the activity feed (every diff), and patterns over time (words that keep recurring, time-of-day, model behavior). Skimming history is also how the user builds trust in the tool.
2. **Diagnostics** — *"Something felt off — why was that slow? Why did it not replace? What did the model actually return?"* The user must be able to answer this without opening a `.jsonl` in a text editor.
3. **Control** — *"Change the API key. Edit my replacements. Toggle startup. Switch model."* The small set of knobs that actually affect behavior. Real and editable, no fake/disabled controls. This is utility, not a destination.

Anything that doesn't serve one of these three is decoration and should be removed.

## Brand / tone

- **Voice:** confident, terse, opinionated. No marketing prose. No emojis. No "Welcome!" copy.
- **Memorable thing:** "this is serious software" — editorial typography, cream canvas, hairline borders. Reads like a tool a craftsman picks up, not a SaaS template.
- **Reference:** Wispr Flow desktop app (cream tones, editorial type, calm density). Linear and Things 3 for product UI restraint.

## Anti-references

- **Generic Windows utility chrome** — Segoe UI everywhere, Mica gray, Settings-app row layouts. Boring, indistinguishable from a hundred other tray tools.
- **SaaS dashboard cliché** — hero metric tiles, gradient accents, "Welcome back, [name]!", hollow ring charts, identical 3-up card grids. This is not Stripe.
- **Fake controls** — disabled buttons that exist only to imply a feature ("Change hotkey" greyed out forever). If a control isn't wired, it doesn't ship.
- **Vanity stats** — "accuracy: 87%", "day streak: 4". The user did not download this to play a game. Stats must answer a real question or they don't exist.
- **Notepad-as-editor** — opening `replacements.json` in the system text editor and calling it "Edit list" is a punt. It's the right v0; it's wrong as a v1 destination.

## Strategic principles

- **Speed is the product.** This applies to the hotkey path, not the dashboard, but the dashboard must never *feel* slower than the hotkey. No spinners on tabs that should be instant.
- **Every pixel earns its place.** A revamp is the chance to *delete*, not just rearrange. Default answer to "should we add X?" is no.
- **Honest controls over impressive ones.** A working "Open replacements file" beats a half-built in-app editor. Only ship a richer control when the wiring is real.
- **The log is the source of truth.** All dashboard data comes from the unified JSONL log (channel-stamped). The dashboard is a viewer over that log, not a parallel database.
- **Channel-aware where it matters.** The user runs Prod and Dev side-by-side; the dashboard should make it obvious which channel's window is which without forcing the user to read the title bar.

## Success criteria for the revamp

The redesign succeeds if, three weeks after shipping, the user:

1. **Reaches for the dashboard when curious** — when a question pops up ("how often did I use this today?", "what did the AI just do?", "why was that slow?") the dashboard is the obvious answer, not the log file.
2. **Insight tab is the default landing page** and feels worth reopening — history is browsable, scannable, and patterns surface without effort.
3. **Zero disabled or fake controls visible anywhere.** Every knob shown is real and works.
4. **Diagnostics surface answers a "why was that slow / why did it fail" question in under 10 seconds**, without opening a `.jsonl` in a text editor.
