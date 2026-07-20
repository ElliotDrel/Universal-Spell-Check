#!/usr/bin/env python3
"""
logs.py - Read and filter Universal Spell Check JSONL logs.

Log files live at: %LOCALAPPDATA%/UniversalSpellCheck.Data/logs/spellcheck-YYYY-MM-DD.jsonl
Each line: {ISO8601} channel={prod|dev} app_version={semver} pid={int} {event} {key=value...}
spellcheck_detail lines have a JSON blob after the event name.
"""
import argparse
import json
import os
import re
import sys
from datetime import date, timedelta
from pathlib import Path

LOG_DIR = Path(os.environ.get("LOCALAPPDATA", "")) / "UniversalSpellCheck.Data" / "logs"

LINE_RE = re.compile(
    r"^(?P<ts>\S+)\s+channel=(?P<channel>\S+)\s+app_version=(?P<version>\S+)\s+pid=(?P<pid>\d+)\s+(?P<event>\S+)(?:\s+(?P<rest>.*))?$"
)

ERROR_EVENTS = {
    "request_failed", "request_retrying", "capture_failed", "paste_failed",
    "replacements_reload_failed", "guard_rejected", "connection_warm_failed",
    "data_migration_failed", "activity_load_failed",
}


def parse_kv(s):
    result = {}
    for m in re.finditer(r'(\w+)=("(?:[^"\\]|\\.)*"|\S+)', s or ""):
        k, v = m.group(1), m.group(2)
        if v.startswith('"') and v.endswith('"'):
            v = v[1:-1]
        result[k] = v
    return result


def format_detail(d, ts, channel):
    status = d.get("status", "?")
    model = d.get("model", "?")
    app = d.get("active_exe", "?")
    input_chars = d.get("input_chars", 0)
    output_chars = d.get("output_chars", 0)
    changed = d.get("text_changed", False)
    timings = d.get("timings", {})
    total_ms = timings.get("total_ms", 0)
    request_ms = timings.get("request_ms", 0)
    tokens = d.get("tokens", {})

    status_icon = "OK" if status == "success" else "FAIL"
    changed_icon = "changed" if changed else "no-change"

    lines = [
        f"[{ts[:19]}] [{status_icon}] {channel} | {model} | {app} | {changed_icon}",
        f"  {input_chars}->{output_chars} chars | {total_ms}ms total ({request_ms}ms API) | {tokens.get('total', 0)} tokens",
    ]

    replacements = d.get("replacements", {})
    if replacements.get("count", 0) > 0:
        applied = replacements.get("applied", [])
        lines.append(f"  replacements: {', '.join(applied)}")

    html_chars = d.get("clipboard_html_chars", 0)
    if html_chars:
        note = " (truncated in log)" if d.get("clipboard_html_truncated") else ""
        lines.append(f"  clipboard_html: {html_chars} chars{note}")

    if d.get("error"):
        lines.append(f"  error: {d['error']}")

    if d.get("prompt_leak", {}).get("triggered"):
        lines.append(f"  prompt_leak: triggered, removed {d['prompt_leak'].get('removed_chars', 0)} chars")

    return "\n".join(lines)


def format_generic(event, rest, ts, channel):
    rest_str = (rest or "")[:200]
    return f"[{ts[:19]}] {channel} | {event} {rest_str}"


