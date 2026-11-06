# Logging

Both channels append to `%LocalAppData%\UniversalSpellCheck.Data\logs\spellcheck-YYYY-MM-DD.jsonl`.
This corpus is the dashboard's data source, the latency record and the fine-tune dataset, so
never split it, rename fields or drop a field without updating `read-logs` and the dashboard.
Read it with the `read-logs` skill; `logs.py --stats` reports latency medians from real use.

## Line format

```
<RFC3339 local time> channel=<prod|dev> app_version=<semver> pid=<n> <event>[ <JSON object>]
```

`logging.rs` writes lines on a background thread with the timestamp taken at event time, and
flushes on shutdown. `pid` separates Prod and Dev running at once. Bench runs log into their own
output folder, never this corpus.

## `spellcheck_detail`

One JSON record per correction, written even when it fails. Main fields:

- Identity and result: `run_id`, `status`, `error_message`, `model`, `active_exe`, `paste_method`, `text_changed`, `status_code`, `request_attempts`, `tokens`.
- Text: `input_text`, `raw_ai_output`, `output_text` (corrected text before rich mapping), `paste_text` and `paste_html` (what was written to the clipboard). These describe the requested paste, not how the destination rendered it.
- Source formats: `clipboard_html` (CF_HTML with header), `clipboard_rtf`, `clipboard_formats` (every format name offered) and a `_status` per optional format (`ok`, `absent`, `too_large`, `read_failed`). `clipboard_formats` is what makes an empty `clipboard_html` interpretable. Large strings are capped at 512K characters with `_chars` and `_truncated` siblings.
- Processing: `replacements`, `prompt_leak`, `rich_text` (`mode`, `applied`, `reason`, `link_status`), `captured_text_history_excluded`, `events[]` (including capture-failure forensics).
- Debug payloads: `request_payload`, `raw_response`.

## Timings (`timing_schema_version` 1)

Every correction logs its latency breakdown; this is the in-the-field performance record.
`total_ms` runs from the accepted hotkey (`Worker::trigger`) to the Ctrl+V input returning. It
excludes physical key travel and does not prove the editor rendered the paste; `--bench`
measures that (`docs/testing.md`). Non-overlapping top-level phases:

`dispatch_ms`, `setup_ms`, `backup_ms`, `clipboard_ms` (capture), `history_exclude_ms`,
`prepare_ms` (normalize, protect, build payload), `request_ms`, `replacements_ms`,
`rich_mapping_ms`, `diagnostics_ms`, `paste_ms`, plus `unattributed_ms` (total minus the rest).

`capture` and `paste` objects break those phases down further (modifier waits, copy input, read
attempts, clipboard write, the 50 ms settle, paste input). `request_send_ms`, `request_wait_ms`
and `response_download_ms` are WinHTTP callback intervals, not proven upload or server time;
compare `request_ms` as a whole. A stage that never ran is `null`; zero is a measured value.
Replay runs report `api_ms` as `null`. Failure `recovery_ms` is reported outside `total_ms`.

## Other events

`started`, `stopped`, `run_completed`, `guard_rejected`, `run_failed`, `replacements_unavailable`,
`apikey_load_failed`, `data_migration_*`, `update_check_*`, `update_download_*`,
`update_apply_now`, `restart_requested` and `developer_evidence_*`.

## Developer evidence

Settings → Developer logging, off by default and effective from the next correction. When on,
each run also writes `logs/developer-evidence/<run_id>/manifest.json` with full sidecars
(SHA-256, byte and UTF-16 counts) for input, source formats, model output, paste payload and
clipboard readback, plus before and after window screenshots and UI Automation text. All capture
runs on background workers; the correction never waits for it. The manifest reference in
`spellcheck_detail` starts as `pending` and a later `developer_evidence_completed` event finalizes
it. Storage is shared across channels under a named mutex, capped at 16 MiB per payload and
256 MiB total, with oldest-first pruning of whole run folders. Evidence can contain surrounding
editor content; keys and settings are never included. Google Docs native slices are kept in
memory for the paste and persisted only when developer logging is on.
