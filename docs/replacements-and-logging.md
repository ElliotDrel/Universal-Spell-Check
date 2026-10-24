# Replacements, Prompt-Leak Guard, and Logging

## Target formatting hooks

`TargetFormattingPipeline` resolves one rule from its explicit ordered list after clipboard-history
exclusion. Its after-copy hook runs before literal protection and the API request. After the existing
post-processing restores protected literals, the optional before-paste hook runs against a second set
of formatter-neutral private-use placeholders. Missing or duplicated placeholders skip that optional hook and paste the corrected text;
an unexpected hook exception retains the unmodified text and is recorded asynchronously.

Unmatched runs perform only the short resolver scan and a null branch. They do not perform the second
literal-protection scan. Headless benchmark runs remain unmatched unless a test explicitly supplies a
target context.

## Terminal input normalization (pre-processing)

Before the API call, `TerminalFormattingRule.AfterCopy` is invoked when the active process is a terminal (`WindowsTerminal`, `Code`, `powershell`, `pwsh`, `cmd`, `bash`). It removes Windows Terminal's `▎` wrap marker, then repairs wrapped literals before normalizing soft-wrap artifacts while preserving intentional structure:

1. **Terminal marker** (`▎`) → empty — removes the copied terminal UI marker before line handling.
2. **Double CRLF** (`\r\n\r\n[ \t]*`) → `\n\n` — preserves paragraph/section breaks.
3. **List items** (`\r\n[ \t]+` before `-`, `*`, `•`, or `N.`) → `\n` — preserves bullet and numbered list structure.
4. **Wrapped file paths** — a soft wrap inside a recognized file path is removed before generic line handling, so the path stays continuous and is protected from the model.
5. **Soft-wrap continuation** (` *\r\n[ \t]+`) → ` ` — collapses lines that wrapped purely due to terminal width.

Tabs are matched alongside spaces in the three line-handling passes. Bare `\r\n` without trailing whitespace is left untouched.

- Applied on the **hot path only** (`ExecuteHotPathAsync`). Headless/bench runs are unaffected.
- Bare `\r\n` without trailing whitespace is left untouched.
- Result is recorded through the common formatting result; the legacy `terminal_normalized=true terminal_norm_chars_removed=N` line fields and `terminal_normalization` JSON object remain unchanged.
- If not applied (non-terminal process or no artifacts found), `terminal_normalization.applied = false` and `chars_removed = 0`.

Full pipeline order: **resolve target** → **after-copy format** → **protect literals** → API call →
post-process (replacements + prompt-leak guard) → **restore literals** →
**before-paste format with protected literals** → **paste into the currently active app**.

## Protected literals

Before the API call, `ProtectedText.Protect` replaces exact literals with collision-safe numbered
placeholders. It protects:

- `http://` and `https://` URLs
- In Rust, complete copied Markdown links, including nested/escaped parentheses, using the same parser as link reconstruction. The inherited regex alone can stop before a link's closing delimiter; its shorter match is superseded by the complete parsed literal before the model sees the request.
- UUIDs and long opaque IDs containing both letters and digits
- common prefixed API keys/tokens, JWTs, and values assigned to key/token/secret/session fields
- Windows drive paths, UNC paths, POSIX paths, and slash-delimited relative paths

Quoted file paths may contain spaces. Unquoted paths stop at whitespace. After the AI response,
replacements and the prompt-leak guard run while placeholders are still present, then
`ProtectedText.Restore` restores the original byte-for-byte values. If a placeholder is missing
or duplicated, the run fails with `protected_text_restore_failed` and does not paste potentially
corrupted text.

---

## Replacements system

`replacements.json` lives at the repo root and is copied next to the exe at publish time via a `<None Include>` itemgroup in the csproj. `AppPaths.ReplacementsPath` finds it by walking up from `AppContext.BaseDirectory` until the file is found — works for both dev checkout (`src/bin/...` walks up to repo root) and Velopack-installed prod (found on the first try, next to the exe).

Format — canonical key maps to an array of variants to replace:

```json
{
  "GitHub": ["Git Hub", "git hub", "GitHUB", "GITHUB", "Github", "github"],
  "OpenAI": ["Open AI", "Open Ai", "open ai", "openai"]
}
```

