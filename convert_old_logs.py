#!/usr/bin/env python3
"""Convert legacy spell-check log files to structured JSONL format.

Parses all old log files (~10,600 entries across Format 1 pipe-delimited and
Format 2 multi-section detailed logs) into the same JSONL schema used by the
live logger, distributes them into weekly files, deduplicates the Oct 14 overlap,
and archives the originals.

Usage:
    python convert_old_logs.py            # full conversion
    python convert_old_logs.py --dry-run  # parse and report only, no file writes
"""

import json
import os
import re
import shutil
import sys
from datetime import datetime, timedelta
from pathlib import Path

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------

SCRIPT_DIR = Path(__file__).resolve().parent
LOGS_DIR = SCRIPT_DIR / "logs"
ARCHIVE_DIR = LOGS_DIR / "archive"
MAX_WEEKLY_LOG_SIZE = 5 * 1024 * 1024  # 5 MiB soft cap
CONVERTER_VERSION = "1.0"

# Files that must NOT be touched (live JSONL, viewer, bak)
LIVE_JSONL_PATTERN = re.compile(r"^spellcheck-\d{4}-\d{2}-\d{2}-to-\d{4}-\d{2}-\d{2}(-\d+)?\.jsonl$")

# Timestamp pattern for Format 1 line detection
F1_LINE_START = re.compile(r"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}")

# Format 2 entry delimiter
F2_DELIMITER = "=" * 80

# Format 2 header patterns
F2_RUN_PATTERN = re.compile(r"^(?:\[ERROR\] )?RUN: (\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})")

# Format 2 section labels (order matters for matching - longer first)
F2_SECTION_LABELS = [
    "AI Output (before post-processing):",
    "Post-processing replacements:",
    "Prompt-leak safeguard:",
    "Post-processing:",
    "Timing Breakdown:",
    "Input Text:",
    "Output Text:",
    "Pasted Text:",
    "API Response:",
    "Events:",
    "Status:",
    "Duration:",
    "Source:",
]

# Timing label -> JSONL field mapping
TIMING_LABEL_MAP = {
    "Clipboard capture": "clipboard_ms",
    "Payload preparation": "payload_ms",
    "Request setup": "request_ms",
    "API round-trip": "api_ms",
    "Response parsing": "parse_ms",
    "Post-processing": "replacements_ms",
    "Post-processing replacements": "replacements_ms",
    "Prompt-leak safeguard": "prompt_guard_ms",
    "Text pasting": "paste_ms",
}

# ---------------------------------------------------------------------------
# Week routing functions (duplicated from generate_log_viewer.py)
# ---------------------------------------------------------------------------

def get_week_start_stamp(dt):
    """Return the Monday-based week start stamp for a datetime."""
    return (dt - timedelta(days=dt.weekday())).strftime("%Y-%m-%d")


def get_week_end_stamp(dt):
    """Return the Sunday-based week end stamp for a datetime."""
    return (dt + timedelta(days=(6 - dt.weekday()))).strftime("%Y-%m-%d")


def build_weekly_log_path(week_start_stamp, week_end_stamp, suffix_index=0):
    """Return the canonical weekly log path for a week and optional overflow suffix."""
    suffix = f"-{suffix_index + 1}" if suffix_index > 0 else ""
    return LOGS_DIR / f"spellcheck-{week_start_stamp}-to-{week_end_stamp}{suffix}.jsonl"


def resolve_weekly_log_path(week_start_stamp, week_end_stamp, pending_bytes):
    """Pick the weekly file that can accept the next line without crossing the size cap."""
    suffix_index = 0
    while True:
        path = build_weekly_log_path(week_start_stamp, week_end_stamp, suffix_index)
        if not path.exists():
            return path
        if path.stat().st_size + pending_bytes <= MAX_WEEKLY_LOG_SIZE:
            return path
        suffix_index += 1


# ---------------------------------------------------------------------------
# Token extraction helpers
# ---------------------------------------------------------------------------

