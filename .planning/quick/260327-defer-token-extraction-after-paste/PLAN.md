# Decision Record: Keep token extraction before paste

## Status

Rejected optimization. No change should be made to `Universal Spell Checker.ahk` for this idea.

## Context

The hotkey handler currently extracts `modelVersion` and token counts from the successful Responses API payload immediately after reading `response` and before parsing or paste operations.

That placement is slightly earlier than strictly necessary for the happy path, but it has an important property: the log metadata is captured before any later operation can throw.

## Why the after-paste move was rejected

Moving the six `RegExMatch` calls to after `SendText()` or `Send("^v")` would create a regression in failure-path logging.

If any exception occurs after the API response is decoded but before the deferred token block runs, execution jumps to `catch` and then `FinalizeRun(logData)`. The asynchronous log snapshot would then miss `modelVersion` and token data for that run.

Those fields are most useful on exactly those abnormal runs, so deferring them past paste is not a stable tradeoff.

## Safe guidance

- Keep token/model extraction immediately after `response := GetUtf8Response(http)`.
- Do not treat startup-shortcut repair as a code change inside the hotkey handler.
- If startup launch needs repair, use the repo-managed `Install-StartupShortcut.ps1` script instead of a one-off manual shortcut edit.

## Verification

1. Confirm `Universal Spell Checker.ahk` still extracts token/model data right after the successful response is read.
2. Confirm normal spell check runs still produce populated `model_version` and `tokens` fields in the JSONL log.
3. When testing failure handling, distinguish between:
   - non-200 API responses, which return before token extraction and should log default zero values
   - post-response exceptions, which should still preserve token/model fields because extraction happened earlier