### `TextPostProcessor` behavior (`src/TextPostProcessor.cs`)

- Parses the JSON, strips BOM, flattens to `(variant, canonical)` pairs, sorts longest-first so longer variants win over shorter overlapping ones.
- Uses case-sensitive ordinal comparisons and replacements (`StringComparison.Ordinal`).
- Protected literals remain as placeholders during replacements, so replacement variants cannot alter them.
- Reloads when `FileInfo.LastWriteTimeUtc` or `Length` changes. Keeps last known-good cache on reload failure. Clears cache when the file is missing.
- Reload logged as `replacements_reloaded count={N}`. Reload failure logged as `replacements_reload_failed`.

### Prompt-leak guard

The request prompt is structured as:

```
instructions: <PromptInstruction>
text input: <selected text>
```

If the model echoes the instruction preamble back in its output, `StripPromptLeak` removes the `instructions: ...` line and then strips a leading `text input:` label. Logged via `prompt_leak_triggered` / `prompt_leak_removed_chars` on the `replace_succeeded` line.

### Watchlist for replacements

- **Repeated reload failures** (`replacements_reload_failed` in logs) → malformed JSON, file locked by editor, or save-timing issue.
- **Same-size fast-edit edge case** — cache key is `mtime + size`; a very fast same-size edit can be missed until a later detectable save.
- **Read-while-writing risk** — if the file is edited mid-read, stale data can be cached under newer metadata.

---

## Logging (JSONL)

### Location

Both Prod and Dev write to the same directory:

```
%LocalAppData%\UniversalSpellCheck.Data\logs\spellcheck-{yyyy-MM-dd}.jsonl
```

`AppPaths.LogDirectory` always returns this path regardless of `BuildChannel.AppDataFolder`. Logs are **never split by channel** — the unified corpus is required for fine-tune dataset use. The `.Data` suffix is mandatory because the unsuffixed directory is owned and replaced by Velopack.

### Line format

Every line written by `DiagnosticsLogger.Log()`:

```
{ISO8601 timestamp} channel={prod|dev} app_version={semver} pid={int} {event_name} {key=value ...}
```

`DiagnosticsLogger.LogData(eventName, obj)` appends a JSON-serialized object after the event name on the same line.

### Required fields on every line

| Field | Source |
|---|---|
| `channel` | `BuildChannel.ChannelName` — `"prod"` or `"dev"` |
| `app_version` | `BuildChannel.AppVersion` — semver for prod, and the same base version with `-dev` appended for dev (for example `0.1.6-dev`) |
| `pid` | `Process.GetCurrentProcess().Id` — disambiguates two simultaneously running channels |

### Concurrent write safety

`DiagnosticsLogger.AppendWithRetry` opens the file with `FileMode.Append + FileShare.ReadWrite` and retries up to 5 times with linear backoff (10ms × attempt) on `IOException`. A per-instance `lock` prevents concurrent writes from the same process. The combination handles two channels (Prod + Dev) appending to the same file without data loss.

### Key log events

| Event | When |
|---|---|
| `started` | App boot; includes `channel`, `version`, `hotkey_vk` |
| `hotkey_pressed` | Every hotkey activation |
| `run_started` | Pipeline begins; includes `active_process`, `window_title` |
| `capture_succeeded` / `capture_failed` | After clipboard capture attempt. On failure, the `capture_failed` entry in `spellcheck_detail.events[]` carries per-attempt forensics: clipboard sequence numbers (`seq_before`/`seq_at_timeout`), physical modifier state (`mods_at_send`/`mods_at_timeout`), and foreground process + elevation (`fg_at_timeout`). See `docs/watchlist.md` § Capture-failure forensics |
| `capture_history_excluded` / `capture_history_exclude_failed` | Whether the captured (incorrect) text was tagged out of Windows clipboard history |
| `guard_rejected reason=already_running` | Overlapping hotkey press |
| `request_failed` / `request_retrying` | API errors |
| `replace_succeeded` | Full pipeline success; includes all timing fields |
| `paste_failed` | Clipboard setup or Ctrl+V failed |
| `spellcheck_detail` | JSON blob with full input/output/tokens/timings on every run |
| `replacements_reloaded` / `replacements_reload_failed` | Replacements file change detection |
| `update_check_start` / `update_download_done` / `update_apply_now` | UpdateService flow |
| `restart_command_received` / `restart_command_forwarded` / `restart_command_rejected` | Native `--restart`/`--update` alias received, acknowledgement (`accepted`), or Dev rejection |
| `restart_notification` / `restart_requested` / `restart_prepare_failed` / `restart_failed` | One progress notice, Velopack restart waiter, preparation or shutdown failure |
| `finalize_failed` | The post-run finalize step threw — the run's `run_completed`/`spellcheck_detail` lines were **lost**. Logs status, active process, and full stack. Treat as a missing-telemetry bug |
| `dashboard_open step=construct|show|activate|done` | Dashboard lifecycle |
| `loading_overlay_show` / `loading_overlay_hide` | Overlay visibility |
| `stopping` | Clean shutdown |

