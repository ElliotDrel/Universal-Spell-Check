# Filing Rules — Where Things Go

Disambiguation for content placement. Routed from root `CLAUDE.md` when scope is "where should this live?" or "should I add a new doc or append?"

## The core question: who needs this, and when?

| If the content is... | It goes in... |
|---|---|
| A non-negotiable rule that applies to most edits in this repo | Root `CLAUDE.md` § Hard Rules |
| A pointer telling Claude "for X, read Y first" | Root `CLAUDE.md` § Task Routing |
| Background on the project, stack, repo layout | `docs/overview.md` |
| Detailed how-it-works for one subsystem (architecture, model config, logging, etc.) | A focused `docs/<topic>.md`. Append if the topic exists; create new only if it's a genuinely new subsystem. |
| A short grounding header + local routing for one directory | `<dir>/CLAUDE.md` (3-10 lines of context, then pointers — not an essay) |
| Visual design (colors, fonts, mockups, layout) | `DESIGN.md` (singular root-level file by convention) |
| A coding convention or style rule | `docs/conventions.md` |
| A debugging principle / verification standard | `docs/debugging-principles.md` |
| A subtle bug pattern or edge case to watch for | `docs/watchlist.md` (append a new entry; don't create a new doc) |
| A model/temperature/reasoning param decision | `docs/model-config.md` |
| Replacement-list or JSONL-log-field semantics | `docs/replacements-and-logging.md` |
| A runbook for running/building/manually testing | The closest `<dir>/CLAUDE.md` (e.g., `src/CLAUDE.md`) |
| Comments explaining *why* the code is unusual | Inline in the source file (one short line; never multi-paragraph) |
| Comments explaining *what* the code does | Don't write them — name things clearly instead |

## Append vs. create new

**Default to append.** A new file is justified only when:
1. The topic is genuinely orthogonal to every existing doc, AND
2. It will accumulate enough content (> ~30 lines) to warrant its own routing entry.

If you create a new `docs/*.md`, you MUST add a row to the root `CLAUDE.md` routing table in the same change. An unrouted doc is dead — it will not be discovered.

## Subdirectory `CLAUDE.md` files

Each subdir-level `CLAUDE.md` should have:
1. A **3-5 line grounding header** — what this dir is, the high-level goal it serves, what's currently top-of-mind.
2. A **mandate** to read root routing + the most relevant `docs/*.md` before acting in this dir.
3. **Local-only content**: things that are only useful when working inside this dir (run commands, manual checks, dir-specific gotchas).

Subdir CLAUDE.md files are NOT mini-encyclopedias. If content applies repo-wide, it belongs in root or `docs/`.

## Past mistakes catalog

Add an entry here whenever a filing decision was reverted or caused confusion. Empty for now — populate as we hit edge cases.

| Date | Mistake | Lesson |
|---|---|---|
| _(empty — populate on first incident)_ | | |

## When in doubt

Ask the user. A 10-second clarification beats reshuffling docs later.