def extract_tokens_chat_completion(usage):
    """Extract tokens from Chat Completions API usage block."""
    if not usage:
        return None
    return {
        "input": usage.get("prompt_tokens"),
        "output": usage.get("completion_tokens"),
        "total": usage.get("total_tokens"),
        "cached": (usage.get("prompt_tokens_details") or {}).get("cached_tokens", 0),
        "reasoning": (usage.get("completion_tokens_details") or {}).get("reasoning_tokens", 0),
    }


def extract_tokens_response(usage):
    """Extract tokens from Responses API usage block."""
    if not usage:
        return None
    return {
        "input": usage.get("input_tokens"),
        "output": usage.get("output_tokens"),
        "total": usage.get("total_tokens"),
        "cached": (usage.get("input_tokens_details") or {}).get("cached_tokens", 0),
        "reasoning": (usage.get("output_tokens_details") or {}).get("reasoning_tokens", 0),
    }


def extract_tokens_from_response(api_json):
    """Detect API type and extract tokens."""
    if not api_json or not isinstance(api_json, dict):
        return None, None, None
    # Check for error response
    if "error" in api_json and not api_json.get("usage"):
        return None, None, None
    obj_type = api_json.get("object", "")
    usage = api_json.get("usage")
    model_version = api_json.get("model")
    # Derive short model name by stripping date suffix
    model = None
    if model_version:
        # "gpt-4.1-2025-04-14" -> "gpt-4.1"
        m = re.match(r"^(.*?)-\d{4}-\d{2}-\d{2}$", model_version)
        model = m.group(1) if m else model_version
    if obj_type == "chat.completion":
        return extract_tokens_chat_completion(usage), model, model_version
    elif obj_type == "response":
        return extract_tokens_response(usage), model, model_version
    else:
        # Try both - response API first (newer)
        if usage:
            if "input_tokens" in usage:
                return extract_tokens_response(usage), model, model_version
            elif "prompt_tokens" in usage:
                return extract_tokens_chat_completion(usage), model, model_version
        return None, model, model_version


# ---------------------------------------------------------------------------
# Format 1 Parser (pipe-delimited)
# ---------------------------------------------------------------------------

def parse_format1(filepath, dedup_set, stats):
    """Parse the old pipe-delimited log file.

    Returns list of JSONL-ready dicts.
    """
    entries = []
    filename = os.path.basename(filepath)

    # Read as binary and decode manually to avoid Python's universal newline
    # translation.  The file contains standalone \r (0x0D) inside field values
    # (AHK logged literal \\n from user text as \r + literal \\n).  Python's
    # default text mode would split on bare \r, breaking entries.
    with open(filepath, "rb") as f:
        raw_bytes = f.read()
    raw_text = raw_bytes.decode("utf-8", errors="replace")
    # Normalise: strip \r so we split only on \n
    raw_text = raw_text.replace("\r\n", "\n").replace("\r", "")
    raw_lines = raw_text.split("\n")

    # Join continuation lines (multi-line errors)
    logical_lines = []
    for line in raw_lines:
        line = line.rstrip()
        if not line and logical_lines:
            # Empty line - continuation of previous entry
            logical_lines[-1] += "\n"
            continue
        if F1_LINE_START.match(line):
            logical_lines.append(line)
        elif logical_lines:
            logical_lines[-1] += "\n" + line
        else:
            logical_lines.append(line)

    converted_at = datetime.now().strftime("%Y-%m-%dT%H:%M:%S")
    entry_num = 0

    for logical_line in logical_lines:
        if not logical_line.strip():
            continue
        if not F1_LINE_START.match(logical_line):
            continue

        entry_num += 1
        try:
            entry = _parse_format1_line(logical_line, filename, converted_at, entry_num)
            if entry is None:
                continue

            # Dedup check for Oct 14
            ts = entry["timestamp"]
            if ts.startswith("2025-10-14"):
                dedup_key = (ts, (entry.get("input_text") or "")[:100])
                if dedup_key in dedup_set:
                    stats["f1_dedup_skipped"] += 1
                    continue

            entries.append(entry)

            # Track sub-era
            fmt = entry["_converted"]["source_format"]
            if fmt == "pipe-delimited-1a":
                stats["f1a_count"] += 1
            else:
                stats["f1b_count"] += 1

        except Exception as e:
            stats["errors"].append(f"Format1 entry #{entry_num} in {filename}: {e}")
            stats["error_count"] += 1
            if stats["error_count"] > 50:
                raise RuntimeError(f"Too many errors ({stats['error_count']}), aborting")

    return entries