### `spellcheck_detail` fields

Each run emits a `spellcheck_detail` JSON blob containing: `status`, `error`, `model`, `active_app`, `active_exe`, `paste_target_app`, `paste_target_exe`, `paste_method`, `corrected_text_on_clipboard`, `original_clipboard_restored`, `captured_text_history_excluded`, `history_exclude_detail`, `text_changed`, `input_text`, `input_chars`, `output_text`, `output_chars`, `paste_text`, `paste_html` (see below), `raw_ai_output`, `clipboard_html` (see below), `rich_text` (source HTML presence, safe-rewrite attempt/result, reason, and paragraph count), `raw_response`, `request_payload`, `tokens` (input/output/total/cached/reasoning), `timings` (clipboard_ms, after_copy_format_ms, before_paste_format_ms, payload_ms, request_ms, api_ms, request_send_ms, request_wait_ms, response_download_ms, parse_ms, replacements_ms, prompt_guard_ms, paste_ms, total_ms), `replacements` (count/applied/protected_values plus per-kind protected counts), `prompt_leak` (triggered/occurrences/text_input_removed/removed_chars/before_length/after_length), the backward-compatible `terminal_normalization` object, `target_formatting` (rule/match identity plus per-hook application, character counts, stable operations, and failures), and `events[]`.

`target_formatting` may contain a parsed hostname for a site rule. It never contains a raw browser
path, query, fragment, page title, selected text, or extension message.

### Rust local latency timings (schema 1)

Rust detail records now include `timing_schema_version=1`, `request_mode`
(`live` or `offline_replay`), and an explicit `timing_boundary`. Existing timing
names remain compatible; the C# reference retains its historical schema.

- Start `total_ms` at accepted `Worker::trigger` entry, before the worker queue.
  Finish at input-call return on success, or operation failure before recovery.
  This excludes physical key delivery before app receipt and does not acknowledge
  editor acceptance or visible rendering. Final status bookkeeping adds a small
  residual before the sample is taken.
- Retain partial timings on returned errors and caught panics. Report stages that
  never ran as `null`; accumulate elapsed time across retries. Zero is a measured
  value, never an unavailable-data placeholder.
- Attribute nonoverlapping top-level work to `dispatch_ms` (queue and any client
  reinitialization before run entry), `setup_ms` (target/evidence setup and key/client
  validation), `backup_ms`, `clipboard_ms`, `history_exclude_ms`, `prepare_ms`
  (normalization, protection and payload construction), `request_ms`,
  `replacements_ms`, `rich_mapping_ms`, `diagnostics_ms` (existing hot-path metadata,
  string/base64 copying and serialization), and `paste_ms`.
- Report `unattributed_ms` as total minus those top-level fields. It includes UI
  notices, response extraction, bookkeeping and timer overhead. Investigate a large
  residual before adding deeper timers. Do not clamp it or add nested phases again.
- Break `capture` into `modifiers_ms`, `copy_input_ms`, `wait_ms` (message-aware
  waits, not an unconditional 25-ms sleep), `read_ms`, `copy_attempts` and
  `read_attempts`. Break `capture.reads` into `open_ms`, `unicode_ms`, `html_ms`,
  `rtf_ms`, `native_ms`, `decode_ms` and `enumerate_ms`. Optional-format checks
  are timed even when a format is absent. Counts cover attempted calls including
  failures; read counters/durations accumulate across both copy attempts.
