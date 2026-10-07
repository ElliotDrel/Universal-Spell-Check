# Debugging Principles

## 1. Log first, fix second

When root cause is unclear, add logging first, analyze the output, then fix. No guessing patches.

1. Add targeted log lines around the suspected failure point.
2. Rebuild and relaunch — a code change is not running until the process is stopped and rebuilt (`dotnet run -c Dev` or publish+install for Prod).
3. Reproduce the failure and read the log.
4. Implement a fix based on data, not assumptions.

Logs live at `%LocalAppData%\UniversalSpellCheck.Data\logs\spellcheck-{yyyy-MM-dd}.jsonl`. Both channels write here; filter by `channel=prod` or `channel=dev`. Filter by `pid` to isolate a single run when both are running simultaneously.

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
2. Read `%LocalAppData%\UniversalSpellCheck.Data\logs\spellcheck-{today}.jsonl` before changing code.
3. For a capture or paste failure: find the `run_started` → `capture_*` → `run_completed`/`spellcheck_detail` sequence and read `copy_attempts` plus `timings.clipboard_ms`, `timings.request_ms`, `timings.request_send_ms`, `timings.request_wait_ms`, `timings.response_download_ms`, `timings.replacements_ms`, and `timings.paste_ms`. A `capture_failed` event inside `spellcheck_detail.events[]` carries per-attempt forensics (sequence numbers, modifier state, foreground exe + elevation) — read those before theorizing. See `docs/watchlist.md` § Capture-failure forensics.
3a. If a run is missing from the logs entirely (a `hotkey_pressed` with no `run_completed`), grep for `finalize_failed` — a finalize crash erases the run's telemetry.
4. For a request failure: read `status_code`, `error_code`, and the raw response body logged with `request_failed`.
5. For a dashboard failure: grep for `dashboard_open step=` to see how far construction got, then read the `error` and `stack` fields on the failing line.

---

## 4. Success criteria for manual testing

A spell-check run is clean when:

- `capture_succeeded` with `copy_attempts=1` (or a clearly logged retry reason).
- `request_succeeded` implied by `replace_succeeded` appearing with `request_attempts=1`.
- `replace_succeeded` records both the capture app and the app active at paste time; they may differ.
- No-selection produces `capture_failed` and no paste occurs.
- Rapid double-press produces `guard_rejected reason=already_running` for the second press.

---

## 5. Dashboard / WPF failures

`dashboard_open_failed` and `ui_dispatcher_unhandled` both log `error_type`, `error`, and full `stack`. Always check:

1. `wpf_resources_failed` at startup — `Styles.xaml`/`Components.xaml` didn't load. Dashboard cannot work until fixed.
2. `'{DependencyProperty.UnsetValue}' is not a valid value for property '...'` — almost always missing `System.Windows.Application` instance or a missing resource key. Do not chase template rewrites first. See `docs/watchlist.md`.
3. `loading_overlay_failed` — overlay handle not created on UI thread, or marshal path broken.

The `--dashboard-smoke` mode (`dotnet run -- --dashboard-smoke`) pumps `DispatcherFrame`s and hooks `Dispatcher.UnhandledException`. A smoke log that says `dashboard_smoke_ok` while the user still sees a crash means the pump duration is too short, not that the code is correct.

The smoke mode also verifies responsiveness, not only exceptions: a background watchdog terminates a blocked dispatcher after 10 seconds; the first activity page must finish within 5 seconds; initial and deferred startup rendering must remain bounded to 30 entries; and a synthetic large-text diff must take the bounded fallback. Run the built Release executable with `Start-Process -Wait` because the project is a Windows GUI executable and direct PowerShell invocation does not reliably wait for process exit.

When pumping WPF for deferred-work assertions, the frame sentinel must run below the work under test. The smoke sentinel uses `DispatcherPriority.ApplicationIdle` so `ContextIdle` viewport-fill callbacks execute before the frame exits; using `Background` would starve them and create a false pass.

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


## Local formatting replay

Formatting incidents can be frozen and replayed through the actual `RichTextClipboard` implementation
without an API request, live clipboard write, running editor, or server. The replay scope is the
rich-text mapper: original `input_text` + `clipboard_html` + recorded post-processed `output_text`.
It does not rerun AI, replacements, target hooks, RTF, or the destination's rendering behavior.

```powershell
New-Item -ItemType Directory -Force formatting_replay_runs
python .agents/skills/read-logs/scripts/logs.py --event spellcheck_detail --last 1 --save-replay-case formatting_replay_runs/case.json
dotnet build tests/TargetFormattingTests/UniversalSpellCheck.TargetFormattingTests.csproj -c Release
dotnet run --project tests/TargetFormattingTests/UniversalSpellCheck.TargetFormattingTests.csproj -c Release --no-build -- --replay-file formatting_replay_runs/case.json
```

Select the reported correction using time/app/channel/content filters before exporting. Export requires
exactly one run and refuses to overwrite a case. It supports complete legacy inline records. With
`developer_evidence`, it reads full sidecars, checks manifest run identity, SHA-256, byte and UTF-16
character counts, and embeds the inputs in the saved case. Truncated, corrupt, or size-limited required inputs fail explicitly. A pending reference whose
manifest is not available yet permits validated complete inline inputs with a warning;
larger truncated inputs require retry after evidence completion. If developer saving reports `failed`, `storage_limit`, `storage_busy`, or capture-admission `busy`,
complete inline inputs remain usable, with an explicit source warning. Existing corrupt manifests
never silently fall back. Inline replay rejects an empty source HTML capture when the source offered
`HTML Format`; missing format enumeration produces an explicit availability warning. Evidence paths must stay inside the evidence directory.

The JSON result contains complete `result.text` and `result.html` (including CF_HTML header), mode,
reason, attempt/application flags, paragraph count, current app version/build, and source identity.
`recorded_matches` compares current text/HTML to the historical requested clipboard payloads when
both are complete. A mismatch is useful diagnostic evidence, not automatically a regression: current
code may already contain a fix. Missing historical payloads are flagged and the comparison is `null`.

For a regression case, add an `expected` object alongside `detail` in the saved JSON containing
`text`, `html`, `mode`, and `reason` from the verified correct result. Replay checks all four exactly:
exit `0` means replay succeeded and any supplied expectation matched; exit `1` means expectation
mismatch; exit `2` means invalid arguments or incomplete/unreadable evidence. Direct `--replay-stdin`
accepts one JSON row from `read-logs --json`; use saved-case export for full developer sidecar hydration.
Unexpected mapper exceptions reproduce the production `rich_text_failed` plain-text fallback;
`mapper_exception_type` and a warning retain the diagnostic cause. Input-validation failures still
produce exit `2`. Build separately, then use `--no-build` for clean machine-readable stdout.

`formatting_replay_runs/` is ignored because real captured content may be private. Only deliberately
sanitized regression cases belong in version control. No visual preview is introduced: our mapper's
HTML is not proof of the original editor's paste behavior. Final acceptance remains a retest of the
updated application in the original location.

Validation: exporter tests cover supplementary Unicode, clipped inline/full sidecar recovery,
corruption, missing evidence, mismatched run identity, path escape rejection, ambiguous run selection,
and overwrite protection. Native replay tests cover missing/truncated inputs, fallback output,
historical comparison availability, and expected-result mismatch, alongside the existing real-mapper
formatting fixtures. The temporary Python environment and pinned SDK stay outside committed files.
