# Architecture & File Layout

## Active files
- **Universal Spell Checker.ahk**: Primary script with `modelModule` selector (`gpt-4.1`, `gpt-5.1`, `gpt-5-mini`). `scriptVersion` is a manual integer-like string near the top; bump before any commit that touches this file and before asking the user to reload/retest.
- **replacements.json**: Post-processing replacement pairs — `{ "canonical": ["variant1", ...] }`.
- **spellcheck-server.pyw**: Local proxy. Required. Script hard-fails if it cannot be started or recovered.
- **generate_log_viewer.py**: Reads `logs/*.jsonl` → `logs/viewer.html`. Run `python generate_log_viewer.py` (add `--open`).
- **export_openai_finetune_dataset.py**: Builds fine-tune datasets from live logs.

## Training data layout
- `fine_tune_runs/` — one dated folder per fine-tune run; contains train/val JSONL, finetune_job.json, benchmark.json, summary.md.
- `benchmark_runs/` — one dated folder per standalone benchmark run.

## Fine-tune refresh
```powershell
python export_openai_finetune_dataset.py --source logs --weeks 8 --max-per-bucket 15
```

## Legacy (reference only, do not use as template)
- `Universal Spell Checker - SEND TEXT instead of ctr+v.ahk` — minimal `SendText()` variant, no logging/post-processing.
- `Universal Spell Checker App/`, `Universal Spell Check - V1/V2/V3/V3.5.ahk`, `spellcheck*.js` — abandoned.

## Text processing flow
1. User selects text, presses Ctrl+Alt+U.
2. Replacements loaded once at startup; reparsed only when `replacements.json` modified-time or size changes.
3. At startup, script requires healthy proxy. Recovery ladder: 3 attempts with 5s / 30s / 60s readiness budgets; full shutdown+restart between attempts 2 and 3; `ExitApp` on exhaustion.
4. Default apps use the fast single-copy path. `notepad.exe` waits for hotkey release, retries `Ctrl+C` up to 3 times, and aborts paste if `Ctrl+Alt+U` never fully releases.
5. `GetClipboardText()` reads clipboard, preferring HTML (strips formatting) → Unicode → ANSI.
6. Pre-request proxy health check; reruns recovery ladder if proxy died mid-session.
7. Sends to local proxy → OpenAI Responses API via warm connection pool.
8. Parse: regex primary → Map-based fallback.
9. `ApplyReplacements()` fixes brand/term casing.
10. `StripPromptLeak()` strips echoed instruction blocks.
11. Paste via Ctrl+V or `SendText()` depending on `sendTextApps`.

## Design principles
- Speed first — every op optimized for latency.
- Self-contained .ahk files; only external dep is `replacements.json`.
- Direct clipboard manipulation for instant replacement.
- One file supports all target models via top-level selector.

## Constraints
- Windows only, AutoHotkey v2.0 required.
- OpenAI API key with Responses API access.
- No build step.
