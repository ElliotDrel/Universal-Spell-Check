# Quick Task 260327-ht2: Convert Old Logs to JSONL - Research

**Researched:** 2026-03-27
**Domain:** Python log parsing and format conversion
**Confidence:** HIGH

## Summary

This task requires a Python script (`convert_old_logs.py`) to parse ~10,600 legacy spell-check log entries across two major format families (pipe-delimited and multi-section text) and emit them as structured JSONL matching the live logger's schema. The research below documents the exact byte-level structure of each format era, the two API response schemas that must be handled for token extraction, the deduplication strategy for the Oct 14 overlap window, and the key edge cases (multi-line errors, encoding quirks, escaped JSON).

**Primary recommendation:** Use a state-machine parser for Format 2 (detailed logs) with section-aware accumulation. Use regex splitting for Format 1 (pipe-delimited). Reuse `generate_log_viewer.py`'s week-calculation and file-routing logic directly.

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- Use `null` for fields not collected in older formats (distinguishes "not collected" from "was empty string")
- Output weekly files: `spellcheck-YYYY-MM-DD-to-YYYY-MM-DD.jsonl` with 5 MiB soft cap and suffix rollover (-2, -3, etc.)
- Never split a single log entry across files
- After successful conversion, move old `.log` files to `logs/archive/`

### Claude's Discretion
- Deduplication strategy for Oct 14 overlap (prefer detailed log entries)
- `_converted` metadata field structure
- Internal parsing approach (regex vs section-based)

### Deferred Ideas (OUT OF SCOPE)
None specified.
</user_constraints>

## Format Analysis

### Format 1: Pipe-delimited (`logs/OLD - spellcheck.log`)

**File stats:** 2,427 lines, ~3.7 MB, encoding: Non-ISO extended-ASCII with CRLF line endings, dates: 2025-07-12 to 2025-10-14

**Sub-era 1a (lines 1-20, Jul 12 early entries): No Raw AI field**
```
YYYY-MM-DD HH:MM:SS | {duration}ms | {STATUS_OR_ERROR} | Input: {text} | Output: {text}
```
- Only 20 lines lack `| Raw AI:` field (lines 1-20)
- Error entries in this sub-era: `2025-07-12 16:30:48 | -199249093ms | ERROR: Could not parse API response | Input: ... | Output: `

**Sub-era 1b (lines 21-2427, Jul 12 onward): Has Raw AI field**
```
YYYY-MM-DD HH:MM:SS | {duration}ms | {STATUS_OR_ERROR} | Input: {text} | Raw AI: {escaped_json} | Output: {text} | Pasted: {text}
```
- Raw AI JSON uses literal `\\n` (double-backslash n) for newlines, not actual newlines
- All Raw AI responses are Chat Completions API (`"object": "chat.completion"`)

**Multi-line error pattern (CRITICAL):**
Error entries for timeout exceptions span 3-4 physical lines:
```
2025-07-13 23:41:54 | -311515265ms | ERROR: Exception: (0x80072EE2)
The operation timed out

Source:	WinHttp.WinHttpRequest | Input: {text} | Raw AI:  | Output:  | Pasted:
```
- Line 1: timestamp + error start (ends with space, no pipe-delimited fields)
- Line 2: `The operation timed out`
- Line 3: empty line
- Line 4: `Source:\tWinHttp.WinHttpRequest | Input: ... | Raw AI:  | Output:  | Pasted: `
- There are ~18 such multi-line error entries (identified by `grep -c "^The operation timed out"`)

**Parsing strategy for Format 1:**
1. Read all lines
2. Join multi-line error entries: if a line does NOT start with `YYYY-` pattern, append it to the previous entry
3. Split each logical entry by ` | ` delimiter
4. Detect sub-era by presence of `Raw AI:` segment
5. Parse duration: strip `ms` suffix, handle negative values (AHK timer bug -- store as-is)
6. Parse Raw AI JSON: replace `\\n` with `\n`, then `json.loads()`

**Token extraction from Chat Completions API response:**
```python
tokens = {
    "input": usage.get("prompt_tokens"),
    "output": usage.get("completion_tokens"),
    "total": usage.get("total_tokens"),
    "cached": usage.get("prompt_tokens_details", {}).get("cached_tokens", 0),
    "reasoning": usage.get("completion_tokens_details", {}).get("reasoning_tokens", 0)
}
```

### Format 2: Multi-section detailed logs (19 files, ~8,200 entries)

