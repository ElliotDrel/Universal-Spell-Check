# Debugging Principles

## 1. Log first, fix second

When root cause is unclear, add logging first, analyze the output, then fix. No guessing patches.

1. Add targeted log lines around the suspected failure point.
2. Rebuild and relaunch — a code change is not running until the process is stopped and rebuilt (`dotnet run -c Dev` or publish+install for Prod).
3. Reproduce the failure and read the log.
4. Implement a fix based on data, not assumptions.

Logs live at `%LocalAppData%\UniversalSpellCheck\logs\spellcheck-{yyyy-MM-dd}.jsonl`. Both channels write here; filter by `channel=prod` or `channel=dev`. Filter by `pid` to isolate a single run when both are running simultaneously.

---

## 2. Complete verification before declaring done

Never declare work complete without verifying ALL aspects, not just structure.

For API/model work:
1. Model name correct.
2. Endpoint correct.
3. Request structure matches current API docs.
4. **Every parameter supported by this specific model type** — most commonly missed. Standard models use `temperature`; reasoning models reject it and require `reasoning.effort`. See `docs/model-config.md`.
5. Response shape compatible with `OpenAiSpellcheckService.ExtractOutputText`.
6. Error handling captures raw response body on 4xx/5xx.

Real example: migrating to a reasoning model passed name/endpoint checks but missed that `temperature` causes an API error on reasoning models.

---

## 3. Native app debugging workflow

1. Confirm which process is running — dev checkout (`dotnet run -c Dev`) or installed Prod. They use different hotkeys and data folders. Check `channel=` and `app_version=` on the first `started` log line.
2. Read `%LocalAppData%\UniversalSpellCheck\logs\spellcheck-{today}.jsonl` before changing code.
3. For a capture or paste failure: find the `run_started` → `capture_*` → `replace_succeeded`/`paste_failed` sequence and read `capture_duration_ms`, `copy_attempts`, `request_duration_ms`, `postprocess_duration_ms`, `paste_duration_ms`.
4. For a request failure: read `status_code`, `error_code`, and the raw response body logged with `request_failed`.
5. For a dashboard failure: grep for `dashboard_open step=` to see how far construction got, then read the `error` and `stack` fields on the failing line.

---

## 4. Success criteria for manual testing

A spell-check run is clean when:

- `capture_succeeded` with `copy_attempts=1` (or a clearly logged retry reason).
- `request_succeeded` implied by `replace_succeeded` appearing with `request_attempts=1`.
- `replace_succeeded` shows matching `active_process` from capture through paste.
- No-selection produces `capture_failed` and no paste occurs.
- Rapid double-press produces `guard_rejected reason=already_running` for the second press.

---

## 5. Dashboard / WPF failures

`dashboard_open_failed` and `ui_dispatcher_unhandled` both log `error_type`, `error`, and full `stack`. Always check:

1. `wpf_resources_failed` at startup — `Styles.xaml`/`Components.xaml` didn't load. Dashboard cannot work until fixed.
2. `'{DependencyProperty.UnsetValue}' is not a valid value for property '...'` — almost always missing `System.Windows.Application` instance or a missing resource key. Do not chase template rewrites first. See `docs/watchlist.md`.
3. `loading_overlay_failed` — overlay handle not created on UI thread, or marshal path broken.

The `--dashboard-smoke` mode (`dotnet run -- --dashboard-smoke`) pumps `DispatcherFrame`s and hooks `Dispatcher.UnhandledException`. A smoke log that says `dashboard_smoke_ok` while the user still sees a crash means the pump duration is too short, not that the code is correct.

---

## 6. Simplest solution first (performance priority)

Speed is the product. When parsing API responses, regex over JSON object model when:
- Only one field is needed.
- Response structure is predictable.
- Performance matters (regex is ~10x faster for this case).

Use full JSON parse when many nested fields are needed or structure varies. Keep primary path fast; add a fallback with verbose logging on both branches.

---

## 7. Update service debugging

- `update_check_skipped reason=dev_or_uninstalled` — correct behavior when running `dotnet run`.
- `update_check_skipped reason=not_installed` — Velopack not installed; only relevant for Prod builds run outside the installer.
- `update_check_skipped reason=in_progress` — concurrent check already running; not an error.
- `update_check_failed` — network issue or GitHub API error; check `error` field.
- `update_apply_failed` — Velopack couldn't apply the downloaded update; check `error` field and whether the download actually completed (`update_download_done` present).
