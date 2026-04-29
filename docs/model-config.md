# Model Configuration & API Integration

**Endpoint (all models):** `https://api.openai.com/v1/responses`
**Timeout:** 30s (set on `HttpClient` in `OpenAiSpellcheckService`)

## Current caller

`src/OpenAiSpellcheckService.cs` — persistent app-lifetime `HttpClient`, sends directly to the Responses API. Current model: `gpt-4.1` (hardcoded as `OpenAiSpellcheckService.Model`). No model-selection UI exists yet.

## API key storage

Saved through `SettingsStore` using DPAPI `DataProtectionScope.CurrentUser` into `%LocalAppData%\{BuildChannel.AppDataFolder}\apikey.dat`. Never written to `settings.json` or any plain-text file. Prod and Dev keys are isolated by channel (`AppDataFolder` differs).

---

## Standard vs. Reasoning models — do not mix parameters

| Feature | Standard (`gpt-4.1`) | Reasoning (`gpt-5.1`, `gpt-5-mini`, `o1`, `o3`) |
|---|---|---|
| `temperature` | Yes | **No — API error if present** |
| `top_p`, penalties, `logprobs` | Yes | No |
| `reasoning` block | No | Yes |
| `text.verbosity` | `"medium"` | `"low"` |
| API shape | Responses API | Responses API |

This is a hard rule (see `CLAUDE.md` §2). Reasoning models return a 4xx API error when `temperature` is sent — always verify parameter compatibility, not just model name.

---

## Current payload — `gpt-4.1`

```json
{
  "model": "gpt-4.1",
  "input": [{"role": "user", "content": [{"type": "input_text", "text": "..."}]}],
  "store": true,
  "text": {"verbosity": "medium"},
  "temperature": 0.3
}
```

Prompt shape sent as `input[0].content[0].text`:

```
instructions: <PromptInstruction>
text input: <selected text>
```

`PromptInstruction` is the constant string in `OpenAiSpellcheckService.PromptInstruction`. `TextPostProcessor` strips echoed `instructions:`/`text input:` text before paste.

---

## Reasoning model payloads (reference — not currently wired)

**gpt-5.1**
```json
{"model": "gpt-5.1", "input": [...], "store": true, "text": {"verbosity": "low"}, "reasoning": {"effort": "none", "summary": "auto"}}
```

**gpt-5-mini**
```json
{"model": "gpt-5-mini", "input": [...], "store": true, "text": {"verbosity": "low"}, "reasoning": {"effort": "minimal", "summary": "auto"}}
```

- `effort: "minimal"` is unique to gpt-5-mini.
- Correct shape is `reasoning: { effort: ... }`, NOT `reasoning_effort`.
- `store: true` required for all models.

---

## Response parsing

`OpenAiSpellcheckService.ExtractOutputText` tries two paths:
1. Top-level `output_text` string property (fast path).
2. Walk `output[].content[]` looking for `type == "output_text"` and a `text` property.

On parse failure (`output_text` empty or absent): logs `parse_failed empty_output_text`, returns `SpellcheckResult.Fail(ParseFailed, ...)`.

---

## Migration checklist when switching models

1. Model name/identifier correct.
2. Endpoint correct for the family.
3. **Every request parameter supported by this model type** — not just the version. Remove `temperature` for reasoning models; add `reasoning` block.
4. Response shape compatible with `ExtractOutputText`.
5. `text.verbosity` correct: `"medium"` for standard, `"low"` for reasoning.
6. Test with a real sample request before declaring done.

For any model change, also verify:
- Persistent `HttpClient` remains app-lifetime; do not recreate it per hotkey press.
- Failure paths still return a `SpellcheckResult` and do not paste over the user's selected text.
- Logs include `request_duration_ms`, `request_attempts`, `status_code`, and `error_code` for failures.
- Post-processing (`TextPostProcessor`) still runs after successful model output and before paste.