def _parse_format1_line(line, filename, converted_at, entry_num):
    """Parse a single logical line from Format 1."""
    # Extract timestamp (first 19 chars)
    timestamp = line[:19]

    # Find first " | " after timestamp
    rest = line[19:]
    if not rest.startswith(" | "):
        return None
    rest = rest[3:]  # skip " | "

    # Find duration: everything until next " | "
    pipe_idx = rest.find(" | ")
    if pipe_idx == -1:
        return None
    duration_str = rest[:pipe_idx]
    rest = rest[pipe_idx + 3:]

    # Parse duration
    duration_ms = None
    if duration_str.endswith("ms"):
        try:
            duration_ms = int(duration_str[:-2])
        except ValueError:
            duration_ms = None

    # Find status: everything until " | Input: " marker
    input_marker = " | Input: "
    input_idx = rest.find(input_marker)
    if input_idx == -1:
        # Could be an entry with no input marker - skip
        return None
    status_str = rest[:input_idx]
    rest = rest[input_idx + len(input_marker):]

    # Determine error status
    is_error = status_str.startswith("ERROR")
    error_msg = ""
    if is_error:
        if status_str.startswith("ERROR: "):
            error_msg = status_str[7:]
        else:
            error_msg = status_str
        status = status_str
    else:
        status = "SUCCESS"

    # Detect sub-era: check for " | Raw AI: " marker
    raw_ai_marker = " | Raw AI: "
    raw_ai_idx = rest.find(raw_ai_marker)

    if raw_ai_idx == -1:
        # Sub-era 1a: Input | Output only
        # Find " | Output: " from the rest
        output_marker = " | Output: "
        output_idx = rest.find(output_marker)
        if output_idx == -1:
            input_text = rest
            output_text = ""
        else:
            input_text = rest[:output_idx]
            output_text = rest[output_idx + len(output_marker):]

        source_format = "pipe-delimited-1a"
        raw_ai_output = None
        raw_response = None
        tokens = None
        model = None
        model_version = None
    else:
        # Sub-era 1b: Input | Raw AI | Output | Pasted
        input_text = rest[:raw_ai_idx]
        rest = rest[raw_ai_idx + len(raw_ai_marker):]

        # Find " | Output: " after Raw AI
        output_marker = " | Output: "
        output_idx = rest.find(output_marker)
        if output_idx == -1:
            raw_ai_str = rest
            output_text = ""
            pasted_text = ""
        else:
            raw_ai_str = rest[:output_idx]
            rest = rest[output_idx + len(output_marker):]

            # Find " | Pasted: " after Output
            pasted_marker = " | Pasted: "
            pasted_idx = rest.find(pasted_marker)
            if pasted_idx == -1:
                output_text = rest
            else:
                output_text = rest[:pasted_idx]

        source_format = "pipe-delimited-1b"

        # Parse Raw AI JSON
        raw_ai_str = raw_ai_str.strip()
        raw_response = None
        tokens = None
        model = None
        model_version = None
        raw_ai_output = output_text  # In this era, output_text IS the AI output

        if raw_ai_str:
            try:
                # The AHK logger wrote literal \n as two chars (0x5C, 0x6E) into the
                # file.  After our \r-stripping read, the Python string contains two
                # backslash characters followed by 'n' for each original escape.
                # Replace that 3-char sequence (\\, \\, n) with a real newline so
                # json.loads() can parse the JSON structure.
                json_str = raw_ai_str.replace("\\\\n", "\n")
                raw_response = json_str
                api_json = json.loads(json_str)
                tokens, model, model_version = extract_tokens_from_response(api_json)
            except json.JSONDecodeError:
                raw_response = raw_ai_str
                tokens = None

    # Build JSONL entry
    text_changed = (input_text != output_text) if output_text else False

    return {
        "timestamp": timestamp,
        "status": status,
        "error": error_msg,
        "duration_ms": duration_ms,
        "model": model,
        "model_version": model_version,
        "active_app": None,
        "active_exe": None,
        "paste_method": None,
        "text_changed": text_changed,
        "input_text": input_text,
        "input_chars": len(input_text),
        "output_text": output_text,
        "output_chars": len(output_text),
        "raw_ai_output": raw_ai_output,
        "tokens": tokens,
        "timings": None,
        "replacements": None,
        "prompt_leak": None,
        "events": None,
        "raw_request": None,
        "raw_response": raw_response,
        "_converted": {
            "source_file": filename,
            "source_format": source_format,
            "converted_at": converted_at,
            "converter_version": CONVERTER_VERSION,
        },
    }