- Break `paste` into `write_ms` (allocation/open/publication wrapper), `readback_ms`
  (developer-only readback and metadata), `settle_ms`, `modifiers_ms` and `input_ms`.
  These nest inside `paste_ms`. Preserve the existing 50-ms settlement and all
  clipboard, target-selection and recovery behavior.
- Report failure `recovery_ms` separately, outside `total_ms`. The `run_completed`
  event's elapsed time includes recovery/notification/timing finalization; use the
  detail record's explicit boundary for latency analysis.
- Preserve network phase labels without treating send/wait as proven upload/server
  timings. Return `api_ms` and network subphases as `null` for offline replay; its
  `request_ms` measures local replay lookup/response parsing. Failed requests retain
  total request duration even when no phase breakdown is available.
- Keep measurement itself to monotonic clock reads and fixed fields. Serialize new
  timing metadata after the measured pipeline; no new I/O occurs before input.

### `clipboard_html`, `clipboard_rtf`, `clipboard_formats`

Every capture reads all the flavors the source offered. `input_text` is `CF_UNICODETEXT`;
`clipboard_html` is `CF_HTML` verbatim including its header; `clipboard_rtf` is the RTF flavor. Both
rich fields are `""` when absent, carry `_chars` (true pre-truncation size) and `_truncated` siblings,
and are capped at 512K chars so one pathological selection cannot produce a multi-megabyte log line.

`clipboard_formats` is the comma-joined list of every format name present at capture. **This is the
field that makes an empty `clipboard_html` interpretable** — without it, "the app offered no markup"
and "the app offered markup we failed to read" are indistinguishable after the fact. Chrome offers
`HTML Format,UnicodeText,Chromium internal source RFH token,Chromium internal source URL,Locale,Text,OEMText`;
a plain-text source offers only the text formats.

Note on identifying the source page: `CF_HTML`'s header can carry a `SourceURL:` line, but it is
**not reliable** — observed present on a `file://` copy and absent on a real ChatGPT copy from the
same browser. The `Chromium internal source URL` format is also unusable: it reads back empty from
another process. Neither replaces the Chrome extension bridge in
`.planning/app-site-formatting-customizations.md`.

This is the input to the rich-text pipeline (`.planning/rich-text-clipboard-pipeline.md`). A narrow
safe path now handles ChatGPT's simple `data-pm-slice` paragraph fragments: if every non-empty source
paragraph exactly maps to the copied text and the corrected output has the same paragraph count, the
app replaces only text-node contents and writes both `CF_HTML` and Unicode text. Empty paragraphs stay
as markup, preventing their browser plain-text serialization from becoming extra pasted lines. One
ordered or unordered list item, including an indented nested item, also qualifies only when the
generated Unicode indentation and list marker are identical before and after correction. The generated prefix is
stripped and only the corrected item body is pasted as Unicode text because ChatGPT inserts any HTML
block as a nested item. A single inline `<span>` is
accepted around the item body. Mixed spans, links, and lists next use text-node alignment: each original
HTML text node must map in order to the copied Unicode text, and every model edit must belong to a
mapped node. The output retains the original tags and attributes. `rich_text.mode=aligned_html`
identifies this path. Future text nodes may claim an earlier candidate only when their complete
source match contains it; a later standalone space or typo cannot mark existing Unicode text as
missing. Recovery restores only nodes absent from the source alignment, never nodes deleted by
the model's correction. `html` and `list_body_text` identify the narrower paths. If alignment
removes copied trailing whitespace before inserting final punctuation, that insertion is owned by
the text node at the start of the removed suffix. Large alignment diffs omit identical prefix/suffix
text before applying the existing bounded matrix limit. Skill mentions retain their original spans
and attributes when surrounding corrections align successfully. If alignment
declines, corrected Unicode text still pastes with any HTML text nodes omitted from the source Unicode flavor,
and `rich_text.reason` records why. If reconstruction throws, the corrected Unicode text still pastes.
An exact
model echo followed by `---` and a corrected copy is removed before replacements and placeholder
restoration; the run records `echoed_original_removed`. The exact two-level fragment produced when
a parent item and its one nested child are selected is reconstructed as `nested_list_html`; both
generated prefixes and both source bodies must match before either HTML text node is replaced.
Larger simple list trees and bare `<li data-pm-slice>` subtrees use `structured_list_html`: every HTML
paragraph must map exactly to one generated Unicode line, and the corrected output must retain the
same ordered or unordered prefix at
each position. The app removes surplus blank lines inserted between prefixed items, reconstructs the
source newline sequence, and replaces only paragraph text in the original HTML tree. Unknown markup,
changed prefixes, missing lines, and nonblank extra lines still fall back instead of being guessed.

