# CLAUDE.md — Master System Resolver

> **CRITICAL MANDATE:** Do not invent logic or guess how to execute tasks in this repo. Before editing code, answering detailed questions, or running a skill, match the user's intent to the routing table below and **read the referenced doc FIRST**. Do not rely on default assumptions or memory from prior sessions — docs evolve.

---

## Project Overview

**Universal Spell Checker** — a minimalist AutoHotkey v2 script that provides instant AI-powered spell checking across all Windows applications. Select text, press **Ctrl+Alt+U**, corrected text replaces the selection in place.

**Core value:** spell checking must feel instant and invisible — select, hotkey, done. Speed is the product.

**Stack:** AutoHotkey v2.0 (main script), Python 3 (log viewer + dataset tools), local `spellcheck-server.pyw` proxy (required), OpenAI Responses API. Windows only. No build step.

**Primary file:** `Universal Spell Checker.ahk` with top-level `modelModule` selector supporting `gpt-4.1`, `gpt-5.1`, `gpt-5-mini`. Configuration lives in `replacements.json`. Structured JSONL logs land in `logs/`.

**Tone when collaborating:** speed first, simplicity second, minimal UI/overhead third. Treat every added abstraction or fallback as a cost.

---

## Repo Map

```
Universal Spell Check/
├── Universal Spell Checker.ahk      # PRIMARY script — hotkey handler, model selector, pipeline
├── spellcheck-server.pyw            # Required local proxy (OpenAI Responses API forwarder)
├── replacements.json                # Post-processing brand/casing replacements (hot-reloaded)
│
├── generate_log_viewer.py           # Builds logs/viewer.html from JSONL logs
│
├── logs/                            # JSONL runtime logs (weekly files, rotates at 5 MiB → -2/-3/…)
│     └── viewer.html                # Generated log viewer (after running generate_log_viewer.py)
│
├── benchmark_data/                  # Frozen eval dataset — do not mutate casually
├── fine_tune_runs/                  # One dated folder per fine-tune run (file presence = state)
│
├── tests/                           # Pytest suites for the Python tooling
│     ├── test_benchmark_spellcheck_models.py
│     └── test_export_openai_finetune_dataset.py
│
├── .claude/skills/finetune-cycle/   # Fine-tune cycle skill + all scripts
│     ├── SKILL.md                   # Agent-facing workflow (invoke via /finetune-cycle)
│     ├── scripts/
│     │     ├── submit_finetune.py   # Upload + create job + poll
│     │     ├── benchmark_spellcheck_models.py
│     │     └── export_openai_finetune_dataset.py
│     └── references/
│           └── openai_finetune_api.md
│
├── docs/                            # Focused context docs — load via routing table below
│     ├── architecture.md
│     ├── model-config.md
│     ├── replacements-and-logging.md
│     ├── debugging-principles.md
│     ├── watchlist.md
│     └── conventions.md
│
├── Old Spell Check Version Files/   # Legacy variants — reference only, do NOT use as templates
├── .githooks/                       # Pre-commit (enforces scriptVersion bump when .ahk changes)
├── .planning/                       # GSD planning artifacts (if present)
└── CLAUDE.md                        # This file — routing table, not an encyclopedia
```

---

## 1. Task Routing — load the right doc before acting

| Intent | Read this first |
|---|---|
| Editing API payloads, switching models, anything about temperature/reasoning/verbosity | `docs/model-config.md` |
| File layout, processing flow, proxy recovery ladder, legacy files | `docs/architecture.md` |
| Replacements system, `replacements.json` cache, prompt-leak guard, logging/JSONL fields | `docs/replacements-and-logging.md` |
| Debugging a bug, verification standards, AHK v2 gotchas, regex-vs-JSON decisions | `docs/debugging-principles.md` |
| Clipboard/hotkey issues (Notepad etc.), cache edge cases, stale-reload checks | `docs/watchlist.md` |
| Naming, style, error-handling patterns, comment rules | `docs/conventions.md` |

If the task spans multiple areas, load each relevant doc before writing code.

---

## 2. Hard Rules (non-negotiable)

1. **Bump `scriptVersion`** before any commit that modifies `Universal Spell Checker.ahk` AND before asking the user to reload/retest. Treat mismatched `script_version` in logs as proof the user is running an older build.
2. **Proxy is mandatory.** Preserve the 5s / 30s / 60s recovery ladder with a full restart between attempts 2 and 3, then `ExitApp` on exhaustion.
3. **Never mix reasoning + standard params.** Standard GPT uses `temperature`; reasoning models use `reasoning.effort`. See `docs/model-config.md`.
4. **Debug before fixing.** When root cause is unclear, add logging first, analyze, then fix. No guessing patches.
5. **Simplest solution first** when performance matters. Regex > object parsing for single-field extraction.
6. **Verify every parameter** (not just model name/endpoint) when changing model configs.

---

## 3. Proactive Behavior

- After changes, review diffs for bugs without waiting for the user to ask.
- When file structure, flow, or config changes, update the relevant `docs/*.md` immediately — do not bloat this file.
- Ask clarifying questions up front when intent or scope is ambiguous (which script variant? which model? which commit?).