# ---------------------------------------------------------------------------
# Format 2 Parser (detailed multi-section)
# ---------------------------------------------------------------------------

def parse_format2(filepath, stats):
    """Parse a detailed multi-section log file.

    Returns list of JSONL-ready dicts.
    """
    entries = []
    filename = os.path.basename(filepath)

    with open(filepath, encoding="utf-8", errors="replace") as f:
        content = f.read()

    # Split into blocks by the 80-= delimiter
    # An entry is: delim line, header line, delim line, then content until next delim
    blocks = _split_into_entry_blocks(content)
    converted_at = datetime.now().strftime("%Y-%m-%dT%H:%M:%S")

    for block_num, block in enumerate(blocks, 1):
        try:
            entry = _parse_format2_block(block, filename, converted_at)
            if entry is not None:
                entries.append(entry)
                fmt = entry["_converted"]["source_format"]
                stats[f"f2_{fmt.split('-')[1]}_count"] = stats.get(f"f2_{fmt.split('-')[1]}_count", 0) + 1
        except Exception as e:
            stats["errors"].append(f"Format2 block #{block_num} in {filename}: {e}")
            stats["error_count"] += 1
            if stats["error_count"] > 50:
                raise RuntimeError(f"Too many errors ({stats['error_count']}), aborting")

    return entries


def _split_into_entry_blocks(content):
    """Split file content into entry blocks separated by the 80-= delimiter.

    Entry structure in the files:
        ====...====          (delimiter)
        RUN: YYYY-MM-DD ...  (or [ERROR] RUN: ...)
        ====...====          (delimiter)
        content lines...
        ====...====          (trailing delimiter / separator)
        (empty line)

    We scan for the pattern: DELIM, HEADER, DELIM and collect all content
    lines until the next DELIM (which is either the trailing delimiter of
    this entry or the start of the next entry's header pair).
    """
    lines = content.split("\n")
    blocks = []

    i = 0
    n = len(lines)

    while i < n:
        line = lines[i].rstrip("\r")

        # Look for the entry header pattern: DELIM / RUN: / DELIM
        if line == F2_DELIMITER and (i + 2) < n:
            header_line = lines[i + 1].rstrip("\r")
            next_delim = lines[i + 2].rstrip("\r")

            if next_delim == F2_DELIMITER and F2_RUN_PATTERN.match(header_line):
                # Found an entry header - collect content until next delimiter
                content_lines = [header_line]
                j = i + 3
                while j < n:
                    cline = lines[j].rstrip("\r")
                    if cline == F2_DELIMITER:
                        break
                    content_lines.append(cline)
                    j += 1
                blocks.append(content_lines)
                i = j  # Move to the trailing delimiter (will be re-examined as potential start)
                continue

        i += 1

    return blocks


