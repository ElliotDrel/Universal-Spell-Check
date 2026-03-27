#!/usr/bin/env python3
"""Generate an HTML log viewer from JSONL spell-check logs.

Usage:
    python generate_log_viewer.py            # reads logs/*.jsonl, writes logs/viewer.html, opens in browser
    python generate_log_viewer.py --no-open # same, but skip opening in browser

If a legacy logs/spellcheck.jsonl file exists, the script first migrates it into
weekly spellcheck-YYYY-MM-DD-to-YYYY-MM-DD.jsonl files and keeps the original as a .bak file.
"""

import json
import glob
import os
import re
import sys
import html
from datetime import datetime, timedelta
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
LOGS_DIR = SCRIPT_DIR / "logs"
OUTPUT_FILE = LOGS_DIR / "viewer.html"
LEGACY_LOG_FILE = LOGS_DIR / "spellcheck.jsonl"
WEEKLY_LOG_PREFIX = "spellcheck"
MAX_WEEKLY_LOG_SIZE = 5 * 1024 * 1024  # Keep in sync with Universal Spell Checker.ahk
OLD_WEEKLY_LOG_RE = re.compile(rf"^{WEEKLY_LOG_PREFIX}-(\d{{4}}-\d{{2}}-\d{{2}})(?:-(\d+))?\.jsonl$")


def get_week_start_stamp(dt):
    """Return the Monday-based week start stamp for a datetime."""
    return (dt - timedelta(days=dt.weekday())).strftime("%Y-%m-%d")


def get_week_end_stamp(dt):
    """Return the Sunday-based week end stamp for a datetime."""
    return (dt + timedelta(days=(6 - dt.weekday()))).strftime("%Y-%m-%d")


def build_weekly_log_path(week_start_stamp, week_end_stamp, suffix_index=0):
    """Return the canonical weekly log path for a week and optional overflow suffix."""
    suffix = f"-{suffix_index + 1}" if suffix_index > 0 else ""
    return LOGS_DIR / f"{WEEKLY_LOG_PREFIX}-{week_start_stamp}-to-{week_end_stamp}{suffix}.jsonl"


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


def parse_entry_timestamp(entry):
    """Parse the existing log timestamp format."""
    timestamp = (entry.get("timestamp") or "").strip()
    if not timestamp:
        return None
    try:
        return datetime.strptime(timestamp, "%Y-%m-%d %H:%M:%S")
    except ValueError:
        return None


def migrate_legacy_log():
    """Split the legacy single JSONL file into weekly files and keep a backup."""
    if not LEGACY_LOG_FILE.exists():
        return None

    migrated = 0
    skipped = 0

    with LEGACY_LOG_FILE.open(encoding="utf-8", errors="replace") as src:
        for lineno, raw_line in enumerate(src, 1):
            line = raw_line.strip()
            if not line:
                continue

            try:
                entry = json.loads(line)
            except json.JSONDecodeError:
                skipped += 1
                continue

            entry_dt = parse_entry_timestamp(entry)
            if entry_dt is None:
                skipped += 1
                continue

            week_start_stamp = get_week_start_stamp(entry_dt)
            week_end_stamp = get_week_end_stamp(entry_dt)
            payload = line + "\n"
            target = resolve_weekly_log_path(week_start_stamp, week_end_stamp, len(payload.encode("utf-8")))
            with target.open("a", encoding="utf-8", newline="\n") as dst:
                dst.write(payload)
            migrated += 1

    backup_name = f"spellcheck-legacy-migrated-{datetime.now().strftime('%Y-%m-%d-%H%M%S')}.bak"
    legacy_backup = LOGS_DIR / backup_name
    LEGACY_LOG_FILE.replace(legacy_backup)
    return {
        "migrated": migrated,
        "skipped": skipped,
        "backup": legacy_backup,
    }


def migrate_old_weekly_log_names():
    """Rename old single-date weekly filenames to the new start-to-end range format."""
    renamed = []
    skipped = []

    for path in sorted(LOGS_DIR.glob(f"{WEEKLY_LOG_PREFIX}-*.jsonl")):
        match = OLD_WEEKLY_LOG_RE.match(path.name)
        if not match:
            continue

        week_start_stamp = match.group(1)
        suffix_value = int(match.group(2)) if match.group(2) else 1
        week_end_stamp = get_week_end_stamp(datetime.strptime(week_start_stamp, "%Y-%m-%d"))
        target = build_weekly_log_path(week_start_stamp, week_end_stamp, suffix_value - 1)

        if target.exists():
            skipped.append((path.name, target.name))
            continue

        path.replace(target)
        renamed.append((path.name, target.name))

    return {
        "renamed": renamed,
        "skipped": skipped,
    }


