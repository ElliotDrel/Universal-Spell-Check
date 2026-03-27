#!/usr/bin/env python3
"""Generate an HTML log viewer from JSONL spell-check logs.

Usage:
    python generate_log_viewer.py          # reads logs/*.jsonl, writes logs/viewer.html
    python generate_log_viewer.py --open   # same, then opens in default browser
"""

import json
import glob
import os
import sys
import html
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
LOGS_DIR = SCRIPT_DIR / "logs"
OUTPUT_FILE = LOGS_DIR / "viewer.html"


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
    api_times = [e.get("timings", {}).get("api_ms", 0) for e in entries if e.get("status") == "SUCCESS"]
    total_tokens = sum(e.get("tokens", {}).get("total", 0) for e in entries)
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
<title>Spell Check Log Viewer</title>
<style>
  :root {
    --bg: #0d1117; --surface: #161b22; --border: #30363d;
    --text: #e6edf3; --text-dim: #8b949e; --accent: #58a6ff;
    --green: #3fb950; --red: #f85149; --yellow: #d29922;
  }
  * { box-sizing: border-box; margin: 0; padding: 0; }
  body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Helvetica, Arial, sans-serif;
         background: var(--bg); color: var(--text); padding: 20px; line-height: 1.5; }
  h1 { font-size: 1.4em; margin-bottom: 16px; }
  .stats { display: flex; gap: 12px; flex-wrap: wrap; margin-bottom: 20px; }
  .stat-card { background: var(--surface); border: 1px solid var(--border);
               border-radius: 8px; padding: 12px 16px; min-width: 140px; }
  .stat-card .label { font-size: 0.75em; color: var(--text-dim); text-transform: uppercase; letter-spacing: 0.5px; }
  .stat-card .value { font-size: 1.5em; font-weight: 600; }
  .stat-card .value.green { color: var(--green); }
  .stat-card .value.red { color: var(--red); }

  .controls { display: flex; gap: 10px; margin-bottom: 16px; flex-wrap: wrap; align-items: center; }
  .controls input, .controls select { background: var(--surface); color: var(--text);
    border: 1px solid var(--border); border-radius: 6px; padding: 6px 10px; font-size: 0.9em; }
  .controls input { flex: 1; min-width: 200px; }
  .controls select { min-width: 120px; }
  .controls label { font-size: 0.85em; color: var(--text-dim); }

  table { width: 100%; border-collapse: collapse; font-size: 0.85em; }
  thead th { background: var(--surface); position: sticky; top: 0; padding: 8px 10px;
             text-align: left; border-bottom: 2px solid var(--border); cursor: pointer;
             user-select: none; white-space: nowrap; }
  thead th:hover { color: var(--accent); }
  thead th .arrow { margin-left: 4px; font-size: 0.7em; }
  tbody tr { border-bottom: 1px solid var(--border); }
  tbody tr:hover { background: rgba(88, 166, 255, 0.05); }
  td { padding: 6px 10px; vertical-align: top; max-width: 300px; overflow: hidden;
       text-overflow: ellipsis; white-space: nowrap; }
  td.wrap { white-space: normal; word-break: break-word; }

  .status-ok { color: var(--green); font-weight: 600; }
  .status-err { color: var(--red); font-weight: 600; }
  .changed-yes { color: var(--green); }
  .changed-no { color: var(--text-dim); }

  .expand-btn { background: none; border: 1px solid var(--border); color: var(--accent);
                border-radius: 4px; padding: 2px 8px; cursor: pointer; font-size: 0.8em; }
  .expand-btn:hover { background: rgba(88, 166, 255, 0.1); }

  .detail-row td { padding: 0; }
  .detail-content { background: var(--surface); padding: 16px; display: none; }
  .detail-content.open { display: block; }
  .detail-section { margin-bottom: 14px; }
  .detail-section h4 { font-size: 0.85em; color: var(--accent); margin-bottom: 4px; }
  .detail-section pre { background: #0d1117; border: 1px solid var(--border);
    border-radius: 4px; padding: 10px; overflow-x: auto; white-space: pre-wrap;
    word-break: break-word; font-size: 0.82em; max-height: 300px; overflow-y: auto; }
  .detail-section .kv { display: grid; grid-template-columns: 160px 1fr; gap: 2px 12px; font-size: 0.85em; }
  .detail-section .kv .k { color: var(--text-dim); }

  .timing-bar { display: flex; height: 20px; border-radius: 4px; overflow: hidden; margin: 4px 0; }
  .timing-bar > div { height: 100%; display: flex; align-items: center; justify-content: center;
    font-size: 0.7em; color: #fff; min-width: 2px; }
  .tb-clip { background: #1f6feb; } .tb-payload { background: #388bfd; }
  .tb-req { background: #58a6ff; } .tb-api { background: #d29922; }
  .tb-parse { background: #3fb950; } .tb-replace { background: #2ea043; }
  .tb-guard { background: #8b949e; } .tb-paste { background: #6e7681; }

  .count-badge { display: inline-block; background: var(--border); border-radius: 10px;
    padding: 0 6px; font-size: 0.75em; margin-left: 4px; }
  footer { margin-top: 24px; text-align: center; font-size: 0.8em; color: var(--text-dim); }

  @media (max-width: 900px) {
    td { font-size: 0.78em; padding: 4px 6px; }
  }
</style>
</head>
<body>
<h1>Spell Check Log Viewer</h1>

<div class="stats" id="stats"></div>

<div class="controls">
  <input type="text" id="search" placeholder="Search text, app, error...">
  <select id="filterStatus"><option value="">All Status</option><option value="SUCCESS">Success</option><option value="ERROR">Error</option></select>
  <select id="filterModel"></select>
  <select id="filterApp"></select>
  <label><input type="checkbox" id="changedOnly"> Changed only</label>
</div>

<table>
  <thead>
    <tr>
      <th data-col="timestamp">Timestamp <span class="arrow"></span></th>
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

<footer id="footer"></footer>

<script>
const DATA = %%DATA%%;
const STATS = %%STATS%%;

// --- Render stats ---
(function() {
  const s = STATS;
  const el = document.getElementById('stats');
  if (s.total === 0) { el.innerHTML = '<div class="stat-card"><div class="label">Entries</div><div class="value">0</div></div>'; return; }
  el.innerHTML = `
    <div class="stat-card"><div class="label">Total Runs</div><div class="value">${s.total}</div></div>
    <div class="stat-card"><div class="label">Success Rate</div><div class="value green">${s.success_rate}%</div></div>
    <div class="stat-card"><div class="label">Errors</div><div class="value ${s.errors?'red':''}">${s.errors}</div></div>
    <div class="stat-card"><div class="label">Avg Duration</div><div class="value">${s.avg_duration}ms</div></div>
    <div class="stat-card"><div class="label">Avg API Time</div><div class="value">${s.avg_api_time}ms</div></div>
    <div class="stat-card"><div class="label">Total Tokens</div><div class="value">${s.total_tokens.toLocaleString()}</div></div>
    <div class="stat-card"><div class="label">Text Changed</div><div class="value">${s.text_changed_pct}%</div></div>
  `;
})();

// --- Populate filters ---
(function() {
  const models = [...new Set(DATA.map(e => e.model || ''))].filter(Boolean).sort();
  const apps = [...new Set(DATA.map(e => e.active_exe || ''))].filter(Boolean).sort();
  const mSel = document.getElementById('filterModel');
  mSel.innerHTML = '<option value="">All Models</option>' + models.map(m => `<option value="${esc(m)}">${esc(m)}</option>`).join('');
  const aSel = document.getElementById('filterApp');
  aSel.innerHTML = '<option value="">All Apps</option>' + apps.map(a => `<option value="${esc(a)}">${esc(a)}</option>`).join('');
})();

function esc(s) { const d = document.createElement('div'); d.textContent = s; return d.innerHTML; }
function trunc(s, n) { return s && s.length > n ? s.slice(0, n) + '...' : (s || ''); }
function nested(obj, path) { return path.split('.').reduce((o, k) => (o && o[k] !== undefined) ? o[k] : 0, obj); }

let sortCol = 'timestamp', sortDir = -1;

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
    if (search) {
      const blob = JSON.stringify(e).toLowerCase();
      if (!blob.includes(search)) return false;
    }
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
    ['tb-clip', t.clipboard_ms, 'Clip'],
    ['tb-payload', t.payload_ms, 'Prep'],
    ['tb-req', t.request_ms, 'Req'],
    ['tb-api', t.api_ms, 'API'],
    ['tb-parse', t.parse_ms, 'Parse'],
    ['tb-replace', t.replacements_ms, 'Repl'],
    ['tb-guard', t.prompt_guard_ms, 'Guard'],
    ['tb-paste', t.paste_ms, 'Paste'],
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
    <div class="detail-section"><h4>Timing Breakdown</h4>${renderTimingBar(t, e.duration_ms)}
      <div class="kv">
        <span class="k">Clipboard</span><span>${t.clipboard_ms || 0}ms</span>
        <span class="k">Payload prep</span><span>${t.payload_ms || 0}ms</span>
        <span class="k">Request setup</span><span>${t.request_ms || 0}ms</span>
        <span class="k">API round-trip</span><span>${t.api_ms || 0}ms</span>
        <span class="k">Response parse</span><span>${t.parse_ms || 0}ms</span>
        <span class="k">Replacements</span><span>${t.replacements_ms || 0}ms</span>
        <span class="k">Prompt guard</span><span>${t.prompt_guard_ms || 0}ms</span>
        <span class="k">Paste</span><span>${t.paste_ms || 0}ms</span>
      </div>
    </div>
    <div class="detail-section"><h4>Tokens</h4>
      <div class="kv">
        <span class="k">Input</span><span>${tok.input || 0}</span>
        <span class="k">Output</span><span>${tok.output || 0}</span>
        <span class="k">Total</span><span>${tok.total || 0}</span>
        <span class="k">Cached</span><span>${tok.cached || 0}</span>
        <span class="k">Reasoning</span><span>${tok.reasoning || 0}</span>
      </div>
    </div>
    <div class="detail-section"><h4>Input Text</h4><pre>${esc(e.input_text || '')}</pre></div>
    <div class="detail-section"><h4>AI Output (before post-processing)</h4><pre>${esc(e.raw_ai_output || '')}</pre></div>
    <div class="detail-section"><h4>Final Output</h4><pre>${esc(e.output_text || '')}</pre></div>
    ${rep.count > 0 ? `<div class="detail-section"><h4>Replacements (${rep.count})</h4><pre>${esc((rep.applied || []).join('\n'))}</pre></div>` : ''}
    ${rep.urls_protected > 0 ? `<div class="detail-section"><h4>URLs Protected</h4><span>${rep.urls_protected}</span></div>` : ''}
    ${pl.triggered ? `<div class="detail-section"><h4>Prompt Leak Guard</h4>
      <div class="kv">
        <span class="k">Occurrences</span><span>${pl.occurrences}</span>
        <span class="k">Text input removed</span><span>${pl.text_input_removed}</span>
        <span class="k">Chars removed</span><span>${pl.removed_chars}</span>
        <span class="k">Length</span><span>${pl.before_length} → ${pl.after_length}</span>
      </div></div>` : ''}
    <div class="detail-section"><h4>Events</h4><pre>${esc((e.events || []).join('\n'))}</pre></div>
    ${e.error ? `<div class="detail-section"><h4>Error</h4><pre>${esc(e.error)}</pre></div>` : ''}
    ${rawReq ? `<div class="detail-section"><h4>Raw API Request</h4><pre>${esc(rawReq)}</pre></div>` : ''}
    ${rawResp ? `<div class="detail-section"><h4>Raw API Response</h4><pre>${esc(rawResp)}</pre></div>` : ''}
    <div class="detail-section">
      <div class="kv">
        <span class="k">Model version</span><span>${esc(e.model_version || '')}</span>
        <span class="k">Active app</span><span>${esc(e.active_app || '')}</span>
        <span class="k">Active exe</span><span>${esc(e.active_exe || '')}</span>
        <span class="k">Paste method</span><span>${esc(e.paste_method || '')}</span>
      </div>
    </div>
  </div>`;
}

function render() {
  const filtered = getFiltered();
  const tbody = document.getElementById('tbody');
  tbody.innerHTML = filtered.map((e, i) => `
    <tr>
      <td>${esc(e.timestamp || '')}</td>
      <td class="${e.status === 'SUCCESS' ? 'status-ok' : 'status-err'}">${esc(e.status || '')}</td>
      <td>${e.duration_ms || 0}ms</td>
      <td>${esc(e.model || '')}</td>
      <td title="${esc(e.active_app || '')}">${esc(trunc(e.active_exe || '', 20))}</td>
      <td>${e.input_chars || 0}</td>
      <td>${e.output_chars || 0}</td>
      <td class="${e.text_changed ? 'changed-yes' : 'changed-no'}">${e.text_changed ? 'Yes' : 'No'}</td>
      <td>${(e.tokens || {}).total || 0}</td>
      <td><button class="expand-btn" onclick="toggle(${i})">Details</button></td>
    </tr>
    <tr class="detail-row"><td colspan="10">${renderDetail(e, i)}</td></tr>
  `).join('');
  document.getElementById('footer').textContent = `Showing ${filtered.length} of ${DATA.length} entries`;

  // Update sort arrows
  document.querySelectorAll('thead th').forEach(th => {
    const arrow = th.querySelector('.arrow');
    if (!arrow) return;
    if (th.dataset.col === sortCol) arrow.textContent = sortDir === 1 ? '▲' : '▼';
    else arrow.textContent = '';
  });
}

function toggle(idx) {
  const el = document.getElementById('detail-' + idx);
  if (el) el.classList.toggle('open');
}

// Sort on header click
document.querySelectorAll('thead th[data-col]').forEach(th => {
  th.addEventListener('click', () => {
    const col = th.dataset.col;
    if (sortCol === col) sortDir *= -1;
    else { sortCol = col; sortDir = col === 'timestamp' ? -1 : 1; }
    render();
  });
});

// Filter events
['search', 'filterStatus', 'filterModel', 'filterApp', 'changedOnly'].forEach(id => {
  document.getElementById(id).addEventListener(id === 'changedOnly' ? 'change' : 'input', render);
});

render();
</script>
</body>
</html>"""


def main():
    print(f"Reading JSONL files from: {LOGS_DIR}")
    entries = load_entries()
    print(f"  Loaded {len(entries)} log entries")

    if not entries:
        print("  No entries found. Run the spell checker first to generate logs.")
        return

    stats = compute_stats(entries)

    # Build HTML with embedded data
    data_json = json.dumps(entries, ensure_ascii=False)
    stats_json = json.dumps(stats, ensure_ascii=False)
    html_out = HTML_TEMPLATE.replace("%%DATA%%", data_json).replace("%%STATS%%", stats_json)

    OUTPUT_FILE.parent.mkdir(parents=True, exist_ok=True)
    with open(OUTPUT_FILE, "w", encoding="utf-8") as f:
        f.write(html_out)

    size_mb = OUTPUT_FILE.stat().st_size / (1024 * 1024)
    print(f"  Generated: {OUTPUT_FILE} ({size_mb:.1f} MB)")

    if "--open" in sys.argv:
        import webbrowser
        webbrowser.open(str(OUTPUT_FILE))
        print("  Opened in browser")


if __name__ == "__main__":
    main()
