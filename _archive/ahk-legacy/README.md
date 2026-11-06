# Archived: AutoHotkey Legacy

Historical reference. Not built or invoked by the active product. The active product is the C# WinForms tray app under `src/`.

## Contents

| File / Folder | Original role |
|---|---|
| `Universal Spell Checker.ahk` | AutoHotkey v2 hotkey handler — the original Ctrl+Alt+U pipeline. Selected text, sent it to a local Python proxy, replaced the selection. |
| `spellcheck-server.pyw` | Local HTTP proxy that forwarded requests from the AHK script to the OpenAI Responses API. Required by the AHK script only. |
| `Old Spell Check Version Files/` | Earlier AHK and JS variants kept for git history. Not run. |
| `generate_log_viewer.py` | Script that built `logs/viewer.html` from the AHK JSONL logs. Superseded by the native app's dashboard. |
| `githooks/` | Pre-commit hook that bumped the AHK `scriptVersion` constant. No longer wired to the active code. |

## Why kept

History only. Anyone who needs to revive the AHK fallback path can do so by reading these files. Day-to-day development happens on the C# native app — see the root `README.md`.