**File list (chronological):**
| File | Dates | Entries | Era |
|------|-------|---------|-----|
| spellcheck-detailed-2025-11-30-125348.log | Oct 14 - Nov 30 | 2,254 | 2a |
| spellcheck-detailed-2025-12-06-162019.log | Nov 30 - Dec 6 | 336 | 2a/2b transition |
| spellcheck-detailed-2025-12-14-195449.log | Dec 6 - Dec 14 | 339 | 2b |
| spellcheck-detailed-2025-12-28-192121.log | Dec 14 - Dec 28 | 344 | 2b |
| spellcheck-detailed-2026-01-08-214001.log | Dec 28 - Jan 8 | 365 | 2b |
| spellcheck-detailed-2026-01-17-123854.log | Jan 8 - Jan 17 | 363 | 2b |
| spellcheck-detailed-2026-01-24-121036.log | Jan 24 - Jan 24 | 352 | 2b |
| spellcheck-detailed-2026-01-30-103632.log | Jan 24 - Jan 30 | 342 | 2b |
| spellcheck-detailed-2026-02-07-150906.log | Jan 30 - Feb 7 | 355 | 2b |
| spellcheck-detailed-2026-02-17-223831.log | Feb 7 - Feb 17 | 342 | 2b |
| spellcheck-detailed-2026-02-22-212757.log | Feb 17 - Feb 22 | 329 | 2b |
| spellcheck-detailed-2026-02-25-204036.log | Feb 22 - Feb 25 | 336 | 2b |
| spellcheck-detailed-2026-03-01-201306.log | Feb 25 - Mar 1 | 327 | 2b |
| spellcheck-detailed-2026-03-04-000800.log | Mar 1 - Mar 4 | 312 | 2c |
| spellcheck-detailed-2026-03-06-122844.log | Mar 4 - Mar 6 | 301 | 2d |
| spellcheck-detailed-2026-03-11-110141.log | Mar 6 - Mar 11 | 303 | 2d |
| spellcheck-detailed-2026-03-16-190613.log | Mar 11 - Mar 16 | 307 | 2d |
| spellcheck-detailed-2026-03-23-100310.log | Mar 16 - Mar 23 | 306 | 2d |
| spellcheck-detailed.log (current) | Mar 23 - Mar 27 | 274 | 2d |

**Total:** ~8,237 entries

**Entry delimiter:** `================================================================================` (80 `=` chars)

**Entry header patterns:**
- Success: `RUN: YYYY-MM-DD HH:MM:SS`
- Error: `[ERROR] RUN: YYYY-MM-DD HH:MM:SS`

**Section markers (all indented with 2 spaces for content):**

| Section | Key line | Present in era | Notes |
|---------|----------|---------------|-------|
| Status | `Status: {value}` | All | Always present |
| Duration | `Duration: {N}ms` | All | Always present |
| Timing Breakdown | `Timing Breakdown:` | 2b, 2c, 2d | Sub-items: `  {name}: {N}ms` |
| Input Text | `Input Text:` | All | Next line(s) indented with `  ` |
| AI Output (before post-processing) | `AI Output (before post-processing):` | 2c, 2d | Only when post-processing section exists |
| Output Text | `Output Text:` | 2a, 2b | Used when AI Output section absent |
| Post-processing | `Post-processing: {detail}` | 2c, 2d | Single line like `no replacements matched` or `1 replacement: X -> Y` |
| Prompt-leak safeguard | `Prompt-leak safeguard: {detail}` | 2d | Single line like `no leak pattern matched` |
| Pasted Text | `Pasted Text:` | All | Next line(s) indented with `  ` |
| API Response | `API Response:` | All | Multi-line indented JSON block |
| Events | `Events:` | 2a, 2b, 2c, 2d | Indented event lines starting with `  ` |

**Section ordering within an entry (varies by era):**
- Era 2a: Status, Duration, Input Text, Output Text, API Response, Events, Pasted Text
- Era 2b: Status, Duration, Timing Breakdown, Input Text, Output Text, API Response, Events, Pasted Text
- Era 2c: Status, Duration, Timing Breakdown, Input Text, AI Output, Post-processing, Pasted Text, API Response, Events
- Era 2d: Status, Duration, Timing Breakdown, Input Text, AI Output, Post-processing, Prompt-leak safeguard, Pasted Text, API Response, Events

**Key observation about section order change:** In eras 2c/2d, API Response and Events move to the end (after Pasted Text), while in 2a/2b they appear before Pasted Text. The parser must handle both orderings.

**Token extraction from Responses API response:**
```python
tokens = {
    "input": usage.get("input_tokens"),
    "output": usage.get("output_tokens"),
    "total": usage.get("total_tokens"),
    "cached": usage.get("input_tokens_details", {}).get("cached_tokens", 0),
    "reasoning": usage.get("output_tokens_details", {}).get("reasoning_tokens", 0)
}
```

