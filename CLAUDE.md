# CLAUDE.md — Master System Resolver

> **CRITICAL MANDATE:** Do not invent logic or guess. Before editing code, answering detailed questions, or running a skill, match the user's intent to the routing table below and **read the referenced doc FIRST**. Do not rely on memory from prior sessions — docs evolve. If a task spans multiple rows, load each relevant doc before writing code.

---

## Grounding (always-loaded context)

**Universal Spell Check** is a Windows-wide AI spell checker. Select text → press hotkey → corrected text replaces selection in place. C#/.NET 10 WinForms tray app + WPF dashboard under `src/` is the product; legacy AHK is archived under `.archive/ahk-legacy/`.

**Core value:** select, hotkey, done. **Speed is the product** — instant and invisible. Every added abstraction or fallback is a cost.

**Two channels run side-by-side:** Prod (`Release`, Ctrl+Alt+U) and Dev (`Dev` config, Ctrl+Alt+D). Settings are isolated per channel; logs are unified into one shared corpus stamped per-line with `channel`/`app_version`/`pid` (this matters for fine-tune work). All channel constants live in `src/BuildChannel.cs` — never hardcode them elsewhere.

**Collaboration tone:** speed first, simplicity second, minimal UI/overhead third. Concise, action-oriented. Decide fast.

**Agent root:** `.agents` is the canonical editable root for agent files and helper scripts. `.claude` exists only as a compatibility junction; do not edit it directly.

For full project overview, stack details, and repo map → load `docs/overview.md`.

---

## 1. Task Routing — load the right doc before acting

| Intent | Read this first |
|---|---|
| Project overview, stack, repo map, channel summary | `docs/overview.md` |
| Native app architecture, tray lifetime, hotkey, loading overlay | `docs/architecture.md` |
| Adding app/site-specific formatting rules or Chrome target detection | `.planning/app-site-formatting-customizations.md` |
| Clipboard formatting fidelity, CF_HTML, preserving bold/links/spacing through a correction | `.planning/rich-text-clipboard-pipeline.md` |
| Channel separation, hotkey/mutex/path constants, version stamping | `src/BuildChannel.cs` (canonical source) |
| Editing API payloads, switching models, temperature/reasoning/verbosity | `docs/model-config.md` |
| Replacements system, prompt-leak guard, JSONL log fields | `docs/replacements-and-logging.md` |
| Reading, filtering, or analyzing runtime logs | Use the `read-logs` skill — run `python .agents/skills/read-logs/scripts/logs.py --help` for options. Use `--grep-detail <term>` or `--grep-detail field:term` to search inside spellcheck_detail blobs. Never manually parse log files. |
| Pushing to Dev channel or releasing to Production | Use the `deploy` skill. Always. Do not push or tag manually. |
| Auto-update flow, Velopack, release pipeline | `src/UpdateService.cs`, `.github/workflows/release.yml` |
| Debugging a bug, verification standards, runtime diagnostics | `docs/debugging-principles.md` |
| Clipboard/hotkey edge cases, loading overlay checks, cache pitfalls | `docs/watchlist.md` |
| Naming, style, error-handling, comments, C#/Python conventions | `docs/conventions.md` |
| Dashboard UI / WPF / colors / fonts / mockups / visual changes | `DESIGN.md` (always, before any visual change) |
| Working inside the WPF dashboard folder | `src/UI/CLAUDE.md` + `DESIGN.md` |
| Running, building, manual acceptance-testing the native app | `src/CLAUDE.md` |
| Python fine-tune dataset tooling, benchmarks, eval runs | `tests/CLAUDE.md` |
| Running the speed bench, comparing optimization variants, bench architecture, correctness gate | `docs/bench.md` |
| Dry-running text through replacements.json without the live app | `python tests/test-replacements.py "<text>"` — also accepts `--run <timestamp>` to replay a log entry, and `--show-skipped` to see rejected variants. |
| Tooling gaps, debugging workflow improvements, log reader / test / dry-run feature ideas | `docs/tooling-gaps.md` |
| Autonomous speed optimization loop (`/autoopt N`), behavioral contracts, worktree workflow | `docs/autoopt.md` |
| CI workflows, release tag automation | `.github/workflows/` (read the YAML) |
| Where to file new docs / when to add vs append / past filing mistakes | `docs/filing-rules.md` |
| Reviving the legacy AHK fallback | `.archive/ahk-legacy/` |