def load_entries():
    """Read all .jsonl files in logs/, return list of parsed dicts sorted by timestamp."""
    entries = []
    for path in sorted(LOGS_DIR.glob("*.jsonl")):
        with open(path, encoding="utf-8", errors="replace") as f:
            for lineno, line in enumerate(f, 1):
                line = line.strip()
                if not line:
                    continue
                try:
                    entries.append(json.loads(line))
                except json.JSONDecodeError:
                    print(f"  Warning: skipped malformed line {lineno} in {path.name}")
    # Sort newest first
    entries.sort(key=lambda e: e.get("timestamp", ""), reverse=True)
    return entries


def compute_stats(entries):
    """Compute summary statistics."""
    total = len(entries)
    if total == 0:
        return {"total": 0}

    successes = sum(1 for e in entries if e.get("status") == "SUCCESS")
    durations = [e["duration_ms"] for e in entries if "duration_ms" in e and e.get("status") == "SUCCESS"]
    api_times = [(e.get("timings") or {}).get("api_ms", 0) for e in entries if e.get("status") == "SUCCESS"]
    total_tokens = sum((e.get("tokens") or {}).get("total", 0) for e in entries)
    changed = sum(1 for e in entries if e.get("text_changed"))

    return {
        "total": total,
        "successes": successes,
        "errors": total - successes,
        "success_rate": round(successes / total * 100, 1) if total else 0,
        "avg_duration": round(sum(durations) / len(durations)) if durations else 0,
        "avg_api_time": round(sum(api_times) / len(api_times)) if api_times else 0,
        "total_tokens": total_tokens,
        "text_changed_pct": round(changed / total * 100, 1) if total else 0,
    }


