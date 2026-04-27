# Model Configuration & API Integration

**Endpoint (all models):** `https://api.openai.com/v1/responses`
**Timeout:** 30s

## Current callers

- **AHK production app:** `Universal Spell Checker.ahk` sends requests through the required local Python proxy at `http://127.0.0.1:48080/v1/responses`; the proxy forwards to the Responses API.
- **Native main app:** `native/UniversalSpellCheck/OpenAiSpellcheckService.cs` sends directly to `https://api.openai.com/v1/responses` with one app-lifetime `HttpClient`. It currently uses fixed `gpt-4.1` only; no native model-selection UI exists yet.

## API key storage

- **AHK:** reads `OPENAI_API_KEY` once at startup from the repo environment flow.
- **Native:** saves the key through `SettingsStore` into `%LOCALAPPDATA%\UniversalSpellCheck\apikey.dat` using DPAPI `DataProtectionScope.CurrentUser`; the key is not written to `settings.json`.

## Standard vs Reasoning models — do not mix parameters

| Feature | Standard (gpt-4.1) | Reasoning (gpt-5.1, gpt-5-mini, o1, o3) |
|---|---|---|
| `temperature` | Yes | **No — will error** |
| `top_p`, penalties, `logprobs`, `logit_bias` | Yes | No |
| `reasoning` block | No | Yes |
| `verbosity` | `"medium"` (no `"low"`) | `"low"` |
| API shape | Responses API, same endpoint | Responses API |

## Per-model payloads

**gpt-4.1**
```json
{"model":"gpt-4.1","input":[...],"store":true,"text":{"verbosity":"medium"},"temperature":0.3}
```

Native prompt shape for `gpt-4.1` mirrors the AHK prompt guard target:

```text
instructions: <PromptInstruction>
text input: <selected text>
```

`PromptInstruction` lives in `OpenAiSpellcheckService.PromptInstruction`, and `TextPostProcessor` removes leaked `instructions:` / `text input:` text before paste.

**gpt-5.1**
```json
{"model":"gpt-5.1","input":[...],"store":true,"text":{"verbosity":"low"},"reasoning":{"effort":"none","summary":"auto"}}
```

**gpt-5-mini**
```json
{"model":"gpt-5-mini","input":[...],"store":true,"text":{"verbosity":"low"},"reasoning":{"effort":"minimal","summary":"auto"}}
```

- `effort: "minimal"` is unique to gpt-5-mini.
- `input` shape: `[{role:"user", content:[{type:"input_text", text:"..."}]}]`
- `store: true` required for all models.
- Correct shape is `reasoning: { effort: ... }`, NOT `reasoning_effort`.

## Migration checklist when switching models
1. Model name/identifier correct.
2. Endpoint correct for the family.
3. **Every request parameter supported by this model TYPE** (not just the version).
4. Response shape compatible.
5. Test with a sample request before declaring done.

For native model work, also verify:
1. Persistent `HttpClient` remains app-lifetime; do not recreate it per hotkey press.
2. Failure paths still return a `SpellcheckResult` and do not paste over the user's selected text.
3. Native logs include `request_duration_ms`, `request_attempts`, `status_code`, and `error_code` for failures.
4. Post-processing still runs after successful model output and before paste.

Reasoning models return API errors on unsupported params — always verify parameter compatibility, not just model name.
