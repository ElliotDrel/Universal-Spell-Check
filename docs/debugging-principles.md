# Critical Debugging Principles

## 1. Complete verification before declaring success
Never declare work complete without verifying ALL aspects, not just structure. For API/model work:
1. Model name correct.
2. Endpoint correct.
3. Request structure matches docs.
4. **Every parameter supported by this specific model type** (most-missed step).
5. Response format compatible.
6. Error handling captures raw error body on 4xx/5xx.

Real example: migrating to GPT-5.1 passed name/endpoint checks but missed that reasoning models reject `temperature`. See `model-config.md`.

## 2. Debug first, fix second
Never guess fixes when root cause is unclear.
1. Add comprehensive debug logging to identify failure point.
2. Analyze the debug output.
3. Implement a targeted fix based on data, not assumptions.

Past examples: regex "returned empty" logs revealed overly-strict pattern; AHK v2 "Expected a Number but got a String" revealed number-parse syntax bug.

## 3. Simplest solution first (performance priority)
When speed matters, prefer regex-based parsing over object-model parsing. The current regex extractor is ~10x faster than the Map-based JSON parser and ~25 lines vs ~200.

**Use regex when:** structure predictable, need one field, performance critical.
**Use full JSON parse when:** many nested fields, structure varies, need to rebuild JSON.

This project: regex is correct - only one text field, speed critical. Keep regex primary + full parser fallback with verbose logging.

## 4. AutoHotkey v2 compatibility gotchas

**Number conversion:**
- WRONG: `return numberText + 0` - throws "Expected a Number but got a String".
- RIGHT: `Integer(numberText)` or `Float(numberText)`.

**Object types:**
- `{}` -> basic Object. `Map()` -> Map (better for dynamic keys).

**Property access:**
- `obj.property`, `obj.%varName%`, `map[key]` (bracket may not work on Object in some versions).

**Method names:**
- Object: `obj.HasOwnProp(key)`. Map: `map.Has(key)`. Array: `arr.Length`.

## 5. Multiple solutions strategy
Keep a primary (fast) path and a fallback (safe) path, both instrumented. Current code: regex primary + Map-based fallback with debug logs on every branch.

## 6. Verification standards
- SPEED IS PARAMOUNT - user has emphasized repeatedly.
- Official docs only when user emphasizes them; be strategic when direct access fails.
- Temporary debug logging is acceptable for troubleshooting even in otherwise minimal scripts.
- Proxy startup is a required dependency path for AHK - preserve the 5s/30s/60s attempt ladder and `ExitApp` on exhaustion.

## 7. Native app debugging standards

The native app is now the production hotkey owner.

When debugging native behavior:
1. Confirm whether the running process is the dev build or published EXE.
2. Check `%LOCALAPPDATA%\UniversalSpellCheck\native-spike-logs\phase*-YYYY-MM-DD.log` before changing code.
3. Preserve `Ctrl+Alt+U` unless the user explicitly asks for a temporary test hotkey.
4. Do not change the AHK proxy/retry ladder while debugging native failures; native does not use the Python proxy.
5. Reproduce capture/paste failures with active app name and log timing before adding app-specific rules.
6. Keep request failures non-destructive: restore original clipboard and do not paste.
7. Keep the loading overlay tied to post-capture request/paste work, shown without activation, and hidden in `finally`.
8. If paste fails after a successful request, compare the logged original target app with `paste_target_exe` / `paste_target_app` before changing timing or app-specific behavior.

Native success criteria for manual testing:
- selected text is captured with `copy_attempts=1` or a clearly explained retry
- request succeeds with `request_attempts=1` unless a transient retry is logged
- `replace_succeeded` includes capture/request/postprocess/paste timing
- `replace_succeeded` keeps the same foreground process from capture through paste
- no-selection logs `capture_failed` and does not paste stale clipboard text
- rapid double-press logs `guard_rejected reason=already_running`
