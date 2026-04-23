# Model Configuration & API Integration

**Endpoint (all models):** `https://api.openai.com/v1/responses`
**Timeout:** 30s

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

Reasoning models return API errors on unsupported params — always verify parameter compatibility, not just model name.