def _parse_format2_block(block_lines, filename, converted_at):
    """Parse a single entry block from Format 2."""
    if not block_lines:
        return None

    # First line should be the RUN: header
    header_line = block_lines[0].strip()
    m = F2_RUN_PATTERN.match(header_line)
    if not m:
        return None

    timestamp = m.group(1)

    # Parse sections using state machine
    sections = _extract_sections(block_lines[1:])

    # Extract Status
    status_raw = sections.get("Status", "").strip()
    is_error = status_raw.startswith("ERROR")
    error_msg = ""
    if is_error:
        if status_raw.startswith("ERROR: "):
            error_msg = status_raw[7:]
        elif status_raw.startswith("ERROR:"):
            error_msg = status_raw[6:].strip()
        else:
            error_msg = status_raw

    # Extract Duration
    duration_raw = sections.get("Duration", "").strip()
    duration_ms = None
    if duration_raw.endswith("ms"):
        try:
            duration_ms = int(duration_raw[:-2])
        except ValueError:
            pass

    # Extract Timing Breakdown
    timings = _parse_timings(sections.get("Timing Breakdown"))

    # Extract Input Text
    input_text = sections.get("Input Text", "")

    # Determine output fields based on era
    ai_output_raw = sections.get("AI Output (before post-processing)")
    output_text_raw = sections.get("Output Text")
    pasted_text = sections.get("Pasted Text", "")

    if ai_output_raw is not None:
        # Era 2c/2d: AI Output is the raw AI output, Pasted Text is the final output
        raw_ai_output = ai_output_raw
        output_text = pasted_text
    elif output_text_raw is not None:
        # Era 2a/2b: Output Text is both the AI output and the output
        raw_ai_output = output_text_raw
        output_text = output_text_raw
    else:
        raw_ai_output = ""
        output_text = ""

    # Extract API Response
    api_response_str = sections.get("API Response", "").strip()
    tokens = None
    model = None
    model_version = None
    raw_response = None

    if api_response_str:
        raw_response = api_response_str
        try:
            api_json = json.loads(api_response_str)
            tokens, model, model_version = extract_tokens_from_response(api_json)
        except json.JSONDecodeError:
            pass

    # Extract Events
    events = _parse_events(sections.get("Events"))

    # Extract Post-processing
    replacements = _parse_post_processing(sections.get("Post-processing"))
    if replacements is None:
        # Check alternate label
        replacements = _parse_post_processing(sections.get("Post-processing replacements"))

    # Extract Prompt-leak safeguard
    prompt_leak = _parse_prompt_leak(sections.get("Prompt-leak safeguard"))

    # Infer paste_method from events
    paste_method = None
    if events:
        events_str = " ".join(events).lower()
        if "sendtext" in events_str:
            paste_method = "sendtext"
        elif "clipboard paste" in events_str or "clipboard" in events_str:
            paste_method = "clipboard"

    # Determine source format (era)
    if prompt_leak is not None or "Prompt-leak safeguard" in sections:
        source_format = "detailed-2d"
    elif replacements is not None or "Post-processing" in sections or "Post-processing replacements" in sections:
        source_format = "detailed-2c"
    elif timings is not None:
        source_format = "detailed-2b"
    else:
        source_format = "detailed-2a"

    text_changed = (input_text != output_text)

    return {
        "timestamp": timestamp,
        "status": status_raw if status_raw else "SUCCESS",
        "error": error_msg,
        "duration_ms": duration_ms,
        "model": model,
        "model_version": model_version,
        "active_app": None,
        "active_exe": None,
        "paste_method": paste_method,
        "text_changed": text_changed,
        "input_text": input_text,
        "input_chars": len(input_text),
        "output_text": output_text,
        "output_chars": len(output_text),
        "raw_ai_output": raw_ai_output,
        "tokens": tokens,
        "timings": timings,
        "replacements": replacements,
        "prompt_leak": prompt_leak,
        "events": events,
        "raw_request": None,
        "raw_response": raw_response,
        "_converted": {
            "source_file": filename,
            "source_format": source_format,
            "converted_at": converted_at,
            "converter_version": CONVERTER_VERSION,
        },
    }