**Detecting API type:** Check `"object"` field in parsed JSON:
- `"chat.completion"` -> Chat Completions API token fields (`prompt_tokens`, `completion_tokens`)
- `"response"` -> Responses API token fields (`input_tokens`, `output_tokens`)
- Error responses have `"error"` key at top level -> no tokens

### Timing Breakdown Parsing

The `Timing Breakdown:` section maps to JSONL `timings` object. Field name mapping:

| Detailed log label | JSONL field | Present in |
|-------------------|-------------|------------|
| `Clipboard capture` | `clipboard_ms` | 2b+ |
| `Payload preparation` | `payload_ms` | 2b+ |
| `Request setup` | `request_ms` | 2b+ |
| `API round-trip` | `api_ms` | 2b+ |
| `Response parsing` | `parse_ms` | 2b+ |
| `Post-processing` or `Post-processing replacements` | `replacements_ms` | 2c+ (in timing section) |
| `Prompt-leak safeguard` | `prompt_guard_ms` | 2d (in timing section) |
| `Text pasting` | `paste_ms` | 2b+ |

## Deduplication: Oct 14 Overlap

The old pipe-delimited log ends on Oct 14, 2025. The earliest detailed log begins on Oct 14, 2025.

- Old log: 24 entries dated 2025-10-14
- Detailed log: 94 entries dated 2025-10-14

**Recommendation:** Prefer detailed log entries for Oct 14. Deduplicate by matching `(timestamp, input_text)` pairs. For each Format 1 entry on Oct 14, check if a matching Format 2 entry exists with the same timestamp and input text. If yes, skip the Format 1 entry. Process detailed logs first, then Format 1 entries for Oct 14 only if they lack a match.

**Implementation:** Build a set of `(timestamp, first_100_chars_of_input)` from detailed Oct 14 entries. Skip Format 1 entries that match.

## `_converted` Metadata Field

**Recommended structure:**
```json
{
    "_converted": {
        "source_file": "OLD - spellcheck.log",
        "source_format": "pipe-delimited-1b",
        "converted_at": "2026-03-27T14:00:00",
        "converter_version": "1.0"
    }
}
```

`source_format` values: `pipe-delimited-1a`, `pipe-delimited-1b`, `detailed-2a`, `detailed-2b`, `detailed-2c`, `detailed-2d`

## Parsing Approach

**Recommendation: State machine for Format 2, regex split for Format 1.**

### Format 1 Parser (pipe-delimited)
1. Read entire file with `encoding="utf-8", errors="replace"` (handles the non-ISO chars)
2. Merge continuation lines (lines not starting with `YYYY-MM-DD`) into previous logical line
3. For each logical line, split by ` | ` and parse positionally
4. Detect sub-era by presence of `Raw AI:` in split fields

### Format 2 Parser (detailed multi-section)
State machine approach:
1. Split file into entries using the `===...===` delimiter (80 equals signs)
2. For each entry block, identify sections by their header labels
3. Parse each section's content (indented lines below the header)
4. For API Response section: accumulate all indented JSON lines, strip leading `  `, join and `json.loads()`

**Why state machine over regex:** The multi-line JSON blocks, variable section ordering across eras, and indented content blocks make regex brittle. A state machine that accumulates lines under the current section header is cleaner and handles all eras uniformly.

## Edge Cases and Encoding

| Edge Case | Where | Handling |
|-----------|-------|----------|
| Multi-line timeout errors | Format 1, ~18 entries | Join lines not starting with timestamp pattern |
| Escaped JSON `\\n` literals | Format 1 Raw AI field | Replace `\\\\n` with `\\n` before `json.loads()` |
| UTF-8 replacement chars (mojibake) | Format 1, early entries | `errors="replace"` -- store as-is, data was already corrupted at capture time |
| CRLF line endings | Format 1 | `.strip()` handles this |
| Negative duration values | Format 1, error entries | Store as-is (AHK timer overflow bug) |
| Empty Output/Raw AI on errors | Both formats | Map to `""` or `null` as appropriate |
| Error responses (no usage/tokens) | Both formats, API error entries | All token fields -> `null` |
| `"object": "chat.completion"` vs `"response"` | Both formats | Different token field names (see above) |
| API Response contains error JSON (not usage) | Both formats | Check for `"error"` key before token extraction |
| Indented content with leading `  ` | Format 2 | Strip exactly 2 leading spaces from content lines |
| Missing sections in older eras | Format 2 2a | Timing, Post-processing, Prompt-leak all `null` |

## ISO Week Calculation (Python)

