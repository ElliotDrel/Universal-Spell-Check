---
name: read-logs
description: >
  Read, filter, and analyze Universal Spell Check JSONL logs using the bundled CLI script.
  Use this skill whenever you need to investigate logs, debug an issue, check recent runs,
  look for errors or failures, audit performance/latency, or answer "what happened" questions
  about the app. Always use the script — don't manually parse log files. Invoke this whenever
  the user mentions logs, errors, latency, failures, debugging, "what happened", or asks to
  check runs in a specific app.
---

# Log Reader

## Script location

```
.claude/skills/read-logs/scripts/logs.py
```

Run from the repo root (the working directory is always the repo root in this project):

```powershell
python .claude/skills/read-logs/scripts/logs.py [options]
```

## Log file location

`%LOCALAPPDATA%\UniversalSpellCheck.Data\logs\spellcheck-YYYY-MM-DD.jsonl`

Both `prod` and `dev` channels write to the same files, stamped per-line with `channel` and `app_version`.

## Common commands (run these immediately — no manual reading needed)

### What happened today?
```powershell
python .claude/skills/read-logs/scripts/logs.py --today
```

### Today's aggregate stats (success rate, latency, tokens, top apps)
```powershell
python .claude/skills/read-logs/scripts/logs.py --today --stats
```

### Check for errors and failures
```powershell
python .claude/skills/read-logs/scripts/logs.py --today --errors
```

### Last N spellcheck runs (most recent first after filtering)
```powershell
python .claude/skills/read-logs/scripts/logs.py --today --event spellcheck_detail --last 10
```

### Filter by app (e.g. only runs in Chrome, Slack, VS Code)
```powershell
python .claude/skills/read-logs/scripts/logs.py --today --app chrome
python .claude/skills/read-logs/scripts/logs.py --today --app slack --stats
```

### Date range
```powershell
python .claude/skills/read-logs/scripts/logs.py --from 2026-05-20 --to 2026-05-24 --stats
python .claude/skills/read-logs/scripts/logs.py --from 2026-05-20 --to 2026-05-24 --errors
```

### Filter by channel
```powershell
python .claude/skills/read-logs/scripts/logs.py --today --channel dev
python .claude/skills/read-logs/scripts/logs.py --today --channel prod --stats
```

### Specific event type
```powershell
python .claude/skills/read-logs/scripts/logs.py --today --event run_completed
python .claude/skills/read-logs/scripts/logs.py --today --event update_check_done
```

### Raw lines (for grepping or copy-pasting to another tool)
```powershell
python .claude/skills/read-logs/scripts/logs.py --today --raw --event request_failed
```

### JSON output (for programmatic processing)
```powershell
python .claude/skills/read-logs/scripts/logs.py --today --json --event spellcheck_detail
```

### Search inside spellcheck_detail content (input/output/raw AI output)
```powershell
# Plain substring — searches input_text, output_text, and raw_ai_output (case-insensitive)
python .claude/skills/read-logs/scripts/logs.py --today --grep-detail competition
# Field-scoped — restrict to one field
python .claude/skills/read-logs/scripts/logs.py --today --grep-detail output_text:Competitionetition
python .claude/skills/read-logs/scripts/logs.py --today --grep-detail raw_ai_output:competition --last 10
```
This is the way to find "all runs where X appeared in the input/output" without writing a throwaway JSON parser. Implicitly restricts to `spellcheck_detail` events. A query containing `:` is treated as `field:value`.

### Runs whose selection carried rich-text markup
```powershell
python .claude/skills/read-logs/scripts/logs.py --from 2026-07-20 --to 2026-07-31 --has-html
python .claude/skills/read-logs/scripts/logs.py --today --has-html --json   # includes the markup
python .claude/skills/read-logs/scripts/logs.py --today --grep-detail clipboard_html:margin-bottom
```
Every run logs what the source app offered: `clipboard_html` (CF_HTML verbatim), `clipboard_rtf`, each with `_chars` and `_truncated` siblings (capped at 512K), plus `clipboard_formats` listing every format name present. Use `--has-rich` for HTML **or** RTF. The formatted view prints sizes and the format list — use `--json` to get the markup itself. Plain `--grep-detail` does **not** search these fields; scope explicitly with `clipboard_html:...`. `clipboard_formats` is the field that tells you whether an empty `clipboard_html` means "no markup offered" or "markup we missed". Background: `.planning/rich-text-clipboard-pipeline.md`.

## All flags

| Flag | What it does |
|---|---|
| `--today` | Today's log only (default if no date given) |
| `--from YYYY-MM-DD` | Start date for range |
| `--to YYYY-MM-DD` | End date for range |
| `--event EVENT` / `-e` | Filter to one event name |
| `--channel prod\|dev` / `-c` | Filter by channel |
| `--app EXE` / `-a` | Filter spellcheck_detail by active_exe substring |
| `--errors` | Only error/failure events |
| `--stats` / `-s` | Aggregate stats for matching spellcheck_detail events |
| `--last N` / `-n` | Show last N matching lines |
| `--raw` | Print raw JSONL lines |
| `--json` | Output parsed JSON objects one per line |
| `--grep-detail QUERY` | Filter spellcheck_detail by substring in input_text/output_text/raw_ai_output; `field:value` scopes to one field |
| `--has-html` | Only runs whose selection carried a CF_HTML flavor; markup is in the `clipboard_html` field |
| `--has-rich` | Runs whose selection carried any rich flavor (CF_HTML or RTF) |
| `--log-dir PATH` | Override log directory |

## Error events caught by `--errors`

`request_failed`, `request_retrying`, `capture_failed`, `paste_failed`,
`replacements_reload_failed`, `guard_rejected`, `connection_warm_failed`

## Key event names (for `--event`)

| Event | Meaning |
|---|---|
| `spellcheck_detail` | Full per-run JSON blob with input/output/timings/tokens |
| `run_completed` | End of pipeline; inline timing/status summary |
| `hotkey_pressed` | User triggered the hotkey |
| `request_failed` | API call failed |
| `request_retrying` | Retrying API call |
| `capture_failed` | Clipboard capture failed |
| `paste_failed` | Paste back failed (focus changed, Ctrl+V failed) |
| `guard_rejected` | Overlapping hotkey press blocked |
| `started` | App boot |
| `stopping` | Clean shutdown |
| `update_check_start/done` | Auto-update check |
| `connection_warm_failed` | Background connection warm-up failed |

## Workflow for debugging an issue

1. Start with `--today --errors` to surface any failures immediately.
2. Run `--today --stats` for the full picture (success rate, latency trend).
3. If a specific app or event is suspect, add `--app` or `--event` to narrow down.
4. Use `--last 5` on `spellcheck_detail` to read the most recent individual runs in detail.
5. Use `--raw` if you need to see the full unformatted line (e.g., to check a specific field not shown in formatted output).
6. Use `--from/--to` with `--stats` to compare a date range vs. today.