def _extract_sections(lines):
    """Extract sections from an entry block using a state machine.

    Returns dict of section_label -> content_string.
    Content lines have their 2-space indent stripped and are joined with newlines.
    """
    sections = {}
    current_section = None
    current_content = []

    for line in lines:
        line = line.rstrip("\r")

        # Check if this line is a section header
        matched_section = None
        for label in F2_SECTION_LABELS:
            if line.startswith(label) or line.lstrip().startswith(label):
                # Check for inline value (e.g., "Status: SUCCESS" or "Duration: 1234ms")
                stripped = line.strip()
                if stripped.startswith(label):
                    matched_section = label.rstrip(":")
                    inline_value = stripped[len(label):].strip()
                    break

        if matched_section:
            # Save previous section
            if current_section is not None:
                sections[current_section] = "\n".join(current_content)

            current_section = matched_section
            current_content = []
            if inline_value:
                current_content.append(inline_value)
        elif line.startswith("  ") and current_section is not None:
            # Indented content line - strip 2-space prefix
            current_content.append(line[2:])
        elif line.strip() == "" and current_section is not None:
            # Empty line within a section - preserve it for multi-line content
            # but only if we already have content (to avoid leading blanks)
            if current_content:
                current_content.append("")
        else:
            # Unrecognized line - could be continuation of multi-line status/error
            if current_section is not None and current_content:
                # Append to current section content (e.g., multi-line error messages)
                current_content.append(line)

    # Save last section
    if current_section is not None:
        sections[current_section] = "\n".join(current_content)

    # Clean up trailing whitespace/newlines from section values
    for key in sections:
        sections[key] = sections[key].strip()

    return sections


def _parse_timings(timing_str):
    """Parse the Timing Breakdown section into a timings dict."""
    if not timing_str:
        return None

    timings = {
        "clipboard_ms": 0,
        "payload_ms": 0,
        "request_ms": 0,
        "api_ms": 0,
        "parse_ms": 0,
        "replacements_ms": 0,
        "prompt_guard_ms": 0,
        "paste_ms": 0,
    }

    for line in timing_str.split("\n"):
        line = line.strip()
        if not line:
            continue
        # Match "Label: Nms"
        m = re.match(r"^(.+?):\s*(\d+)ms$", line)
        if m:
            label = m.group(1).strip()
            value = int(m.group(2))
            jsonl_field = TIMING_LABEL_MAP.get(label)
            if jsonl_field:
                timings[jsonl_field] = value

    # Only return timings if we found at least one timing value
    if any(v != 0 for v in timings.values()):
        return timings
    # Still return the dict even if all zeros (as long as the section existed)
    return timings


def _parse_events(events_str):
    """Parse the Events section into a list of event strings."""
    if events_str is None:
        return None
    if not events_str.strip():
        return []

    events = []
    for line in events_str.split("\n"):
        stripped = line.strip()
        if stripped:
            events.append(stripped)
    return events if events else []


def _parse_post_processing(pp_str):
    """Parse the Post-processing section into a replacements dict."""
    if pp_str is None:
        return None

    pp_str = pp_str.strip()
    if not pp_str:
        return None

    if "no replacements matched" in pp_str.lower():
        return {"count": 0, "applied": [], "urls_protected": 0}

    # Try to parse "N replacement(s) applied" pattern
    m = re.match(r"(\d+)\s+replacement", pp_str)
    if m:
        count = int(m.group(1))
        # Extract applied replacements from subsequent lines
        applied = []
        for line in pp_str.split("\n")[1:]:
            stripped = line.strip()
            if stripped:
                applied.append(stripped)
        return {"count": count, "applied": applied, "urls_protected": 0}

    # Inline single replacement: "1 replacement: X -> Y"
    m2 = re.match(r"(\d+)\s+replacement:\s*(.+)", pp_str)
    if m2:
        count = int(m2.group(1))
        applied = [m2.group(2).strip()]
        return {"count": count, "applied": applied, "urls_protected": 0}

    return {"count": 0, "applied": [], "urls_protected": 0}