LinkedIn messaging selections have a separate conservative paragraph path, identified by the
captured CF_HTML `SourceURL` with an exact HTTPS LinkedIn host and `/messaging/` path.
Only plain `<p>` text and empty `<p><br></p>` blocks qualify. Their decoded text must
match Chrome's copied two-newline paragraph boundaries (NBSP and space compare equally).
The corrected output must keep the same nonempty sections. Reconstruction changes only
paragraph text and retains empty blocks, styles, and source edge whitespace. The Unicode
flavor uses one newline per paragraph boundary; it does not re-paste Chrome's doubled
copy separators. Unsupported markup or changed content structure keeps the plain fallback.
This path uses the existing `rich_text.mode=html` telemetry.

The retained C# reference restricts immediate CF_HTML diagnostic readback to Dev. Rust developer
logging records `clipboard_readback` immediately before input injection in either channel, using
one non-waiting clipboard-open attempt. This supersedes the old Dev-only rule for the shipped app.
Readback verifies publication, not the destination's rendered paste; full payloads remain in the
opt-in evidence sidecars, with summary status and the capture boundary in ordinary records.

`output_text` is the corrected text before rich-text replacement. `paste_text` and `paste_html` are
the final Unicode and CF_HTML strings requested in the clipboard write, including any text recovered
from source HTML. Each has `_chars` (full length) and `_truncated` siblings; the logged strings are
capped at 512K characters. An empty `paste_html` means the app requested a plain-text paste. These
fields describe the requested payload, even if the write or paste later fails. They do not prove how
the destination editor rendered the result. Older runs lack these fields.

Rust rich mapping reports `reason=alignment_budget_exceeded` when it cannot compute a precise
edit map within its fixed work/edit budgets. The dashboard may display a coarse diff, but formatting
and native style-position mapping cannot use that approximation. `rich_text.link_status` is
`not_applicable`, `verified`, or an explicit refusal (`unverified_markup`, `unverified_destinations`,
`unverified_occurrences`, `unverified_order`, `unverified_alignment`, `projection_budget_exceeded`).
`applied=true` describes HTML text mapping; an unverified link status explicitly means link semantics
were not established. A plain fallback retains the original Unicode link destinations. These fields
are also available through `--rich-json`; no screenshots or clipboard access are needed for replay.

`logs.py --has-html` filters to runs that carried markup. The formatted view prints only the size;
`--json` includes the markup. Plain `--grep-detail` deliberately does not search this field (it would
match CSS noise on nearly every row) — scope it with `clipboard_html:<needle>`.

### Dashboard Activity feed

The WPF **Home** page (`ActivityPage`) renders successful `spellcheck_detail` rows from the unified log directory:

- **Included:** `status == "success"` with non-empty `input_text` and `output_text`
- **Diff display:** uses `input_text` vs `output_text` when `text_changed` is true; otherwise shows `output_text` only
- **Timing breakdown:** hover a row and click the clock button to expand its persisted pipeline timings (clipboard capture, request construction, AI send/wait/download, post-processing, paste, and total). Older rows without a `timings` object do not show the button.
- **Stats bar:** all-time counts from every `spellcheck-*.jsonl` file (checks = success lines; corrections = success lines with `text_changed: true`)
- **Pagination:** 30 entries per page, newest first; not a live tail — refresh or scroll to see new/historical data

