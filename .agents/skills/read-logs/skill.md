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
.agents/skills/read-logs/scripts/logs.py
```

Run from the repo root (the working directory is always the repo root in this project):

```powershell
python .agents/skills/read-logs/scripts/logs.py [options]
```

## Log file location

`%LOCALAPPDATA%\UniversalSpellCheck.Data\logs\spellcheck-YYYY-MM-DD.jsonl`

Both `prod` and `dev` channels write to the same files, stamped per-line with `channel` and `app_version`.

## Common commands (run these immediately — no manual reading needed)

### What happened today?
```powershell
python .agents/skills/read-logs/scripts/logs.py --today
```

### Today's aggregate stats (success rate, latency, tokens, top apps)
```powershell
python .agents/skills/read-logs/scripts/logs.py --today --stats
```

### Check for errors and failures
```powershell
python .agents/skills/read-logs/scripts/logs.py --today --errors
```

### Last N spellcheck runs (across midnight)
```powershell
python .agents/skills/read-logs/scripts/logs.py --event spellcheck_detail --last 2
```

### Filter by app (e.g. only runs in Chrome, Slack, VS Code)
```powershell
python .agents/skills/read-logs/scripts/logs.py --today --app chrome
python .agents/skills/read-logs/scripts/logs.py --today --app slack --stats
```

### Date range
```powershell
python .agents/skills/read-logs/scripts/logs.py --from 2026-05-20 --to 2026-05-24 --stats
python .agents/skills/read-logs/scripts/logs.py --from 2026-05-20 --to 2026-05-24 --errors
```

### Filter by channel
```powershell
python .agents/skills/read-logs/scripts/logs.py --today --channel dev
python .agents/skills/read-logs/scripts/logs.py --today --channel prod --stats
```

### Specific event type
```powershell
python .agents/skills/read-logs/scripts/logs.py --today --event run_completed
python .agents/skills/read-logs/scripts/logs.py --today --event update_check_done
```

### Raw lines (for grepping or copy-pasting to another tool)
```powershell
python .agents/skills/read-logs/scripts/logs.py --today --raw --event request_failed
```

### JSON output (for programmatic processing)
```powershell
python .agents/skills/read-logs/scripts/logs.py --today --json --event spellcheck_detail
```

### Search inside spellcheck_detail content (input/output/raw AI output)
```powershell
# Plain substring — searches input_text, output_text, and raw_ai_output (case-insensitive)
python .agents/skills/read-logs/scripts/logs.py --today --grep-detail competition
# Field-scoped — restrict to one field
python .agents/skills/read-logs/scripts/logs.py --today --grep-detail output_text:Competitionetition
python .agents/skills/read-logs/scripts/logs.py --today --grep-detail raw_ai_output:competition --last 10
```
This is the way to find "all runs where X appeared in the input/output" without writing a throwaway JSON parser. Implicitly restricts to `spellcheck_detail` events. A query containing `:` is treated as `field:value`.

### Runs whose selection carried rich-text markup
```powershell
python .agents/skills/read-logs/scripts/logs.py --from 2026-07-20 --to 2026-07-31 --has-html
python .agents/skills/read-logs/scripts/logs.py --today --has-html --json   # includes the markup
python .agents/skills/read-logs/scripts/logs.py --today --grep-detail clipboard_html:margin-bottom
```
Every run logs what the source app offered: `clipboard_html` (CF_HTML verbatim), `clipboard_rtf`, each with `_chars` and `_truncated` siblings (capped at 512K), plus `clipboard_formats` listing every format name present. The final requested replacement is in `paste_text` and `paste_html`, also capped at 512K with `_chars` and `_truncated` siblings. Use `--has-rich` for source HTML **or** RTF. The formatted view prints sizes and the format list — use `--json` for exact payloads. Plain `--grep-detail` does **not** search markup by default; scope it with `clipboard_html:...` or `paste_html:...`. Background: `.planning/rich-text-clipboard-pipeline.md`.

## All flags

| Flag | What it does |
|---|---|
| `--today` | Today's log only (default unless `--last` is used without dates) |
| `--from YYYY-MM-DD` | Start date for range |
| `--to YYYY-MM-DD` | End date for range |
| `--event EVENT` / `-e` | Filter to one event name |
| `--channel prod\|dev` / `-c` | Filter by channel |
| `--app EXE` / `-a` | Filter spellcheck_detail by active_exe substring |
| `--errors` | Only error/failure events |
| `--stats` / `-s` | Aggregate stats for matching spellcheck_detail events |
| `--last N` / `-n` | Show last N matching lines; searches the past 30 days if no dates are given |
| `--raw` | Print raw JSONL lines |
| `--json` | Output parsed JSON objects one per line |
| `--grep-detail QUERY` | Filter spellcheck_detail by substring in input_text/output_text/raw_ai_output; `field:value` scopes to one field |
| `--has-html` | Only runs whose selection carried a CF_HTML flavor; markup is in the `clipboard_html` field |
| `--has-rich` | Runs whose selection carried any rich flavor (CF_HTML or RTF) |
| `--log-dir PATH` | Override log directory |

## Error events caught by `--errors`

`request_failed`, `request_retrying`, `capture_failed`, `paste_failed`,
`replacements_reload_failed`, `guard_rejected`, `connection_warm_failed`,
`data_migration_failed`, `activity_load_failed`. A successful `spellcheck_detail`
can still represent wrong or missing text; `--errors` cannot detect that.

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

1. For "last spell check" or "one before that," run `--event spellcheck_detail --last 2` without `--today`. It searches across midnight. Use the timestamp, channel, process, and window title to select the run; add `--json` to inspect exact text and HTML.
2. Compare `input_text` with `clipboard_html`, then `raw_ai_output`, `output_text`, `paste_text`, `paste_html`, `rich_text.mode/reason`, and `clipboard_html_verification`. A successful status confirms the pipeline returned, not the destination's rendered layout. Older runs do not have `paste_text` or `paste_html`.
3. Check `--errors` and `finalize_failed` when the run failed or is missing; match events by timestamp, channel, and PID. Use `--stats` only when looking for a pattern across runs.
4. Reproduce the rich-text decision without a live paste by piping one `--json` row into `dotnet run --project tests/TargetFormattingTests/UniversalSpellCheck.TargetFormattingTests.csproj -c Dev --no-restore -- --replay-stdin`. This uses the current mapper, so identify the original build before interpreting a historical replay.