HTML_TEMPLATE = r"""<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>Spell Check &mdash; Log Viewer</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link href="https://fonts.googleapis.com/css2?family=Cormorant+Garamond:wght@400;600;700&family=Barlow:wght@400;500;600&family=Fira+Code:wght@400;500&display=swap" rel="stylesheet">
<style>
  :root {
    --bg: #0b0c10; --surface: #14161e;
    --border: #252838; --border-light: #353950;
    --text: #e4dfd6; --text-mid: #aea89e; --text-dim: #6e6960;
    --accent: #c9a84c; --accent-bright: #e0bf5e;
    --accent-subtle: rgba(201,168,76,0.08);
    --success: #5ea671; --error: #d45454;
    --font-display: 'Cormorant Garamond', Georgia, serif;
    --font-body: 'Barlow', 'Segoe UI', sans-serif;
    --font-mono: 'Fira Code', Consolas, monospace;
  }
  * { box-sizing: border-box; margin: 0; padding: 0; }
  body {
    font-family: var(--font-body); background: var(--bg);
    background-image:
      radial-gradient(ellipse at 15% -5%, rgba(201,168,76,0.04) 0%, transparent 50%),
      radial-gradient(ellipse at 85% 105%, rgba(94,166,113,0.025) 0%, transparent 50%);
    color: var(--text); line-height: 1.5; min-height: 100vh;
  }
  .container { max-width: 1440px; margin: 0 auto; padding: 32px 40px; }

  /* --- Header --- */
  .header { text-align: center; margin-bottom: 40px; }
  .header-rule { display: flex; align-items: center; gap: 16px; margin-bottom: 16px; }
  .header-rule::before, .header-rule::after {
    content: ''; flex: 1; height: 1px;
    background: linear-gradient(90deg, transparent, var(--accent) 40%, var(--accent) 60%, transparent);
    opacity: 0.5;
  }
  .diamond {
    width: 7px; height: 7px; background: var(--accent);
    transform: rotate(45deg); flex-shrink: 0;
    animation: diamondIn 0.6s ease-out 0.15s both;
  }
  @keyframes diamondIn {
    from { opacity: 0; transform: rotate(45deg) scale(0); }
    to { opacity: 1; transform: rotate(45deg) scale(1); }
  }
  h1 {
    font-family: var(--font-display); font-size: 2.6em; font-weight: 700;
    letter-spacing: 0.015em; animation: fadeUp 0.5s ease 0.1s both;
  }
  h1 .sep { color: var(--accent); margin: 0 4px; }
  .header-sub {
    font-size: 0.82em; color: var(--text-dim); letter-spacing: 0.1em;
    text-transform: uppercase; margin-top: 8px;
    animation: fadeUp 0.5s ease 0.2s both;
  }

  /* --- Stats --- */
  .stats {
    display: grid; grid-template-columns: repeat(auto-fit, minmax(155px, 1fr));
    gap: 14px; margin-bottom: 32px;
  }
  .stat-card {
    background: var(--surface); border: 1px solid var(--border);
    border-top: 2px solid var(--accent); border-radius: 2px 2px 8px 8px;
    padding: 18px 14px 14px; text-align: center;
    transition: border-color 0.3s, transform 0.3s, box-shadow 0.3s;
    animation: fadeUp 0.5s ease both;
  }
  .stat-card:hover {
    border-top-color: var(--accent-bright); transform: translateY(-2px);
    box-shadow: 0 8px 24px rgba(0,0,0,0.3);
  }
  .stat-card:nth-child(1){animation-delay:.3s}
  .stat-card:nth-child(2){animation-delay:.35s}
  .stat-card:nth-child(3){animation-delay:.4s}
  .stat-card:nth-child(4){animation-delay:.45s}
  .stat-card:nth-child(5){animation-delay:.5s}
  .stat-card:nth-child(6){animation-delay:.55s}
  .stat-card:nth-child(7){animation-delay:.6s}
  @keyframes fadeUp {
    from { opacity: 0; transform: translateY(14px); }
    to { opacity: 1; transform: translateY(0); }
  }
  .stat-value {
    font-family: var(--font-mono); font-size: 1.75em; font-weight: 500; line-height: 1.2;
  }
  .stat-value.green { color: var(--success); }
  .stat-value.red { color: var(--error); }
  .stat-value small { font-size: 0.48em; color: var(--text-dim); font-weight: 400; margin-left: 1px; }
  .stat-label {
    font-size: 0.68em; font-weight: 600; text-transform: uppercase;
    letter-spacing: 0.14em; color: var(--text-dim); margin-top: 6px;
  }

  /* --- Controls --- */
  .controls { display: flex; gap: 12px; margin-bottom: 24px; flex-wrap: wrap; align-items: center; }
  .controls input[type="text"], .controls select {
    font-family: var(--font-body); background: var(--surface); color: var(--text);
    border: 1px solid var(--border); border-radius: 6px; padding: 8px 14px; font-size: 0.88em;
    transition: border-color 0.2s, box-shadow 0.2s;
  }
  .controls input[type="text"]:focus, .controls select:focus {
    outline: none; border-color: var(--accent);
    box-shadow: 0 0 0 3px var(--accent-subtle);
  }
  .controls input[type="text"] {
    flex: 1; min-width: 220px;
    background-image: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='14' height='14' viewBox='0 0 24 24' fill='none' stroke='%236e6960' stroke-width='2'%3E%3Ccircle cx='11' cy='11' r='8'/%3E%3Cline x1='21' y1='21' x2='16.65' y2='16.65'/%3E%3C/svg%3E");
    background-repeat: no-repeat; background-position: 12px center; padding-left: 34px;
  }
  .controls select {
    min-width: 130px; cursor: pointer; appearance: none; -webkit-appearance: none;
    background-image: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='10' height='6'%3E%3Cpath d='M0 0l5 6 5-6z' fill='%236e6960'/%3E%3C/svg%3E");
    background-repeat: no-repeat; background-position: right 10px center; padding-right: 28px;
  }
  .controls label {
    font-size: 0.85em; color: var(--text-mid); cursor: pointer;
    display: flex; align-items: center; gap: 6px;
  }
  .controls label input[type="checkbox"] { accent-color: var(--accent); }

  /* --- Table --- */
  .table-wrap {
    border: 1px solid var(--border); border-radius: 8px; overflow: hidden;
    box-shadow: 0 4px 16px rgba(0,0,0,0.15);
  }
  table { width: 100%; border-collapse: collapse; font-size: 0.85em; }
  thead th {
    font-family: var(--font-body); font-weight: 600; font-size: 0.7em;
    text-transform: uppercase; letter-spacing: 0.1em; color: var(--text-dim);
    background: var(--surface); padding: 12px 14px; text-align: left;
    border-bottom: 1px solid var(--border); cursor: pointer;
    user-select: none; white-space: nowrap; position: sticky; top: 0; z-index: 10;
    transition: color 0.2s;
  }
  thead th:hover { color: var(--accent); }
  thead th .arrow { margin-left: 4px; font-size: 0.85em; color: var(--accent); }
  tbody tr { border-bottom: 1px solid var(--border); transition: background 0.15s; }
  tbody tr:not(.detail-row):hover { background: var(--accent-subtle); }
  tbody tr:not(.detail-row):hover td:first-child { box-shadow: inset 3px 0 0 var(--accent); }
  td {
    padding: 10px 14px; vertical-align: middle; font-family: var(--font-mono);
    font-size: 0.92em; max-width: 300px; overflow: hidden;
    text-overflow: ellipsis; white-space: nowrap;
  }
  td.wrap { white-space: normal; word-break: break-word; }
  .col-ts { color: var(--text-mid); font-size: 0.88em; }
  .status-ok { color: var(--success); font-weight: 600; }
  .status-err { color: var(--error); font-weight: 600; }
  .dot { display: inline-block; width: 8px; height: 8px; border-radius: 50%; }
  .dot-yes { background: var(--success); box-shadow: 0 0 8px rgba(94,166,113,0.4); }
  .dot-no { background: var(--border-light); }

  /* --- Expand button --- */
  .expand-btn {
    font-family: var(--font-body); background: transparent; border: 1px solid var(--border);
    color: var(--accent); border-radius: 4px; padding: 4px 12px; cursor: pointer;
    font-size: 0.75em; font-weight: 500; letter-spacing: 0.06em;
    text-transform: uppercase; transition: all 0.2s;
  }
  .expand-btn:hover { background: var(--accent-subtle); border-color: var(--accent); }

  /* --- Detail panel --- */
  .detail-row td { padding: 0 !important; border-bottom: none; }
  .detail-content {
    max-height: 0; overflow: hidden;
    transition: max-height 0.4s ease-out, padding 0.3s ease-out;
    background: var(--surface); padding: 0 20px;
    border-top: 1px solid transparent;
  }
  .detail-content.open {
    max-height: 4000px; padding: 24px 20px;
    border-top-color: var(--accent);
    transition: max-height 0.6s ease-in, padding 0.3s ease-in;
  }
  .detail-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 20px; margin-bottom: 20px; }
  .detail-section { margin-bottom: 16px; }
  .detail-section h4 {
    font-family: var(--font-display); font-size: 1.05em; font-weight: 600;
    color: var(--accent); margin-bottom: 8px; padding-bottom: 5px;
    border-bottom: 1px solid var(--border);
  }
  .detail-section pre {
    font-family: var(--font-mono); background: var(--bg);
    border: 1px solid var(--border); border-radius: 6px; padding: 12px 14px;
    overflow-x: auto; white-space: pre-wrap; word-break: break-word;
    font-size: 0.82em; max-height: 300px; overflow-y: auto;
    line-height: 1.65; color: var(--text-mid);
  }
  .detail-section .kv {
    display: grid; grid-template-columns: 150px 1fr; gap: 4px 16px; font-size: 0.88em;
  }
  .detail-section .kv .k { color: var(--text-dim); font-weight: 500; }
  .detail-section .kv .v { font-family: var(--font-mono); color: var(--text); }

  /* --- Timing bar --- */
  .timing-bar {
    display: flex; height: 22px; border-radius: 4px; overflow: hidden;
    margin: 8px 0 12px; background: var(--bg); border: 1px solid var(--border);
  }
  .timing-bar > div {
    height: 100%; display: flex; align-items: center; justify-content: center;
    font-family: var(--font-mono); font-size: 0.62em; font-weight: 500;
    color: #fff; min-width: 2px; transition: filter 0.2s; cursor: default;
  }
  .timing-bar > div:hover { filter: brightness(1.2); }
  .tb-clip { background: #3d5a80; } .tb-payload { background: #4a7fb5; }
  .tb-req { background: #5b9bd5; } .tb-api { background: #c9a84c; }
  .tb-parse { background: #5ea671; } .tb-replace { background: #4a8c5e; }
  .tb-guard { background: #6e6960; } .tb-paste { background: #504a42; }

  /* --- Footer --- */
  footer {
    margin-top: 32px; padding-top: 16px; border-top: 1px solid var(--border);
    text-align: center; font-size: 0.78em; color: var(--text-dim); letter-spacing: 0.04em;
  }

  /* --- Stale data banner --- */
  .stale-banner {
    display: flex; align-items: center; gap: 16px;
    background: linear-gradient(135deg, rgba(201,168,76,0.12), rgba(201,168,76,0.06));
    border: 1px solid var(--accent); border-radius: 8px;
    padding: 16px 20px; margin-bottom: 24px;
    animation: fadeUp 0.5s ease 0.1s both;
  }
  .stale-content { flex: 1; }
  .stale-content strong { color: var(--accent-bright); display: block; margin-bottom: 4px; }
  .stale-msg { font-size: 0.88em; color: var(--text-mid); }
  .stale-cmd {
    display: flex; align-items: center; gap: 8px; margin-top: 10px;
    background: var(--bg); border: 1px solid var(--border); border-radius: 6px;
    padding: 8px 12px;
  }
  .stale-cmd code {
    font-family: var(--font-mono); font-size: 0.82em; color: var(--text);
    flex: 1; user-select: all;
  }
  .copy-btn {
    font-family: var(--font-body); background: var(--accent); color: var(--bg);
    border: none; border-radius: 4px; padding: 5px 14px; cursor: pointer;
    font-size: 0.78em; font-weight: 600; text-transform: uppercase;
    letter-spacing: 0.06em; transition: background 0.2s; white-space: nowrap;
  }
  .copy-btn:hover { background: var(--accent-bright); }
  .stale-dismiss {
    background: transparent; border: none; color: var(--text-dim);
    font-size: 1.4em; cursor: pointer; padding: 4px 8px; line-height: 1;
    transition: color 0.2s; align-self: flex-start;
  }
  .stale-dismiss:hover { color: var(--text); }

  /* --- Scrollbar --- */
  ::-webkit-scrollbar { width: 6px; height: 6px; }
  ::-webkit-scrollbar-track { background: var(--bg); }
  ::-webkit-scrollbar-thumb { background: var(--border); border-radius: 3px; }
  ::-webkit-scrollbar-thumb:hover { background: var(--border-light); }

  @media (max-width: 900px) {
    .container { padding: 20px 16px; }
    h1 { font-size: 1.8em; }
    td { font-size: 0.78em; padding: 6px 8px; }
    .detail-grid { grid-template-columns: 1fr; }
    .detail-section .kv { grid-template-columns: 120px 1fr; }
  }
</style>
</head>
<body>
<div class="container">

  <header class="header">
    <div class="header-rule"><div class="diamond"></div></div>
    <h1>Spell Check <span class="sep">&middot;</span> Log Viewer</h1>
    <div class="header-sub" id="headerSub"></div>
  </header>

  <div id="staleBanner" class="stale-banner" style="display:none">
    <div class="stale-content">
      <strong id="staleTitle"></strong>
      <span class="stale-msg">There may be newer spell-check logs. Re-run the generator to see the latest data.</span>
      <div class="stale-cmd">
        <code id="rerunCmd"></code>
        <button class="copy-btn" onclick="copyCmd()">Copy</button>
      </div>
    </div>
    <button class="stale-dismiss" onclick="this.parentElement.style.display='none'">&times;</button>
  </div>

  <div class="stats" id="stats"></div>

  <div class="controls">
    <input type="text" id="search" placeholder="Search text, app, error&hellip;">
    <select id="filterStatus"><option value="">All Status</option><option value="SUCCESS">Success</option><option value="ERROR">Error</option></select>
    <select id="filterModel"></select>
    <select id="filterApp"></select>
    <label><input type="checkbox" id="changedOnly"> Changed only</label>
  </div>

  <div class="table-wrap">
    <table>
      <thead>
        <tr>
          <th data-col="timestamp">Time <span class="arrow"></span></th>
          <th data-col="status">Status <span class="arrow"></span></th>
          <th data-col="duration_ms">Duration <span class="arrow"></span></th>
          <th data-col="model">Model <span class="arrow"></span></th>
          <th data-col="active_app">App <span class="arrow"></span></th>
          <th data-col="input_chars">In <span class="arrow"></span></th>
          <th data-col="output_chars">Out <span class="arrow"></span></th>
          <th data-col="text_changed">Changed <span class="arrow"></span></th>
          <th data-col="tokens.total">Tokens <span class="arrow"></span></th>
          <th></th>
        </tr>
      </thead>
      <tbody id="tbody"></tbody>
    </table>
  </div>

  <footer id="footer"></footer>
</div>

<script type="application/json" id="log-data">%%DATA%%</script>
<script type="application/json" id="log-stats">%%STATS%%</script>
<script>
const DATA = JSON.parse(document.getElementById('log-data').textContent);
const STATS = JSON.parse(document.getElementById('log-stats').textContent);

// --- Stats ---
(function() {
  const s = STATS, el = document.getElementById('stats'), sub = document.getElementById('headerSub');
  if (s.total === 0) {
    el.innerHTML = '<div class="stat-card"><div class="stat-value">0</div><div class="stat-label">Entries</div></div>';
    sub.textContent = 'No entries recorded';
    return;
  }
  sub.textContent = s.total + ' spell-check runs recorded';
  el.innerHTML = `
    <div class="stat-card"><div class="stat-value">${s.total}</div><div class="stat-label">Total Runs</div></div>
    <div class="stat-card"><div class="stat-value green">${s.success_rate}<small>%</small></div><div class="stat-label">Success Rate</div></div>
    <div class="stat-card"><div class="stat-value ${s.errors?'red':''}">${s.errors}</div><div class="stat-label">Errors</div></div>
    <div class="stat-card"><div class="stat-value">${s.avg_duration}<small>ms</small></div><div class="stat-label">Avg Duration</div></div>
    <div class="stat-card"><div class="stat-value">${s.avg_api_time}<small>ms</small></div><div class="stat-label">Avg API Time</div></div>
    <div class="stat-card"><div class="stat-value">${s.total_tokens.toLocaleString()}</div><div class="stat-label">Total Tokens</div></div>
    <div class="stat-card"><div class="stat-value">${s.text_changed_pct}<small>%</small></div><div class="stat-label">Text Changed</div></div>`;
})();

// --- Filters ---
(function() {
  const models = [...new Set(DATA.map(e => e.model || ''))].filter(Boolean).sort();
  const apps = [...new Set(DATA.map(e => e.active_exe || ''))].filter(Boolean).sort();
  document.getElementById('filterModel').innerHTML = '<option value="">All Models</option>' + models.map(m => `<option value="${esc(m)}">${esc(m)}</option>`).join('');
  document.getElementById('filterApp').innerHTML = '<option value="">All Apps</option>' + apps.map(a => `<option value="${esc(a)}">${esc(a)}</option>`).join('');
})();

function esc(s) { const d = document.createElement('div'); d.textContent = s; return d.innerHTML; }
function trunc(s, n) { return s && s.length > n ? s.slice(0, n) + '\u2026' : (s || ''); }
function nested(obj, path) { return path.split('.').reduce((o, k) => (o && o[k] !== undefined) ? o[k] : 0, obj); }

let sortCol = 'timestamp', sortDir = -1;

// --- Generation metadata (used by stale check + footer) ---
const GENERATED_DATE = new Date('%%GENERATED_ISO%%');
const RERUN_CMD = '%%RERUN_CMD%%';

function getFiltered() {
  const search = document.getElementById('search').value.toLowerCase();
  const status = document.getElementById('filterStatus').value;
  const model = document.getElementById('filterModel').value;
  const app = document.getElementById('filterApp').value;
  const changed = document.getElementById('changedOnly').checked;
  return DATA.filter(e => {
    if (status && e.status !== status) return false;
    if (model && e.model !== model) return false;
    if (app && e.active_exe !== app) return false;
    if (changed && !e.text_changed) return false;
    if (search) { if (!JSON.stringify(e).toLowerCase().includes(search)) return false; }
    return true;
  }).sort((a, b) => {
    let va = nested(a, sortCol), vb = nested(b, sortCol);
    if (typeof va === 'string') return va.localeCompare(vb) * sortDir;
    return ((va || 0) - (vb || 0)) * sortDir;
  });
}

function renderTimingBar(t, total) {
  if (!t || total <= 0) return '';
  const parts = [
    ['tb-clip', t.clipboard_ms, 'Clipboard'], ['tb-payload', t.payload_ms, 'Payload'],
    ['tb-req', t.request_ms, 'Request'], ['tb-api', t.api_ms, 'API'],
    ['tb-parse', t.parse_ms, 'Parse'], ['tb-replace', t.replacements_ms, 'Replacements'],
    ['tb-guard', t.prompt_guard_ms, 'Guard'], ['tb-paste', t.paste_ms, 'Paste'],
  ];
  return '<div class="timing-bar">' + parts.map(([cls, ms, label]) => {
    const pct = Math.max((ms / total) * 100, 0);
    if (pct < 0.5) return '';
    return `<div class="${cls}" style="width:${pct}%" title="${label}: ${ms}ms">${pct > 8 ? ms + 'ms' : ''}</div>`;
  }).join('') + '</div>';
}

function renderDetail(e, idx) {
  const t = e.timings || {};
  const tok = e.tokens || {};
  const rep = e.replacements || {};
  const pl = e.prompt_leak || {};
  let rawReq = '';
  try { rawReq = e.raw_request ? JSON.stringify(JSON.parse(e.raw_request), null, 2) : ''; } catch { rawReq = e.raw_request || ''; }
  let rawResp = '';
  try { rawResp = e.raw_response ? JSON.stringify(JSON.parse(e.raw_response), null, 2) : ''; } catch { rawResp = e.raw_response || ''; }

  return `<div class="detail-content" id="detail-${idx}">
    <div class="detail-grid">
      <div class="detail-section"><h4>Timing Breakdown</h4>${renderTimingBar(t, e.duration_ms)}
        <div class="kv">
          <span class="k">Clipboard</span><span class="v">${t.clipboard_ms || 0}ms</span>
          <span class="k">Payload prep</span><span class="v">${t.payload_ms || 0}ms</span>
          <span class="k">Request setup</span><span class="v">${t.request_ms || 0}ms</span>
          <span class="k">API round-trip</span><span class="v">${t.api_ms || 0}ms</span>
          <span class="k">Response parse</span><span class="v">${t.parse_ms || 0}ms</span>
          <span class="k">Replacements</span><span class="v">${t.replacements_ms || 0}ms</span>
          <span class="k">Prompt guard</span><span class="v">${t.prompt_guard_ms || 0}ms</span>
          <span class="k">Paste</span><span class="v">${t.paste_ms || 0}ms</span>
        </div>
      </div>
      <div class="detail-section"><h4>Tokens</h4>
        <div class="kv">
          <span class="k">Input</span><span class="v">${tok.input || 0}</span>
          <span class="k">Output</span><span class="v">${tok.output || 0}</span>
          <span class="k">Total</span><span class="v">${tok.total || 0}</span>
          <span class="k">Cached</span><span class="v">${tok.cached || 0}</span>
          <span class="k">Reasoning</span><span class="v">${tok.reasoning || 0}</span>
        </div>
      </div>
    </div>
    <div class="detail-section"><h4>Input Text</h4><pre>${esc(e.input_text || '')}</pre></div>
    <div class="detail-section"><h4>AI Output (before post-processing)</h4><pre>${esc(e.raw_ai_output || '')}</pre></div>
    <div class="detail-section"><h4>Final Output</h4><pre>${esc(e.output_text || '')}</pre></div>
    ${rep.count > 0 ? `<div class="detail-section"><h4>Replacements (${rep.count})</h4><pre>${esc((rep.applied || []).join('\n'))}</pre></div>` : ''}
    ${rep.urls_protected > 0 ? `<div class="detail-section"><h4>URLs Protected</h4><span class="v">${rep.urls_protected}</span></div>` : ''}
    ${pl.triggered ? `<div class="detail-section"><h4>Prompt Leak Guard</h4>
      <div class="kv">
        <span class="k">Occurrences</span><span class="v">${pl.occurrences}</span>
        <span class="k">Text input removed</span><span class="v">${pl.text_input_removed}</span>
        <span class="k">Chars removed</span><span class="v">${pl.removed_chars}</span>
        <span class="k">Length</span><span class="v">${pl.before_length} \u2192 ${pl.after_length}</span>
      </div></div>` : ''}
    <div class="detail-section"><h4>Events</h4><pre>${esc((e.events || []).join('\n'))}</pre></div>
    ${e.error ? `<div class="detail-section"><h4>Error</h4><pre>${esc(e.error)}</pre></div>` : ''}
    ${rawReq ? `<div class="detail-section"><h4>Raw API Request</h4><pre>${esc(rawReq)}</pre></div>` : ''}
    ${rawResp ? `<div class="detail-section"><h4>Raw API Response</h4><pre>${esc(rawResp)}</pre></div>` : ''}
    <div class="detail-section">
      <div class="kv">
        <span class="k">Model version</span><span class="v">${esc(e.model_version || '')}</span>
        <span class="k">Active app</span><span class="v">${esc(e.active_app || '')}</span>
        <span class="k">Active exe</span><span class="v">${esc(e.active_exe || '')}</span>
        <span class="k">Paste method</span><span class="v">${esc(e.paste_method || '')}</span>
      </div>
    </div>
  </div>`;
}

function render() {
  const filtered = getFiltered();
  document.getElementById('tbody').innerHTML = filtered.map((e, i) => `
    <tr>
      <td class="col-ts">${esc(e.timestamp || '')}</td>
      <td class="${e.status === 'SUCCESS' ? 'status-ok' : 'status-err'}">${esc(e.status || '')}</td>
      <td>${e.duration_ms || 0}ms</td>
      <td>${esc(e.model || '')}</td>
      <td title="${esc(e.active_app || '')}">${esc(trunc(e.active_exe || '', 20))}</td>
      <td>${e.input_chars || 0}</td>
      <td>${e.output_chars || 0}</td>
      <td><span class="dot ${e.text_changed ? 'dot-yes' : 'dot-no'}" title="${e.text_changed ? 'Yes' : 'No'}" aria-label="${e.text_changed ? 'Changed' : 'Unchanged'}"></span></td>
      <td>${(e.tokens || {}).total || 0}</td>
      <td><button class="expand-btn" onclick="toggle(${i})">Details</button></td>
    </tr>
    <tr class="detail-row"><td colspan="10">${renderDetail(e, i)}</td></tr>
  `).join('');
  document.getElementById('footer').textContent = `Showing ${filtered.length} of ${DATA.length} entries \u00B7 Generated ${GENERATED_DATE.toLocaleString()}`;
  document.querySelectorAll('thead th').forEach(th => {
    const arrow = th.querySelector('.arrow');
    if (!arrow) return;
    if (th.dataset.col === sortCol) arrow.textContent = sortDir === 1 ? '\u25B2' : '\u25BC';
    else arrow.textContent = '';
  });
}

function toggle(idx) { const el = document.getElementById('detail-' + idx); if (el) el.classList.toggle('open'); }

document.querySelectorAll('thead th[data-col]').forEach(th => {
  th.addEventListener('click', () => {
    const col = th.dataset.col;
    if (sortCol === col) sortDir *= -1;
    else { sortCol = col; sortDir = col === 'timestamp' ? -1 : 1; }
    render();
  });
});

['search', 'filterStatus', 'filterModel', 'filterApp', 'changedOnly'].forEach(id => {
  document.getElementById(id).addEventListener(id === 'changedOnly' ? 'change' : 'input', render);
});

// --- Stale data check ---
(function() {
  const now = new Date();
  const ageHours = (now - GENERATED_DATE) / 3600000;
  if (ageHours >= 1) {
    let ageText;
    if (ageHours < 24) {
      const h = Math.round(ageHours);
      ageText = h + ' hour' + (h !== 1 ? 's' : '') + ' ago';
    } else {
      const d = Math.round(ageHours / 24);
      ageText = d + ' day' + (d !== 1 ? 's' : '') + ' ago';
    }
    document.getElementById('staleTitle').textContent = 'This viewer was generated ' + ageText;
    document.getElementById('rerunCmd').textContent = RERUN_CMD;
    document.getElementById('staleBanner').style.display = 'flex';
  }
})();

function copyCmd() {
  var btn = document.querySelector('.copy-btn');
  function done() { btn.textContent = 'Copied!'; setTimeout(function() { btn.textContent = 'Copy'; }, 2000); }
  if (navigator.clipboard && window.isSecureContext) {
    navigator.clipboard.writeText(RERUN_CMD).then(done);
  } else {
    var ta = document.createElement('textarea');
    ta.value = RERUN_CMD;
    ta.style.position = 'fixed';
    ta.style.opacity = '0';
    document.body.appendChild(ta);
    ta.select();
    document.execCommand('copy');
    document.body.removeChild(ta);
    done();
  }
}

render();
</script>
</body>
</html>"""