def _parse_prompt_leak(pl_str):
    """Parse the Prompt-leak safeguard section."""
    if pl_str is None:
        return None

    pl_str = pl_str.strip()
    if not pl_str:
        return None

    if "no leak pattern matched" in pl_str.lower():
        return {
            "triggered": False,
            "occurrences": 0,
            "text_input_removed": False,
            "removed_chars": 0,
            "before_length": None,
            "after_length": None,
        }

    # If triggered, try to parse details
    # Pattern: "leak detected: removed N chars (before: M, after: K)"
    triggered = True
    occurrences = 1
    text_input_removed = False
    removed_chars = 0
    before_length = None
    after_length = None

    m = re.search(r"removed (\d+) chars", pl_str)
    if m:
        removed_chars = int(m.group(1))
    m2 = re.search(r"before:\s*(\d+)", pl_str)
    if m2:
        before_length = int(m2.group(1))
    m3 = re.search(r"after:\s*(\d+)", pl_str)
    if m3:
        after_length = int(m3.group(1))
    if "text input" in pl_str.lower():
        text_input_removed = True

    return {
        "triggered": triggered,
        "occurrences": occurrences,
        "text_input_removed": text_input_removed,
        "removed_chars": removed_chars,
        "before_length": before_length,
        "after_length": after_length,
    }


# ---------------------------------------------------------------------------
# Output routing and file writing
# ---------------------------------------------------------------------------

def write_entries_to_weekly_files(entries, dry_run=False):
    """Write entries to weekly JSONL files. Returns dict of filepath -> entry count."""
    file_counts = {}

    for entry in entries:
        ts_str = entry["timestamp"]
        try:
            dt = datetime.strptime(ts_str, "%Y-%m-%d %H:%M:%S")
        except ValueError:
            continue

        week_start = get_week_start_stamp(dt)
        week_end = get_week_end_stamp(dt)

        # Serialize entry
        json_line = json.dumps(entry, ensure_ascii=False, separators=(",", ":")) + "\n"
        line_bytes = len(json_line.encode("utf-8"))

        if not dry_run:
            filepath = resolve_weekly_log_path(week_start, week_end, line_bytes)
            with open(filepath, "a", encoding="utf-8", newline="\n") as f:
                f.write(json_line)
            key = str(filepath)
        else:
            # For dry run, compute which file it would go to
            filepath = resolve_weekly_log_path(week_start, week_end, line_bytes)
            key = str(filepath)

        file_counts[key] = file_counts.get(key, 0) + 1

    return file_counts