The AHK `GetWeekStartStamp` uses Monday-start weeks. The Python equivalent from `generate_log_viewer.py` is already correct:

```python
from datetime import datetime, timedelta

def get_week_start_stamp(dt):
    """Monday-based week start."""
    return (dt - timedelta(days=dt.weekday())).strftime("%Y-%m-%d")

def get_week_end_stamp(dt):
    """Sunday-based week end."""
    return (dt + timedelta(days=(6 - dt.weekday()))).strftime("%Y-%m-%d")
```

This matches `datetime.weekday()` where Monday=0, Sunday=6. Verified against the AHK code which uses `WDay` (Sunday=1, Saturday=7) and computes `daysFromMonday = (wday == 1) ? 6 : (wday - 2)`.

## Code to Reuse from `generate_log_viewer.py`

The following functions can be imported or copied directly:
- `get_week_start_stamp(dt)` -- week calculation
- `get_week_end_stamp(dt)` -- week calculation
- `build_weekly_log_path(week_start_stamp, week_end_stamp, suffix_index)` -- file naming
- `resolve_weekly_log_path(week_start_stamp, week_end_stamp, pending_bytes)` -- 5 MiB routing

These are currently defined at module level with `LOGS_DIR` and `MAX_WEEKLY_LOG_SIZE` constants. Either import from `generate_log_viewer.py` or duplicate (the functions are small -- 3-5 lines each).

**Recommendation:** Duplicate rather than import, to keep `convert_old_logs.py` self-contained and avoid coupling to the viewer's module-level constants.

## Target JSONL Schema (Complete Field List)

Based on analysis of existing `spellcheck-2026-03-23-to-2026-03-29.jsonl`:

```python
{
    "timestamp": "YYYY-MM-DD HH:MM:SS",  # Always available
    "status": "SUCCESS" | "ERROR: ...",    # Always available
    "error": "",                           # Error message or ""
    "duration_ms": int,                    # Always available (may be negative)
    "model": str | null,                   # Extractable from Raw AI / API Response
    "model_version": str | null,           # Full model string from API response
    "active_app": null,                    # Not collected in old formats
    "active_exe": null,                    # Not collected in old formats
    "paste_method": "clipboard" | null,    # Format 2 events may say "SendText"
    "text_changed": bool,                  # Compare input vs output
    "input_text": str,                     # Always available
    "input_chars": int,                    # len(input_text)
    "output_text": str,                    # Always available (may be "")
    "output_chars": int,                   # len(output_text)
    "raw_ai_output": str | null,           # Format 2 "AI Output" section, or output_text
    "tokens": {                            # null if no API response
        "input": int,
        "output": int,
        "total": int,
        "cached": int,
        "reasoning": int
    } | null,
    "timings": {                           # null for Format 1 and era 2a
        "clipboard_ms": int,
        "payload_ms": int,
        "request_ms": int,
        "api_ms": int,
        "parse_ms": int,
        "replacements_ms": int,
        "prompt_guard_ms": int,
        "paste_ms": int
    } | null,
    "replacements": {                      # null for Format 1 and eras 2a/2b
        "count": int,
        "applied": [],
        "urls_protected": int
    } | null,
    "prompt_leak": {                       # null for all except era 2d
        "triggered": bool,
        "occurrences": int,
        "text_input_removed": bool,
        "removed_chars": int,
        "before_length": int,
        "after_length": int
    } | null,
    "events": [...] | null,               # Format 2 only
    "raw_request": null,                   # Not collected in old formats
    "raw_response": str | null,            # The full API response JSON string
    "_converted": {
        "source_file": str,
        "source_format": str,
        "converted_at": str,
        "converter_version": str
    }
}
```

## Processing Order

1. Parse all Format 2 (detailed) log files first, sorted by filename timestamp
2. Build dedup set from Oct 14 entries: `{(timestamp, input_text[:100])}`
3. Parse Format 1 (pipe-delimited), skipping Oct 14 entries that exist in dedup set
4. Sort all entries by timestamp
5. Write to weekly JSONL files using `resolve_weekly_log_path()`
6. Report: total entries converted, per-format counts, dedup count, entries per week file
7. Move old `.log` files to `logs/archive/`

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | Python unittest (stdlib) |
| Config file | None needed (use `python -m pytest` or `python -m unittest`) |
| Quick run command | `python -m pytest convert_old_logs.py -x -v` (if inline tests) or `python convert_old_logs.py --dry-run` |
| Full suite command | `python convert_old_logs.py` (the conversion itself is the test -- verify output count) |

