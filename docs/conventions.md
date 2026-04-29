# Code Conventions

## C# / .NET (primary — everything under `src/`)

**Namespace:** file-scoped `namespace UniversalSpellCheck;` in every file.

**Naming:**
- PascalCase for types, public members, and constants.
- `_camelCase` for private readonly fields.
- snake_case for JSONL log event names and field keys: `replace_succeeded`, `input_len`, `request_duration_ms`.

**Nullability:** nullable reference types enabled. Use `?` and null-checks; avoid `!` suppression unless provably safe.

**Error handling:**
- `try { ... } finally { ... }` for any code that sets busy state or acquires a semaphore — ensures cleanup even on exception.
- Silent failures for optional paths (logging, replacements reload, clipboard restore). Never let a logging failure surface to the user.
- `try { ... } catch (IOException) when (attempts < 5)` for file contention — retry with backoff, then swallow.
- HTTP: 30s total timeout. On 4xx/5xx, capture raw response body before returning a failure result.
- User-facing failures use `NotifyIcon.ShowBalloonTip`; a failed request must never paste over the user's selection.

**`Escape()` helper pattern:** each file that writes log strings defines a private `static string Escape(string? value)` that backslash-escapes `\` and `"` and collapses newlines to spaces. Copy the exact four-substitution pattern rather than inventing a new one — consistency matters for downstream log parsing.

**Channel-aware constants:** all hotkey codes, mutex names, data folder names, display strings, and version strings come from `BuildChannel`. Never hardcode them in call sites.

**`BuildChannel.IsDev` is `const bool`:** the compiler performs dead-code elimination at build time. Blocks guarded by `if (BuildChannel.IsDev)` or `#if DEV` that are unreachable in the opposite configuration produce CS0162 "unreachable code" warnings. These are expected and intentional — do not suppress them.

**App lifetime:** keep the app single-process and resident. Do not introduce helper processes without a measured need.

**`HttpClient`:** app-lifetime instance, not per-request. One instance per service class, initialized at construction.

**API key storage:** DPAPI `DataProtectionScope.CurrentUser` only (via `SettingsStore`). Never write keys to `settings.json` or any plain-text file.

**UI threading:** `SetBusy` and `OnUpdateStateChanged` are called from async pipelines and must marshal to the UI thread via `BeginInvoke` / `Dispatcher.BeginInvoke` before touching form or WPF controls.

**Coordinator serialization:** capture/request/paste run through a single `SemaphoreSlim(1,1)`. Overlapping hotkey presses are rejected, never queued.

**Comments:** write only for complex algorithms, non-obvious performance decisions, known workarounds, critical state transitions. "Why not what." Skip the obvious.

---

## JSONL log line shape

Every line written by `DiagnosticsLogger.Log()`:

```
{ISO8601 timestamp} channel={prod|dev} app_version={semver} pid={int} {event_name} {key=value ...}
```

Fields after `pid` are free-form `key=value` pairs. String values are double-quoted with backslash escaping. JSON payloads (`LogData`) are serialized as a trailing JSON object on the same line as the event name.

Required fields on every line (injected by `DiagnosticsLogger`): `channel`, `app_version`, `pid`.

---

## File/path conventions

- Forward slashes in code-fenced paths and in docs, even on Windows.
- Backslashes only in literal `%LocalAppData%\...` style references.
- Log files: `spellcheck-{yyyy-MM-dd}.jsonl` (daily rolling, not weekly).

---

## Python (fine-tune tooling under `tests/` only)

PEP 8, 4-space indent. No type hints. Stdlib only where feasible. Docstrings for public functions. Not used in the native app runtime.