For visual layout and interaction (hover actions, inline diff, infinite scroll), see `DESIGN.md` § Home (Activity) and `docs/architecture.md` § WPF dashboard.

## Optional developer evidence (schema 1)

Settings → Logs → Developer logging is off by default in both channels, including Production.
Changing it applies to the next correction without restarting. Disabled corrections keep their
existing clipboard and diagnostic behavior. Headless/benchmark requests do not capture the desktop.

Docs corrections read the original `Chromium Web Custom MIME Data Format`
for selections whose CF_HTML carries the Google Docs producer marker. Capture
runs before history exclusion, is capped at 1 MiB of binary data, and skips a
changed clipboard sequence. `clipboard_chromium_mime_status` distinguishes
`disabled`, `not_applicable`, `ok`, busy/read failures, `source_changed`, and
`size_limit`; an unavailable payload is never treated as a successful empty read.
`clipboard_chromium_mime_base64`, its `_chars` count and `_truncated` flag appear
in the detail record with the usual 512K inline cap. Full base64 is retained in
an integrity-checked developer-evidence sidecar with the same name. The manifest
also retains the capture status. This native metadata can include document
identifiers and data beyond the visible selection. Disabled logging does not
persist native payloads: the bounded captured bytes are used only in memory
for Docs paste. Headless requests remain unchanged. Native replacements log
`rich_text.mode=docs_native`, `native_docs_status`, `native_docs_bytes`, and
`clipboard_native_verification` (`exact_match` confirms the binary clipboard
format survived the write). `native_docs_status` is now reported whenever the
common selection model verifies, even if CF_HTML did not; a `docs_native` run
with `rich_text.replacement_html_chars=0` wrote only Unicode plus the native slice because its
HTML block shape could not be verified. Enabled evidence saves requested/readback native
base64 sidecars. Replay export hydrates and integrity-checks the native source
so replay exercises the same path.

Each detail record has `run_id` and `developer_logging_enabled`. Enabled runs add `developer_evidence` with `schema_version`,
`manifest_path` relative to the shared log directory, and `status`. Busy evidence admission is reported explicitly. Ordinary `run_completed` and the single `spellcheck_detail` are written before optional evidence
provider/storage waits, with a `pending` manifest reference. The app then writes evidence and a separate
`developer_evidence_completed` event with the final reference and readback summary. A reader may need
to retry briefly if the manifest is not present; a pending reference can be read once its manifest exists.
There are no duplicate detail records and no added shutdown waits. Quitting can leave optional
evidence incomplete without holding the ordinary run logs behind its completion.

Evidence lives in `logs/developer-evidence/<run_id>/manifest.json`. `payloads` contains full
UTF-8 sidecars for `input_text`, `clipboard_html`, `clipboard_rtf`, `clipboard_formats`,
`raw_ai_output`, `output_text` (the rich mapper input), `paste_text`, `paste_html`, and
`readback_text/html/rtf/formats`. Entries expose relative `path`, SHA-256 `sha256`, UTF-8 `bytes`,
.NET UTF-16 `chars`, `complete` and `status`. Empty source markup is complete only when the format list confirms that flavor is absent.
An offered-but-empty flavor or an unavailable format list is explicitly incomplete, as are null values. Inline 512K markup caps remain, but the sidecars preserve
larger ordinary selections. The Rust store retains the 16MiB per-payload cap (`size_limit`) and 256MiB total
cap. The former stop-at-full policy is superseded: when a write needs space,
Rust reconciles the quota counter against actual files, then removes oldest
eligible whole-run folders until the pending write fits near 192MiB. This runs
on the optional evidence worker under the existing shared cross-channel mutex;
it adds no clipboard/UI work or correction-thread waits. Ordinary JSONL logs,
settings, keys, and separately exported replay cases are not rotated.

