# Universal Spell Checker

## What This Is

A minimalist AutoHotkey script that provides instant AI-powered spell checking across all Windows applications. Select text, press Ctrl+Alt+U, and the corrected text replaces the selection in-place. Built for maximum speed and seamless operation with zero UI overhead.

## Core Value

Spell checking must feel instant and invisible — select, hotkey, done. Speed is the product.

## Requirements

### Validated

- Global hotkey (Ctrl+Alt+U) captures selected text and replaces it with corrected version — existing
- Multi-model support via top-level selector (gpt-4.1, gpt-5.1, gpt-5-mini) with correct parameter branching — existing
- OpenAI Responses API integration with 30s timeout and error capture — existing
- Regex-based JSON response parsing (~10x faster than object parsing) with Map-based fallback — existing
- Enhanced clipboard reading with HTML -> Unicode -> ANSI fallback chain — existing
- Post-processing replacements via `replacements.json` (hot-reloaded every run, case-sensitive, longest-first) — existing
- URL protection during replacements (http/https links preserved via placeholders) — existing
- Prompt-leak safeguard strips echoed instruction headers from model output — existing
- Per-app paste method (clipboard+Ctrl+V default, SendText for configured apps) — existing
- Clipboard history policy management (excludes transient data from Win+V) — existing
- Structured JSONL logging with 30+ fields (timings, tokens, active app, replacements, prompt leak) — existing
- Log rotation at 1MB with timestamped archives — existing
- UTF-8 response handling via ADODB.Stream (prevents mojibake) — existing
- Python log viewer generates interactive HTML dashboard with stats, filtering, search — existing

### Active

- [ ] Improve existing functionality and optimize performance
- [ ] (Specific requirements to be defined when work begins)

### Out of Scope

- New features from Scratchpad.md — those are idea parking, not current scope
- OAuth / multi-user support — this is a personal tool

## Context

- Project started as a simple AHK script and evolved through 5 major versions (V1 through current)
- Consolidated from per-model files into a single script with model selector (commit 59d6a9b)
- Migrated from Chat Completions API to Responses API (commit f516748)
- Extensive iteration on logging — from plain text to structured JSONL with full timing breakdown
- Post-processing replacements system added iteratively with URL protection, BOM handling, case-sensitive matching
- The user treats this as a daily-driver tool — reliability and speed are non-negotiable
- Scratchpad.md contains feature ideas and bug notes that informed Active requirements
- Hardcoded API key is a known concern (flagged in codebase map) but accepted for speed of startup

## Constraints

- **Platform**: Windows only — relies on AHK v2, WinHTTP COM, Windows clipboard API
- **Runtime**: AutoHotkey v2.0 interpreter must be installed
- **API**: OpenAI API key required with Responses API access
- **Performance**: Every operation must be optimized for minimal latency — speed is the core value
- **Simplicity**: Self-contained .ahk files with minimal external dependencies
- **No build step**: Scripts run directly, no compilation or bundling

## Key Decisions

| Decision | Rationale | Outcome |
|----------|-----------|---------|
| Single .ahk file with model selector | Maintaining 3 separate per-model files caused drift and bugs | Good |
| Regex-first JSON parsing | ~10x faster than full object parsing; only need one text field | Good |
| Hardcoded API key | Avoids config file parsing delay at startup; personal tool | Revisit |
| Responses API over Chat Completions | Required for reasoning models (gpt-5.x); unified endpoint | Good |
| JSONL logging format | Structured, parseable, one object per line; enables viewer | Good |
| Hot-reload replacements.json every run | Allows live editing without restarting script | Good |
| Clipboard-based paste as default | Fastest method; SendText fallback for incompatible apps | Good |

## Evolution

This document evolves at phase transitions and milestone boundaries.

**After each phase transition** (via `/gsd:transition`):
1. Requirements invalidated? -> Move to Out of Scope with reason
2. Requirements validated? -> Move to Validated with phase reference
3. New requirements emerged? -> Add to Active
4. Decisions to log? -> Add to Key Decisions
5. "What This Is" still accurate? -> Update if drifted

**After each milestone** (via `/gsd:complete-milestone`):
1. Full review of all sections
2. Core Value check — still the right priority?
3. Audit Out of Scope — reasons still valid?
4. Update Context with current state

---
*Last updated: 2026-03-27 after initialization*