def main():
    print(f"Reading JSONL files from: {LOGS_DIR}")
    rename_migration = migrate_old_weekly_log_names()
    if rename_migration["renamed"] or rename_migration["skipped"]:
        print(
            "  Updated weekly filenames:"
            f" renamed {len(rename_migration['renamed'])},"
            f" skipped {len(rename_migration['skipped'])}"
        )

    migration = migrate_legacy_log()
    if migration:
        print(
            "  Migrated legacy log:"
            f" {migration['migrated']} entries into weekly files,"
            f" skipped {migration['skipped']},"
            f" backup: {migration['backup'].name}"
        )

    entries = load_entries()
    print(f"  Loaded {len(entries)} log entries")

    if not entries:
        print("  No entries found. Run the spell checker first to generate logs.")
        return

    stats = compute_stats(entries)

    # Build HTML with embedded data
    data_json = json.dumps(entries, ensure_ascii=False).replace("<", "\\u003c")
    stats_json = json.dumps(stats, ensure_ascii=False).replace("<", "\\u003c")
    generated_iso = datetime.now().isoformat()
    rerun_cmd = f'cd "{SCRIPT_DIR}" && python generate_log_viewer.py'
    rerun_cmd_js = rerun_cmd.replace("\\", "\\\\")
    html_out = (HTML_TEMPLATE
        .replace("%%DATA%%", data_json)
        .replace("%%STATS%%", stats_json)
        .replace("%%GENERATED_ISO%%", generated_iso)
        .replace("%%RERUN_CMD%%", rerun_cmd_js)
    )

    OUTPUT_FILE.parent.mkdir(parents=True, exist_ok=True)
    with open(OUTPUT_FILE, "w", encoding="utf-8") as f:
        f.write(html_out)

    size_mb = OUTPUT_FILE.stat().st_size / (1024 * 1024)
    print(f"  Generated: {OUTPUT_FILE} ({size_mb:.1f} MB)")

    if "--no-open" not in sys.argv:
        import webbrowser
        webbrowser.open(str(OUTPUT_FILE))
        print("  Opened in browser")


if __name__ == "__main__":
    main()
