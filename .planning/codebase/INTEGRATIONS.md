# External Integrations

**Analysis Date:** 2026-03-27

## APIs & External Services

**OpenAI Responses API:**
- Service: OpenAI Responses API
  - What it's used for: Grammar/spelling correction via AI models
  - Endpoint: `https://api.openai.com/v1/responses` (line 50)
  - HTTP Method: POST
  - SDK/Client: Windows WinHttp.WinHttpRequest.5.1 COM object
  - Auth: Bearer token in Authorization header
  - Timeout: 30 seconds (5s connect, 5s send, 30s receive, 30s total)

## Data Storage

**Databases:**
- None - local file-based logging only

**File Storage:**
- Local filesystem only
  - Log location: `logs/spellcheck.jsonl` - JSONL format (1 JSON object per line)
  - Config location: `replacements.json` - JSON replacement rules
  - Viewer output: `logs/viewer.html` - Generated HTML viewer
  - Log archive: `logs/spellcheck-YYYY-MM-dd-HHmmss.jsonl` - Rotated logs

**Caching:**
- None - fresh API call per spell-check request

## Authentication & Identity

**Auth Provider:**
- OpenAI API Key (custom)
  - Implementation: Bearer token in `Authorization: Bearer sk-proj-...` header
  - Key location: Hardcoded in script (`Universal Spell Checker.ahk` line 877)
  - Key format: `sk-proj-` prefix followed by base64/alphanumeric characters and underscores
  - Scope: Full access to Responses API for specified models

## Monitoring & Observability

**Error Tracking:**
- None (external) - errors logged locally to JSONL

**Logs:**
- Custom JSONL logging system
  - Location: `logs/spellcheck.jsonl`
  - Entry fields:
    - timestamp, status (SUCCESS/ERROR), error message
    - duration_ms (total time)
    - model, model_version
    - active_app, active_exe (window title and process name)
    - paste_method (clipboard+Ctrl+V or SendText)
    - text_changed (boolean)
    - input_text, input_chars, output_text, output_chars
    - raw_ai_output, raw_request, raw_response
    - tokens (input, output, total, cached, reasoning counts)
    - timings object with breakdown in ms: clipboard_ms, payload_ms, request_ms, api_ms, parse_ms, replacements_ms, prompt_guard_ms, paste_ms
    - replacements object: count, applied array, urls_protected count
    - prompt_leak object: triggered, occurrences, text_input_removed, removed_chars, before_length, after_length
    - events array (debug entries)
  - Rotation: At 1MB, archived with timestamp suffix
  - Format: UTF-8, JSONL (JSON Lines)
  - Viewer: Run `python generate_log_viewer.py` to generate interactive HTML dashboard

## CI/CD & Deployment

**Hosting:**
- Not applicable - local Windows desktop application

**CI Pipeline:**
- None - script runs directly when user presses Ctrl+Alt+U

**Distribution:**
- Local file: `Universal Spell Checker.ahk` (main entry point)
- Variant: `Universal Spell Checker - SEND TEXT instead of ctr+v.ahk` (legacy fallback, no logging/post-processing)

## Environment Configuration

**Required env vars:**
- None - all configuration is hardcoded in `.ahk` file or `replacements.json`

**Secrets location:**
- API Key: Hardcoded in `Universal Spell Checker.ahk` line 877 (NOT in .env file)

**Configurable in code:**
- `modelModule` (line 18): Switch between "gpt-4.1", "gpt-5.1", "gpt-5-mini"
- `enableLogging` (line 4): Toggle JSONL logging on/off
- `replacementsPath` (line 9): Path to `replacements.json`
- `sendTextApps` (line 64): Array of executable names for direct keystroke paste vs clipboard

## Webhooks & Callbacks

**Incoming:**
- None

**Outgoing:**
- None

## Request/Response Format

**OpenAI Responses API Request:**

Endpoint: POST `https://api.openai.com/v1/responses`

Standard Model (gpt-4.1):
```json
{
  "model": "gpt-4.1",
  "input": [
    {
      "role": "user",
      "content": [
        {
          "type": "input_text",
          "text": "instructions: Fix the grammar and spelling of the text below. Preserve all formatting, line breaks, and special characters. Do not add or remove any content. Return only the corrected text.\ntext input: [USER_TEXT]"
        }
      ]
    }
  ],
  "store": true,
  "text": {
    "verbosity": "medium"
  },
  "temperature": 0.3
}
```

Reasoning Models (gpt-5.1, gpt-5-mini):
```json
{
  "model": "gpt-5.1",
  "input": [
    {
      "role": "user",
      "content": [
        {
          "type": "input_text",
          "text": "instructions: Fix the grammar and spelling of the text below. Preserve all formatting, line breaks, and special characters. Do not add or remove any content. Return only the corrected text.\ntext input: [USER_TEXT]"
        }
      ]
    }
  ],
  "store": true,
  "text": {
    "verbosity": "low"
  },
  "reasoning": {
    "effort": "none",
    "summary": "auto"
  }
}
```

Note: gpt-5-mini uses `effort: "minimal"` instead of `"none"`.

**OpenAI Responses API Response:**

Success (HTTP 200):
```json
{
  "model": "gpt-4.1-2025-03-27",
  "output": [
    {
      "type": "output_text",
      "content": [
        {
          "type": "output_text",
          "text": "[CORRECTED_TEXT]"
        }
      ]
    }
  ],
  "input_tokens": 45,
  "output_tokens": 38,
  "total_tokens": 83,
  "cached_tokens": 0,
  "reasoning_tokens": 0
}
```

Error Response (non-200):
- Status code + StatusText returned to user
- Raw response body captured in log if available (first 1000 chars)

## Text Processing Flow

1. User selects text and presses Ctrl+Alt+U
2. Script clears clipboard and copies selection (Ctrl+C)
3. Clipboard text read (HTML → Unicode → ANSI fallback)
4. JSON payload built with model-specific parameters
5. POST request sent to OpenAI API with Bearer auth
6. Response received as UTF-8 (via ADODB.Stream to prevent mojibake)
7. Corrected text extracted via regex (primary) or Map-based JSON parser (fallback)
8. Post-processing replacements applied (from `replacements.json`)
9. Prompt-leak safeguard run (detects echoed instruction headers)
10. Text replaced in app via clipboard + Ctrl+V (or SendText for listed apps)
11. Full event chain logged to JSONL with timing breakdown

## Post-Processing System

**Replacement mapping file:** `replacements.json`

Format: JSON object with canonical → variants mapping

Example:
```json
{
  "GitHub": ["Git Hub", "git hub", "GitHUB", "GITHUB", "Github", "github"],
  "OpenAI": ["Open AI", "Open Ai", "open ai", "openai"]
}
```

Processing:
- Loaded fresh on every invocation (edits take effect immediately)
- Flattened to `[variant, canonical]` pairs
- Sorted longest-first (prevents substring interference)
- URLs (`http://`, `https://`) extracted to placeholders before replacements, then restored
- Case-sensitive matching via `StrCompare(..., true)` and `InStr(..., true)`
- Results logged with count and list of applied replacements

---

*Integration audit: 2026-03-27*