---

## 2. Hard Rules (non-negotiable)

1. **Channels are owned by `BuildChannel`.** Never hardcode a hotkey, mutex name, app-data folder, or display string. Add the constant to `BuildChannel.cs` and consume it.
2. **Logs are shared, settings are isolated.** `AppPaths.LogDirectory` returns the shared path; `AppDataDirectory` uses `BuildChannel.AppDataFolder`. Do not split logs by channel — the unified corpus is required for fine-tune work, and per-line `channel`/`app_version` stamps are the filter.
3. **One update flow.** Every entry point (launch, periodic, tray, dashboard) calls `UpdateService.CheckAsync(UpdateTrigger)`. Do not add parallel update paths.
4. **Releases ship via tag.** A semver `v*.*.*` tag triggers `.github/workflows/release.yml`. Do not run `vpk` manually for production.
5. **Never mix reasoning + standard params.** Standard GPT uses `temperature`; reasoning models use `reasoning.effort`. See `docs/model-config.md`.
6. **Debug before fixing.** When root cause is unclear, add logging first, analyze, then fix. No guessing patches.
7. **Native retests require rebuild/relaunch.** A code change is not running until the process is stopped and rebuilt. Prod owns Ctrl+Alt+U; Dev owns Ctrl+Alt+D — they can run side by side.
8. **Dashboard startup work must be bounded and yield to WPF.** Never perform log I/O, corpus-wide scans, recursive pagination, or unbounded diff construction on the dispatcher. Initial feed rendering is capped at one 30-entry page; additional pages may load only after a completed layout pass or user scroll. Run `--dashboard-smoke` against a production-sized log corpus after changing dashboard loading or rendering.
9. **User data never lives in Velopack's install root.** Prod settings, API key, logs, and state live under `%LocalAppData%\UniversalSpellCheck.Data`; `%LocalAppData%\UniversalSpellCheck` is installer-owned and may be replaced during reinstall. Keep migration from the legacy path intact.

---

## 3. Proactive Behavior

- After changes, review diffs for bugs without waiting for the user to ask.
- When file structure, flow, or config changes, update the relevant `docs/*.md` immediately — do not bloat this file. If it's a new category of content, see `docs/filing-rules.md`.
- Ask clarifying questions up front when scope is ambiguous (which channel? which model? which commit?).

---

## 4. Keeping This File Current

This routing table is only useful if it reflects reality. **Treat doc drift as a bug.**

- When you add/rename/delete a file referenced here, update the routing row in the same change.
- When a new subsystem, doc, or `CLAUDE.md` appears, add a routing row.
- When a Hard Rule is superseded by code (e.g., a constant moves), update the rule.
- When the Grounding header no longer matches what the project is doing (channel rename, stack change, new top-of-mind concern), revise it.
- **Flag drift proactively.** If during any task you notice this file, `docs/*.md`, or any subdir `CLAUDE.md` is stale, contradicts the code, or points to something that no longer exists — stop and surface it to the user with a proposed fix. Do not silently work around stale docs.
- Filing rules for new content live in `docs/filing-rules.md`.

---
*Maintenance: this file is a letter — short, intent-only, grown one earned
line at a time. Agent: as you work, watch for things this letter should
learn — new or ambiguous vocabulary (glossary candidates), corrections the
user has had to repeat, anything that changes the intent, rules, or routing
written here. When you notice one, invoke the `estack-claude-md-optimizer`
skill (quick capture): ask the user in one line whether to add it, and if yes do
it right then — under 5 minutes, do it now — then return to your task.
Never silently append, and never let a noticed improvement get lost. For
humans: run the skill directly — refine to audit, session-capture after
working sessions, scale-check before adding any routing.*
