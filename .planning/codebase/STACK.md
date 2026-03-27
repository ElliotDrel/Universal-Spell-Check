# Technology Stack

**Analysis Date:** 2026-03-27

## Languages

**Primary:**
- AutoHotkey v2.0 - Main implementation language for spell-checking automation
- Python 3.x - Log viewer HTML generation tool

## Runtime

**Environment:**
- Windows (script runs as AutoHotkey v2.0 interpreter)
- Python 3.x runtime for log viewer tool

**Package Manager:**
- Python pip (implicit) - Python standard library only, no external dependencies

## Frameworks

**Core:**
- AutoHotkey v2.0 - Hotkey scripting framework with COM integration

**Logging & Utilities:**
- Custom JSON Lines (JSONL) logging system - Structured log format stored in `logs/spellcheck.jsonl`
- Python built-in libraries (json, glob, pathlib, html, os, sys) for log viewer generation

**Build/Dev:**
- Python built-in web templating (no external template library) - `generate_log_viewer.py` generates static HTML

## Key Dependencies

**Critical:**
- Windows COM Objects - ADODB.Stream (UTF-8 response reading), HTMLFile (HTML to plaintext conversion), WinHttp.WinHttpRequest.5.1 (HTTP requests)

**Infrastructure:**
- OpenAI API client - Direct HTTP/1.1 via WinHttp.WinHttpRequest (no SDK)
- Clipboard API - Windows native clipboard format handling (CF_HTML, CF_UNICODETEXT, CF_TEXT)
- Process/Window API - WinGetTitle, WinGetProcessName for active application tracking

## Configuration

**Environment:**
- API Key: Hardcoded in script (`.ahk` file line 877) - "sk-proj-..." format
- Model selection: Top-level `modelModule` variable (line 18) - supports "gpt-4.1", "gpt-5.1", "gpt-5-mini"
- Per-app paste behavior: `sendTextApps` array (line 64) configurable for keystroke typing override

**Build:**
- No build process - AutoHotkey scripts run directly
- Python viewer: Run `python generate_log_viewer.py` to generate `logs/viewer.html`
- Log format: JSONL (UTF-8, BOM-aware reading)

## Platform Requirements

**Development:**
- Windows 11 Pro (tested) - works on Windows 8+
- AutoHotkey v2.0+ (interpreter installed)
- Python 3.x for log viewer generation

**Production:**
- Windows 8+ (script uses WinHTTP which available on all modern Windows)
- OpenAI API key with access to Responses API
- Network connectivity for API calls to `https://api.openai.com/v1/responses`

## Special Notes

**API Integration Type:**
- Direct HTTP via Windows native COM (no SDK dependency)
- Responses API endpoint (not Chat Completions)
- Request timeout: 30 seconds

**Supported Models:**
- gpt-4.1 (standard, uses temperature parameter)
- gpt-5.1 (reasoning, uses reasoning.effort="none")
- gpt-5-mini (reasoning, uses reasoning.effort="minimal")

**Logging:**
- Max single log file: 1MB before rotation
- Archive format: `spellcheck-YYYY-MM-dd-HHmmss.jsonl`
- Fields: 30+ metrics including timings breakdown, token counts, clipboard formats, active app tracking

---

*Stack analysis: 2026-03-27*