Only 32-hex run folders containing known regular evidence files are eligible.
Current runs, incomplete runs modified within 24 hours, backups/custom files,
nested directories, and reparse points are preserved. Incomplete abandoned
runs become eligible after 24 hours. If protected content or an I/O error
prevents reclamation, evidence reports `storage_limit` or `failed`; corrections
continue. `developer_evidence_pruned` records removed run/byte counts;
`developer_evidence_storage_failed` records storage errors. A locked run is skipped
so other runs can be reclaimed; `developer_evidence_storage_pressure` records
blocked-run counts or insufficient reclaimable capacity. Storage warnings are
limited to one per run. A partly pruned run can retain its manifest with missing
payloads; replay reports required missing data explicitly and omits missing optional
payloads. Optional storage workers wait up to 15 seconds for their shared mutex;
the correction thread never waits for this work. Historical manifest
references may expire; export a replay case before preserving a specific incident.
The retained C# reference keeps its historical stop-at-full policy. Evidence is local and can
contain selected text and surrounding editor content; API key/settings files are never included.

`contexts` links automatic `before-source` and `after-paste` captures with requested/captured/completed
timestamps and target process/PID/HWND/title. Images contain visible screen pixels within the
foreground window bounds (screen-clipped), and can include overlays; they are not DOM snapshots.
The after capture requests a 600ms settling delay, which does not prove editor rendering is finished.
Foreground changes and late source captures are explicitly labeled. UIA reports focused control
identity, up to 32768 characters of document text and 8 selected ranges of up to 8193 characters.
Changed paste destinations explicitly label unavailable destination-before context.
Text/range truncation, unsupported selection, password controls, provider errors, busy workers and
750ms timeouts are explicit. Selection offsets are not reconstructed.

UIA runs on a dedicated MTA background thread, with at most one outstanding provider call in this
process. A stuck provider causes later captures to be skipped, preventing accumulating orphan workers.
Image capture/encoding/storage also runs off the correction thread and accepts only one image job
at a time. The correction never waits for UIA or screenshot completion. Clipboard readback uses one
best-effort background STA worker with a 500ms deadline and clipboard sequence checks. A stuck read
blocks later readback jobs rather than accumulating workers. Absent formats, failures and races are
explicit; a successful read proves the clipboard state at its timestamp, not the editor rendering.
Evidence admission permits at most two in-flight runs. Shared cross-channel quota reservations
are serialized with a named mutex; a failed write may conservatively overcount space until the next pressure-triggered reconciliation.

`--developer-evidence-smoke` runs the rebuilt native executable with a disposable RichTextBox,
real Ctrl+C/Ctrl+V, enabled readback and automatic images, disabled readback, live settings cache
updates using disposable settings, and a 600000-character sidecar check. It restores the prior
clipboard and exits 0/1. It does not call the model or modify a live user document. Evidence remains
in the shared logs for inspection, as it would for an ordinary enabled run.

`--developer-evidence-tests` runs deterministic payload integrity, UTF-16 character counts,
large sidecar, offered-but-unreadable source markup, per-payload limit, full-store and bounded-admission
checks in a disposable temporary directory, without interacting with the desktop.

### Verified on 2026-10-07

- Both Dev and Release compiled with the pinned .NET 10.0.401 SDK.
- Release `--developer-evidence-tests` passed UTF8/SHA256 full sidecar roundtrip (600000+
  characters and emoji), UTF16 counts, source-read incompleteness, 16MiB size limit,
  exhausted 256MiB store and two-run admission checks.
- Native Dev disposable RichTextBox smoke passed actual Ctrl+C/Ctrl+V, RTF source capture,
  before/after screenshots, readback and live enable/disable cache checks (PID 21332).
  Later fixture attempts correctly failed when desktop focus/selection was unavailable;
  the fixture reports these as errors rather than silently claiming a paste occurred.
- The actual Dev dashboard enabled the setting and the normal coordinator corrected a disposable
  Notepad selection using Ctrl+Alt+D (PID 24924, run `ea496bc4082448c2ba8a4a9fcd5da874`).
  The manifest contained timely before/after images, original/corrected UIA document text,
  original selection, correct readback and complete empty source HTML/RTF where absent.
  The visible Notepad result changed `teh` to `the`.

The final rebuilt Dev native smoke passed again (PID 13196, run
`7deef858b13a4cd3b05d77ba20abce96`, capture scheduling 3ms). The actual Dev dashboard then
disabled the setting and a second normal Notepad correction succeeded without developer evidence
(run `7ad4a5999ea54a93ab2bfee465f87bf6`). Dev settings were left disabled.