### Phase Requirements to Test Map
| Req ID | Behavior | Test Type | Automated Command |
|--------|----------|-----------|-------------------|
| CONVERT-01 | Format 1a entries parsed (no Raw AI) | smoke | Verify count: first 20 lines produce 17 entries (3 are continuation lines from 1 multi-line error) |
| CONVERT-02 | Format 1b entries parsed (with Raw AI + tokens) | smoke | Verify token extraction from chat.completion JSON |
| CONVERT-03 | Multi-line error entries joined correctly | smoke | Verify ~18 timeout entries have full error message |
| CONVERT-04 | Format 2 all eras parsed | smoke | Verify total count ~8,237 |
| CONVERT-05 | Oct 14 dedup works | smoke | Old log has 24 Oct 14 entries, detailed has 94; final should have ~94-100 |
| CONVERT-06 | Weekly files generated correctly | smoke | Check file names match `spellcheck-YYYY-MM-DD-to-YYYY-MM-DD.jsonl` |
| CONVERT-07 | 5 MiB soft cap respected | smoke | No file exceeds 5 MiB + one entry |
| CONVERT-08 | Old files moved to archive | smoke | `logs/archive/` contains all `.log` files |
| CONVERT-09 | Viewer reads converted files | manual | Run `python generate_log_viewer.py --no-open` and verify viewer.html shows old entries |

### Sampling Rate
- **Post-conversion:** Run `python generate_log_viewer.py --no-open` to validate all converted JSONL files parse cleanly
- **Spot check:** Verify 3-5 entries from each era match source data

## Common Pitfalls

### Pitfall 1: Pipe character in input text
**What goes wrong:** Input text may contain ` | ` which breaks naive splitting
**How to avoid:** For Format 1, split from the LEFT using known field positions. The first 3 fields (timestamp, duration, status) are always present and parseable. Then find `Input:` and `Raw AI:` / `Output:` markers by searching rightward. Do NOT use `split(" | ")` blindly.

### Pitfall 2: Escaped JSON newlines in Format 1
**What goes wrong:** The Raw AI field contains `\\n` as literal two-char sequences, not actual newlines. A naive read would not produce valid JSON.
**How to avoid:** Replace `\\n` with actual `\n` before `json.loads()`. Be careful: the log also contains `\\\\n` in some cases (escaped backslash + n in the original text). The correct approach is `raw_ai_text.replace("\\n", "\n")`.

### Pitfall 3: Section ordering varies across Format 2 eras
**What goes wrong:** Hard-coding section order breaks when processing older files.
**How to avoid:** Parse sections by their header labels, not by position. The state machine should recognize any section header and accumulate content until the next header or entry delimiter.

### Pitfall 4: Empty sections in error entries
**What goes wrong:** Error entries may have empty `Output Text:`, `API Response:`, or `Pasted Text:` sections (just the header with blank indented content).
**How to avoid:** Treat missing/empty sections as `null` or `""` as appropriate for the JSONL field.

### Pitfall 5: File encoding
**What goes wrong:** The old log contains non-UTF-8 bytes (replacement chars from Windows clipboard).
**How to avoid:** Open with `encoding="utf-8", errors="replace"`. The data was already corrupted at capture time; preserve what's readable.

## Project Constraints (from CLAUDE.md)

- Script should be placed at project root: `convert_old_logs.py`
- Python is available: 3.13.6
- No external dependencies needed (stdlib only: `json`, `re`, `os`, `datetime`, `pathlib`, `shutil`)
- Speed is always valued, but this is a one-time batch script -- correctness over performance
- CLAUDE.md principle "Debug first": include verbose logging/reporting of conversion stats
- CLAUDE.md principle "Simplest solution first": single-pass parsing, no over-engineering

## Sources

### Primary (HIGH confidence)
- Direct examination of all 20 log files in `logs/` directory
- `generate_log_viewer.py` source code (week calculation, file routing logic)
- `Universal Spell Checker.ahk` lines 334-379 (GetWeekStartStamp, BuildWeeklyLogPath, ResolveLogPathForAppend)
- Existing JSONL entries in `spellcheck-2026-03-23-to-2026-03-29.jsonl` (target schema)

## Metadata

**Confidence breakdown:**
- Format 1 parsing: HIGH - examined actual file, both sub-eras, all edge cases visible
- Format 2 parsing: HIGH - examined representative entries from each era
- Token extraction: HIGH - verified both API response schemas from actual data
- Week calculation: HIGH - verified Python matches AHK logic and existing viewer code
- Dedup strategy: MEDIUM - entry counts verified but exact overlap matching needs runtime validation

**Research date:** 2026-03-27
**Valid until:** Indefinite (log formats are historical, will not change)