def compute_stats(details):
    total = len(details)
    if total == 0:
        return "No spellcheck_detail events found."

    successes = sum(1 for d in details if d.get("status") == "success")
    changed = sum(1 for d in details if d.get("text_changed", False))
    total_ms_vals = [d.get("timings", {}).get("total_ms", 0) for d in details]
    request_ms_vals = [d.get("timings", {}).get("request_ms", 0) for d in details]
    token_vals = [d.get("tokens", {}).get("total", 0) for d in details]

    avg_total = sum(total_ms_vals) / total
    avg_request = sum(request_ms_vals) / total
    avg_tokens = sum(token_vals) / total
    p50_total = sorted(total_ms_vals)[total // 2]

    apps: dict[str, int] = {}
    for d in details:
        app = d.get("active_exe", "unknown")
        apps[app] = apps.get(app, 0) + 1
    top_apps = sorted(apps.items(), key=lambda x: -x[1])[:5]

    models: dict[str, int] = {}
    for d in details:
        m = d.get("model", "unknown")
        models[m] = models.get(m, 0) + 1

    lines = [
        f"=== Stats: {total} spellcheck runs ===",
        f"Success rate:    {successes}/{total} ({100 * successes // total}%)",
        f"Text changed:    {changed}/{total} ({100 * changed // total}%)",
        f"Avg latency:     {avg_total:.0f}ms total / {avg_request:.0f}ms API",
        f"Median latency:  {p50_total}ms",
        f"Avg tokens:      {avg_tokens:.0f}/run",
        f"Models:          {', '.join(f'{m}={c}' for m, c in models.items())}",
        f"Top apps:        {', '.join(f'{a}={c}' for a, c in top_apps)}",
    ]
    return "\n".join(lines)


def collect_files(log_dir: Path, from_date: date, to_date: date) -> list[Path]:
    files = []
    current = from_date
    while current <= to_date:
        f = log_dir / f"spellcheck-{current.isoformat()}.jsonl"
        if f.exists():
            files.append(f)
        current += timedelta(days=1)
    return files


_GREP_DETAIL_FIELDS = ("input_text", "output_text", "raw_ai_output")


def _grep_detail_match(detail, query):
    """Return True if query matches detail. Supports 'field:value' or plain substring (all three text fields)."""
    if ":" in query:
        field, _, needle = query.partition(":")
        return needle.lower() in str(detail.get(field, "")).lower()
    needle = query.lower()
    return any(needle in str(detail.get(f, "")).lower() for f in _GREP_DETAIL_FIELDS)


def main():
    today = date.today()

    parser = argparse.ArgumentParser(
        description="Read and filter Universal Spell Check logs.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""Examples:
  python logs.py --today
  python logs.py --today --stats
  python logs.py --today --errors
  python logs.py --today --event spellcheck_detail --app chrome
  python logs.py --today --event spellcheck_detail --last 5
  python logs.py --from 2026-05-20 --to 2026-05-24 --stats
  python logs.py --today --channel dev
  python logs.py --today --raw --event run_completed
  python logs.py --today --json --event spellcheck_detail | python -m json.tool
  python logs.py --today --grep-detail competition
  python logs.py --today --grep-detail output_text:Competitionetition
  python logs.py --today --grep-detail raw_ai_output:competition --last 10
        """,
    )
    parser.add_argument("--from", dest="from_date", metavar="YYYY-MM-DD",
                        help="Start date (default: today)")
    parser.add_argument("--to", dest="to_date", metavar="YYYY-MM-DD",
                        help="End date (default: today)")
    parser.add_argument("--today", action="store_true",
                        help="Shorthand for today only")
    parser.add_argument("--event", "-e", metavar="EVENT",
                        help="Filter to one event name (e.g. spellcheck_detail, run_completed, request_failed)")
    parser.add_argument("--channel", "-c", choices=["prod", "dev"],
                        help="Filter by channel")
    parser.add_argument("--app", "-a", metavar="EXE",
                        help="Filter spellcheck_detail by active_exe substring (e.g. chrome, code, notepad)")
    parser.add_argument("--errors", action="store_true",
                        help="Show only error/failure events")
    parser.add_argument("--stats", "-s", action="store_true",
                        help="Print aggregate stats for spellcheck_detail events")
    parser.add_argument("--last", "-n", type=int, metavar="N",
                        help="Show last N matching lines")
    parser.add_argument("--raw", action="store_true",
                        help="Print raw log lines (no formatting)")
    parser.add_argument("--json", dest="json_out", action="store_true",
                        help="Output as JSON objects (one per line), good for piping")
    parser.add_argument("--log-dir", metavar="PATH",
                        help=f"Override log directory (default: {LOG_DIR})")
    parser.add_argument("--grep-detail", metavar="QUERY",
                        help="Filter spellcheck_detail rows by substring match. "
                             "Plain string searches input_text, output_text, and raw_ai_output (case-insensitive). "
                             "Use 'field:value' to scope to one field (e.g. output_text:Competitionetition).")

    parser.add_argument("--has-html", action="store_true",
                        help="Only spellcheck_detail rows whose selection carried a CF_HTML flavor. "
                             "The markup itself is in the clipboard_html field; use --json to extract it.")

    args = parser.parse_args()

    log_dir = Path(args.log_dir) if args.log_dir else LOG_DIR
    if not log_dir.exists():
        print(f"Error: log directory not found: {log_dir}", file=sys.stderr)
        sys.exit(1)

    if args.today:
        from_date = to_date = today
    else:
        from_date = date.fromisoformat(args.from_date) if args.from_date else today
        to_date = date.fromisoformat(args.to_date) if args.to_date else today

    files = collect_files(log_dir, from_date, to_date)
    if not files:
        print(f"No log files found for {from_date} to {to_date}")
        sys.exit(0)

    results = []

    for fpath in files:
        with open(fpath, encoding="utf-8") as fh:
            for line in fh:
                line = line.rstrip()
                if not line:
                    continue
                m = LINE_RE.match(line)
                if not m:
                    continue

                ts = m.group("ts")
                channel = m.group("channel")
                version = m.group("version")
                pid = m.group("pid")
                event = m.group("event")
                rest = (m.group("rest") or "").strip()

                if args.channel and channel != args.channel:
                    continue

                if args.errors:
                    if event not in ERROR_EVENTS:
                        continue
                elif args.event and event != args.event:
                    continue

                if args.app:
                    if event != "spellcheck_detail":
                        continue
                    try:
                        detail = json.loads(rest)
                        if args.app.lower() not in detail.get("active_exe", "").lower():
                            continue
                    except json.JSONDecodeError:
                        continue

                if args.grep_detail:
                    if event != "spellcheck_detail":
                        continue
                    try:
                        detail = json.loads(rest)
                        if not _grep_detail_match(detail, args.grep_detail):
                            continue
                    except json.JSONDecodeError:
                        continue

                if args.has_html:
                    if event != "spellcheck_detail":
                        continue
                    try:
                        detail = json.loads(rest)
                        if not detail.get("clipboard_html_chars", 0):
                            continue
                    except json.JSONDecodeError:
                        continue

                results.append((ts, channel, version, pid, event, rest, line))

    if args.last:
        results = results[-args.last:]

    if args.stats:
        details = []
        for ts, channel, version, pid, event, rest, raw in results:
            if event == "spellcheck_detail":
                try:
                    details.append(json.loads(rest))
                except json.JSONDecodeError:
                    pass
        print(compute_stats(details))
        return

    if not results:
        print("No matching log entries found.")
        return

    for ts, channel, version, pid, event, rest, raw in results:
        if args.raw:
            print(raw)
        elif args.json_out:
            obj: dict = {"ts": ts, "channel": channel, "version": version, "pid": pid, "event": event}
            if event == "spellcheck_detail":
                try:
                    obj["detail"] = json.loads(rest)
                except json.JSONDecodeError:
                    obj["rest"] = rest
            else:
                obj["fields"] = parse_kv(rest)
            print(json.dumps(obj))
        elif event == "spellcheck_detail":
            try:
                detail = json.loads(rest)
                print(format_detail(detail, ts, channel))
                print()
            except json.JSONDecodeError:
                print(format_generic(event, rest[:120], ts, channel))
        else:
            print(format_generic(event, rest, ts, channel))


if __name__ == "__main__":
    main()
