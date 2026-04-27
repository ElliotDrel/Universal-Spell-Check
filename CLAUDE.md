# CLAUDE.md - Master System Resolver

> **CRITICAL MANDATE:** Do not invent logic or guess how to execute tasks in this repo. Before editing code, answering detailed questions, or running a skill, match the user's intent to the routing table below and **read the referenced doc FIRST**. Do not rely on default assumptions or memory from prior sessions - docs evolve.

---

## Project Overview

**Universal Spell Checker** - a Windows-wide AI spell checker. The native C#/.NET WinForms app under `native/UniversalSpellCheck` is now the main app: select text, press **Ctrl+Alt+U**, corrected text replaces the selection in place. The AutoHotkey script remains in the repo as a fallback/reference path, but its Startup shortcut has been removed.

**Core value:** spell checking must feel instant and invisible - select, hotkey, done. Speed is the product.

**Stack:** Native C#/.NET WinForms tray app (main), AutoHotkey v2.0 (legacy fallback script), Python 3 (log viewer + dataset tools), local `spellcheck-server.pyw` proxy (required for AHK only), OpenAI Responses API. Windows only. AHK has no build step; native app builds with `dotnet`.

**Primary files:** `native/UniversalSpellCheck/Program.cs` is the native app entrypoint. `Universal Spell Checker.ahk` is the legacy AHK fallback with top-level `modelModule` selector supporting `gpt-4.1`, `gpt-5.1`, `gpt-5-mini`. Configuration lives in `replacements.json`. AHK structured JSONL logs land in `logs/`; native logs land under `%LOCALAPPDATA%\UniversalSpellCheck\native-spike-logs\`.

**Tone when collaborating:** speed first, simplicity second, minimal UI/overhead third. Treat every added abstraction or fallback as a cost.

---

## Repo Map

```text
Universal Spell Check/
|-- Universal Spell Checker.ahk      # PRIMARY AHK script - hotkey handler, model selector, pipeline
|-- spellcheck-server.pyw            # Required AHK local proxy (OpenAI Responses API forwarder)
|-- replacements.json                # Post-processing brand/casing replacements
|-- generate_log_viewer.py           # Builds logs/viewer.html from JSONL logs
|-- NATIVE_APP_FUTURE_TODO.md        # Root native follow-up work list
|
|-- native/
|   `-- UniversalSpellCheck/         # C#/.NET WinForms main app (Ctrl+Alt+U)
|       |-- Program.cs               # Single-instance native app entrypoint
|       |-- SpellCheckAppContext.cs  # Tray lifetime, menu, settings, busy overlay ownership
|       |-- SpellcheckCoordinator.cs # Serialized capture/request/post-process/paste pipeline
|       |-- OpenAiSpellcheckService.cs # Persistent HttpClient + Responses API request
|       |-- TextPostProcessor.cs     # replacements.json + prompt-leak guard
|       |-- LoadingOverlayForm.cs    # Bottom-center "Spell check loading..." progress bar
|       |-- README.md                # Native run/publish/manual-test notes
|       `-- CUTOVER.md               # Native-vs-AHK comparison and rollback notes
|
|-- logs/                            # AHK JSONL runtime logs; viewer.html is generated
|-- benchmark_runs/                  # One dated folder per standalone benchmark run
|-- fine_tune_runs/                  # One dated folder per fine-tune run
|-- tests/                           # Pytest suites for Python tooling
|-- docs/                            # Focused context docs - load via routing table below
|   |-- architecture.md
|   |-- model-config.md
|   |-- replacements-and-logging.md
|   |-- debugging-principles.md
|   |-- watchlist.md
|   `-- conventions.md
|-- Old Spell Check Version Files/   # Legacy variants - reference only, do NOT use as templates
|-- .githooks/                       # Pre-commit hook for AHK scriptVersion bumps
|-- .planning/                       # Planning artifacts
`-- CLAUDE.md                        # This file - routing table, not an encyclopedia
```

---

## 1. Task Routing - load the right doc before acting

| Intent | Read this first |
|---|---|
| Editing API payloads, switching models, anything about temperature/reasoning/verbosity | `docs/model-config.md` |
| AHK file layout, processing flow, proxy recovery ladder, legacy files | `docs/architecture.md` |
| Native app architecture, tray lifetime, hotkey, loading overlay, cutover state | `docs/architecture.md`, then `native/UniversalSpellCheck/README.md` and `native/UniversalSpellCheck/CUTOVER.md` |
| Replacements system, `replacements.json` cache, prompt-leak guard, logging/JSONL/native log fields | `docs/replacements-and-logging.md` |
| Debugging a bug, verification standards, AHK v2 gotchas, native runtime diagnostics | `docs/debugging-principles.md` |
| Clipboard/hotkey issues, native loading overlay checks, cache edge cases, stale-reload checks | `docs/watchlist.md` |
| Naming, style, error-handling patterns, comments, AHK/Python/C# conventions | `docs/conventions.md` |
| Native future work, cutover blockers, rich-text/app-specific ideas | `NATIVE_APP_FUTURE_TODO.md` |
| Native dashboard UI / WPF / visual design / colors / fonts / mockups | `DESIGN.md` (always read before any visual change) |

If the task spans multiple areas, load each relevant doc before writing code.

---

## 2. Hard Rules (non-negotiable)

1. **Bump `scriptVersion`** before any commit that modifies `Universal Spell Checker.ahk` AND before asking the user to reload/retest. Treat mismatched `script_version` in logs as proof the user is running an older AHK build.
2. **Proxy is mandatory for AHK.** Preserve the 5s / 30s / 60s recovery ladder with a full restart between attempts 2 and 3, then `ExitApp` on exhaustion. The native app does not use the Python proxy.
3. **Never mix reasoning + standard params.** Standard GPT uses `temperature`; reasoning models use `reasoning.effort`. See `docs/model-config.md`.
4. **Debug before fixing.** When root cause is unclear, add logging first, analyze, then fix. No guessing patches.
5. **Simplest solution first** when performance matters. Regex > object parsing for single-field extraction.
6. **Verify every parameter** (not just model name/endpoint) when changing model configs.
7. **Native owns `Ctrl+Alt+U`.** Do not restart the legacy AHK spell checker unless the user explicitly wants to use the fallback path, because it can conflict with the native hotkey.
8. **Native retests require rebuild/publish/relaunch.** A code change under `native/UniversalSpellCheck` is not running until the process is stopped, rebuilt/published, and relaunched.

---

## 3. Proactive Behavior

- After changes, review diffs for bugs without waiting for the user to ask.
- When file structure, flow, or config changes, update the relevant `docs/*.md` immediately - do not bloat this file.
- Ask clarifying questions up front when intent or scope is ambiguous (which app path? which script variant? which model? which commit?).
