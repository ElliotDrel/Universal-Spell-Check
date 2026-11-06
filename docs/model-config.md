# Models and the API

All requests go to `https://api.openai.com/v1/responses` through the app-lifetime WinHTTP client
in `rust/src/http.rs`. Settings offers `gpt-4.1` (default) and `gpt-5.4-mini`; a change applies
to the next request. The payload is built by `core::payload` (and `Protection::payload` for the
T3 citation instruction).

## Payloads

The request text is always:

```
instructions: <core::PROMPT>
text input: <protected selection>
```

```json
{"model": "gpt-4.1", "input": [{"role": "user", "content": [{"type": "input_text", "text": "..."}]}],
 "store": true, "text": {"verbosity": "medium"}, "temperature": 0.3}
```

```json
{"model": "gpt-5.4-mini", "input": [...], "store": true, "text": {"verbosity": "low"},
 "reasoning": {"effort": "none", "summary": "auto"}}
```

**Never mix the families.** Reasoning models reject `temperature`, `top_p`, penalties and
`logprobs` with a 4xx; standard models have no `reasoning` block. The shape is
`reasoning.effort`, not `reasoning_effort`. A migration once passed name and endpoint checks and
still failed on exactly this.

The response text comes from top-level `output_text`, or else the first `output[].content[]` item
of type `output_text`. Empty output fails the run without pasting.

## Switching or adding a model

1. Check every request parameter against that model family, not just the name.
2. Confirm the response shape still parses.
3. Regenerate `bench/perf-budget.json` if the payload size changes; CI will flag growth.
4. Send one real request (`--bench --live --case short` on a Prod build) before calling it done.

## API keys

Named keys are stored as one DPAPI (CurrentUser) encrypted collection in the channel's data
folder (`apikey.dat`), never in `settings.json` or logs. The dashboard shows only names and
masked identifiers. Prod and Dev collections are separate. An isolated store (`Store::isolated`,
used by tests and replay bench runs) must never resolve a key path outside its own directory: an
old benchmark once overwrote the real saved key that way.
