# Quick Task 260327-ht2: Convert all old spell-check logs to new JSONL format - Context

**Gathered:** 2026-03-27
**Status:** Ready for planning

<domain>
## Task Boundary

Convert all old spell-check log files (~10,600 entries across 3 formats spanning Jul 2025 - Mar 2026) to the new structured JSONL format so the log viewer can display the full history.

</domain>

<decisions>
## Implementation Decisions

### Missing field representation
- Use `null` for fields that weren't collected in older formats (active_app, active_exe, raw_request, etc.)
- This distinguishes "not collected" from "was empty string"
- Viewer already uses `.get()` with defaults, so nulls are safe

### Output file strategy
- Replicate the live logger's weekly file naming: `spellcheck-YYYY-MM-DD-to-YYYY-MM-DD.jsonl`
- Same 5 MiB max per file with suffix rollover (-2, -3, etc.)
- CRITICAL: Never split a single log entry across files — if appending an entry would exceed 5 MiB, write it to the next suffixed file instead (soft cap, not hard cut)
- Each converted entry goes into the correct week file based on its timestamp
- Seamless integration with existing viewer — no special handling needed

### Old file handling
- After successful conversion, move all old `.log` files to `logs/archive/`
- Keeps `logs/` clean while preserving originals

### Claude's Discretion
- Deduplication strategy for Oct 14 overlap (prefer detailed log entries)
- `_converted` metadata field structure
- Internal parsing approach (regex vs section-based)

</decisions>

<specifics>
## Specific Ideas

- Conversion script at project root: `convert_old_logs.py`
- Must handle two API response formats: Chat Completions (`chat.completion`) and Responses API (`response`)
- Must handle 4 sub-eras of detailed logs with progressively more sections
- Must handle multi-line error entries in the single-line log format
- The `_converted` metadata object on each entry tracks source file, format era, and conversion timestamp

</specifics>

<canonical_refs>
## Canonical References

- Live logger rotation logic: `Universal Spell Checker.ahk` lines 334-379 (GetWeekStartStamp, BuildWeeklyLogPath, ResolveLogPathForAppend)
- Log viewer: `generate_log_viewer.py` (reads *.jsonl, uses .get() for all fields)
- Plan file: `.claude/plans/adaptive-spinning-dijkstra.md` (detailed format analysis)

</canonical_refs>