def archive_log_files(dry_run=False):
    """Move all .log files from logs/ to logs/archive/."""
    if not dry_run:
        ARCHIVE_DIR.mkdir(parents=True, exist_ok=True)

    moved = []
    for f in sorted(LOGS_DIR.iterdir()):
        if f.is_file() and f.suffix == ".log":
            dest = ARCHIVE_DIR / f.name
            if not dry_run:
                shutil.move(str(f), str(dest))
            moved.append(f.name)

    return moved


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def main():
    dry_run = "--dry-run" in sys.argv

    if dry_run:
        print("=" * 60)
        print("DRY RUN MODE - No files will be written or moved")
        print("=" * 60)
        print()

    # Initialize stats
    stats = {
        "f1a_count": 0,
        "f1b_count": 0,
        "f2_2a_count": 0,
        "f2_2b_count": 0,
        "f2_2c_count": 0,
        "f2_2d_count": 0,
        "f1_dedup_skipped": 0,
        "error_count": 0,
        "errors": [],
    }

    all_entries = []

    # -----------------------------------------------------------------------
    # Step 1: Parse Format 2 files first (for dedup set)
    # -----------------------------------------------------------------------
    print("Parsing Format 2 (detailed) log files...")
    f2_files = sorted([
        f for f in LOGS_DIR.iterdir()
        if f.is_file() and f.name.startswith("spellcheck-detailed") and f.suffix == ".log"
    ])

    for f2_file in f2_files:
        print(f"  {f2_file.name}...", end=" ", flush=True)
        entries = parse_format2(f2_file, stats)
        print(f"{len(entries)} entries")
        all_entries.extend(entries)

    # Build dedup set from Oct 14 entries
    dedup_set = set()
    for entry in all_entries:
        if entry["timestamp"].startswith("2025-10-14"):
            dedup_set.add((entry["timestamp"], entry["input_text"][:100]))

    print(f"\nDedup set: {len(dedup_set)} Oct 14 entries from Format 2")
    print()

    # -----------------------------------------------------------------------
    # Step 2: Parse Format 1 file
    # -----------------------------------------------------------------------
    f1_file = LOGS_DIR / "OLD - spellcheck.log"
    if f1_file.exists():
        print(f"Parsing Format 1 (pipe-delimited): {f1_file.name}...")
        f1_entries = parse_format1(f1_file, dedup_set, stats)
        print(f"  {len(f1_entries)} entries (after dedup)")
        all_entries.extend(f1_entries)
    else:
        print(f"WARNING: Format 1 file not found: {f1_file}")

    # -----------------------------------------------------------------------
    # Step 3: Sort all entries by timestamp
    # -----------------------------------------------------------------------
    print(f"\nSorting {len(all_entries)} entries by timestamp...")
    all_entries.sort(key=lambda e: e["timestamp"])

    # -----------------------------------------------------------------------
    # Step 4: Write to weekly files
    # -----------------------------------------------------------------------
    print(f"\nWriting entries to weekly JSONL files{'  (DRY RUN)' if dry_run else ''}...")
    file_counts = write_entries_to_weekly_files(all_entries, dry_run=dry_run)

    # -----------------------------------------------------------------------
    # Step 5: Archive old files
    # -----------------------------------------------------------------------
    print(f"\nArchiving .log files{'  (DRY RUN)' if dry_run else ''}...")
    archived = archive_log_files(dry_run=dry_run)

    # -----------------------------------------------------------------------
    # Report
    # -----------------------------------------------------------------------
    print("\n" + "=" * 60)
    print("CONVERSION REPORT")
    print("=" * 60)

    print(f"\n--- Entry Counts by Format ---")
    print(f"  Format 1a (pipe, no Raw AI):  {stats['f1a_count']}")
    print(f"  Format 1b (pipe, with Raw AI): {stats['f1b_count']}")
    print(f"  Format 2a (detailed, era 2a):  {stats.get('f2_2a_count', 0)}")
    print(f"  Format 2b (detailed, era 2b):  {stats.get('f2_2b_count', 0)}")
    print(f"  Format 2c (detailed, era 2c):  {stats.get('f2_2c_count', 0)}")
    print(f"  Format 2d (detailed, era 2d):  {stats.get('f2_2d_count', 0)}")

    f1_total = stats["f1a_count"] + stats["f1b_count"]
    f2_total = sum(stats.get(f"f2_{era}_count", 0) for era in ["2a", "2b", "2c", "2d"])
    print(f"\n  Format 1 total: {f1_total}")
    print(f"  Format 2 total: {f2_total}")
    print(f"  Grand total: {len(all_entries)}")

    print(f"\n--- Deduplication ---")
    print(f"  Oct 14 entries in dedup set: {len(dedup_set)}")
    print(f"  Format 1 entries skipped (dedup): {stats['f1_dedup_skipped']}")

    print(f"\n--- Weekly Files ---")
    for path, count in sorted(file_counts.items()):
        print(f"  {os.path.basename(path)}: {count} entries")

    print(f"\n--- Archived Files ---")
    for name in archived:
        print(f"  {name}")

    if stats["errors"]:
        print(f"\n--- Parse Errors ({stats['error_count']}) ---")
        for err in stats["errors"][:20]:
            print(f"  {err}", file=sys.stderr)
        if len(stats["errors"]) > 20:
            print(f"  ... and {len(stats['errors']) - 20} more", file=sys.stderr)
    else:
        print(f"\n--- No Parse Errors ---")

    print(f"\nDone{'  (DRY RUN - no files modified)' if dry_run else ''}.")


if __name__ == "__main__":
    main()
