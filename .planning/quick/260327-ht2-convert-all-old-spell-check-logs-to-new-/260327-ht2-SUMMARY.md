---
phase: 260327-ht2
plan: 01
subsystem: logging
tags: [conversion, migration, python, jsonl]
dependency_graph:
  requires: []
  provides: [convert_old_logs.py, weekly-jsonl-files, logs-archive]
  affects: [generate_log_viewer.py]
tech_stack:
  added: []
  patterns: [state-machine-parser, binary-read-encoding-safety]
key_files:
  created:
    - convert_old_logs.py
  modified:
    - generate_log_viewer.py
decisions:
  - "Binary file read for Format 1 to avoid Python universal newline splitting on embedded \\r"
  - "3-char replacement (backslash-backslash-n) for AHK escaped JSON newlines in pipe-delimited log"
  - "No actual Oct 14 overlap found -- timestamps don't match between formats, dedup set built but zero skips"
metrics:
  duration_seconds: 671
  completed: 2026-03-27T13:35:00
  tasks_completed: 2
  tasks_total: 2
  files_created: 1
  files_modified: 1
---

# Quick Task 260327-ht2: Convert Old Spell-Check Logs to JSONL Summary

Python conversion script that parsed 10,587 legacy spell-check entries across 2 format families and 4 eras (Jul 2025 - Mar 2026) into weekly JSONL files matching the live logger schema, with token extraction from both Chat Completions and Responses API formats.

## Task Results

### Task 1: Build the conversion script with all parsers and file routing

**Commit:** e4475f2

Built `convert_old_logs.py` (996 lines) with:
- **Format 1 parser**: Binary file read with manual `\r` stripping to avoid Python's universal newline splitting on embedded carriage returns in the pipe-delimited log. Left-to-right marker-based field extraction (not blind `split(" | ")`). Sub-era 1a (17 entries, no Raw AI) and 1b (2,383 entries) detection.
- **Format 2 parser**: State-machine section extractor that identifies sections by label (not position), handling all 4 era orderings. Delimiter pattern detection: DELIM/RUN:/DELIM/content/DELIM.
- **Token extraction**: Automatic API type detection via `"object"` field, with correct field mapping for Chat Completions (`prompt_tokens`) and Responses API (`input_tokens`).
- **Week routing**: Duplicated functions from `generate_log_viewer.py` for self-contained operation.
- **Dry-run mode**: Full parse and report without writing files.
- **`_converted` metadata**: Source file, format era, timestamp, version on every entry.

**Key challenge**: The `OLD - spellcheck.log` file contains standalone `\r` (carriage return) bytes within entry text fields. Python's default text mode treats these as line separators, splitting entries mid-field. Solved by reading in binary mode, decoding manually, and stripping `\r` before splitting on `\n`.

**Key challenge**: The AHK logger wrote JSON newlines as the literal 3-byte sequence `\`, `\`, `n` in the file. After reading, this becomes two backslash characters + `n` in the Python string. The `json.loads()` call requires replacing this 3-char sequence with actual newlines first.

### Task 2: Run live conversion, verify output, validate viewer

**Commit:** 0d2f305

Ran the full conversion and validated all output:

| Metric | Value |
|--------|-------|
| Format 1a entries | 17 |
| Format 1b entries | 2,383 |
| Format 2a entries | 2,392 |
| Format 2b entries | 4,146 |
| Format 2c entries | 226 |
| Format 2d entries | 1,423 |
| **Total converted** | **10,587** |
| Oct 14 dedup skipped | 0 |
| Weekly files created | 38 |
| Parse errors | 0 |
| JSON validation errors | 0 |
| Largest weekly file | 2.53 MiB |
| Files archived | 20 |

**Spot checks passed:**
- Earliest entry (Jul 12 2025, Format 1a): Correct timestamp, input, status, null tokens
- Format 2a entry (Oct 14 2025): Chat Completions tokens extracted (input=110, output=57)
- Format 2d entry (Mar 16 2026): Full timings, replacements, prompt_leak, events populated

**Viewer validation**: `python generate_log_viewer.py --no-open` succeeded, generating 29.1 MB viewer with 10,603 entries (10,587 converted + 16 live).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Binary file read for Format 1 encoding**
- **Found during:** Task 1
- **Issue:** Python's universal newline mode splits on standalone `\r` characters embedded in the pipe-delimited log's field values, breaking ~700 entries across lines
- **Fix:** Read file in binary mode, decode as UTF-8 with `errors="replace"`, strip `\r` manually before splitting on `\n`
- **Files modified:** convert_old_logs.py
- **Commit:** e4475f2

**2. [Rule 1 - Bug] AHK escaped JSON newline replacement**
- **Found during:** Task 1
- **Issue:** The `\\n` escape sequences in the Raw AI field are stored as 3-byte sequences (backslash, backslash, n) in the file. Using `replace("\\n", "\n")` (Python 2-char match) missed them; needed `replace("\\\\n", "\n")` (Python 3-char match)
- **Fix:** Corrected the replacement to match the actual 3-character sequence
- **Files modified:** convert_old_logs.py
- **Commit:** e4475f2

**3. [Rule 3 - Blocking] Viewer null-safety for timings/tokens**
- **Found during:** Task 2 viewer validation
- **Issue:** `generate_log_viewer.py` `compute_stats()` used `e.get("timings", {}).get("api_ms", 0)` which returns `None` when `timings` is explicitly `null` (key exists with None value), causing `AttributeError`
- **Fix:** Changed to `(e.get("timings") or {}).get(...)` pattern for both timings and tokens
- **Files modified:** generate_log_viewer.py
- **Commit:** 0d2f305

## Decisions Made

1. **Binary read for Format 1**: Required because the AHK logger embedded standalone `\r` bytes in text fields, which Python's universal newline mode treats as line separators.

2. **No actual Oct 14 overlap**: Despite both formats having entries on Oct 14, the timestamps are disjoint (Format 1 entries are 10:23-19:56, Format 2 entries start at 12:21:43 but none share exact timestamps with Format 1). The dedup mechanism works correctly but finds zero matches.

3. **Viewer null-safety fix**: Applied the `or {}` pattern rather than changing the conversion to use empty dicts, since `null` is the correct semantic representation for "field not collected."

## Known Stubs

None. All fields are either populated from source data or explicitly set to `null` for uncollected fields. The conversion script is complete and the viewer displays the full timeline.

## Self-Check: PASSED

- FOUND: convert_old_logs.py (worktree)
- FOUND: generate_log_viewer.py (worktree)
- FOUND: commit e4475f2 (Task 1)
- FOUND: commit 0d2f305 (Task 2)
- FOUND: 38 weekly JSONL files in main repo logs/
- FOUND: 20 archived .log files in main repo logs/archive/
- FOUND: 0 remaining .log files in main repo logs/
