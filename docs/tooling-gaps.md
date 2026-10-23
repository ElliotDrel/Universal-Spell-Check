# Tooling Gaps — Debugging Workflow

The three gaps from the `Competitionetition` investigation shipped on 2026-06-01. The two gaps from the 2026-09-28 ChatGPT formatting incident are now addressed: `logs.py --event spellcheck_detail --last 2` searches across midnight and shows the rich-text decision, while `spellcheck_detail` records the final requested `paste_text` and `paste_html` payloads. The log still cannot prove how a destination website rendered a paste. Add further instrumentation only if a real incident shows a remaining gap.

## Development environment verification

The 2026-10-03 environment setup uses Python 3.14.8 and the dependencies pinned in `requirements-dev.txt`. The native app builds in Dev and Release, all three C# executable test suites pass, and published startup/dashboard smoke modes exit 0. Live API correction and installed auto-update flows still require separate acceptance testing.

The full Python run (`.venv/Scripts/python.exe -m pytest tests/ bench/ --continue-on-collection-errors -q`) reports 26 passed, four failed, and one collection error. These repository function mismatches also appear before the Velopack update:

- Three tests in `tests/test_benchmark_spellcheck_models.py` call missing `save_stable_dataset` or `build_versioned_dataset_path` helpers in `benchmark_spellcheck_models.py`.
- One test in `tests/test_export_openai_finetune_dataset.py` calls the same missing `save_stable_dataset` helper.
- `bench/test_compare.py` imports missing `build_delta_rows` and `format_delta_table` functions from `bench/compare.py`.

Resolve whether these APIs are still intended before changing implementations or tests. Do not skip the failing cases or describe the Python suite as passing. `google-genai` also emits an upstream Python 3.14 deprecation warning for `_UnionGenericAlias`; it does not fail the passing tests.

Native compilation retains the documented channel-specific CS0162 warnings and an existing CS8602 nullable warning at `SpellCheckAppContext.cs:49`. The dependency update does not suppress those warnings.

## Browser runtime path access (2026-10-11)

During Docs paste verification, the Chrome skill's mandatory bootstrap import
of its installed `scripts/browser-client.mjs` failed in `mcp__node_repl__js`
with `EPERM: operation not permitted, realpath`. The same file is readable
through the shell. No browser connection or browser actions occurred. The
runtime exposes no callable feedback path in this session; this records the
individual tool gap locally. Fix the runtime sandbox's access to its installed
plugin path; do not bypass the mandatory browser surface with another driver.

## Resolved: empty JSON log-reader results (2026-10-18)

- Public C#-to-Rust upgrade evidence export encountered an empty optional event filter. The reader's JSON mode wrote a human status sentence to stdout, breaking JSONL consumers.
- Empty-file and empty-filter status now goes to stderr in JSON mode; stdout remains an empty JSONL stream. Human output is unchanged.
- Two CLI regressions cover both empty paths and the human-readable control. This is tooling-only; it does not change the released application.

## Resolved: failed Rust corrections missing from error audits (2026-10-18)

- The post-release audit found a failed copy in `spellcheck_detail` and `run_completed`, but `logs.py --errors` hid both because it matched only a fixed list of event names.
- Error filtering now includes failure event names and structured correction failure statuses while retaining legacy retry/guard events. It does not interpret successful selected text or evidence-storage diagnostics as correction failures.
- A synthetic CLI regression covers Rust failures, legacy events, successful runs, unrelated diagnostics, and malformed detail payloads. This changes audit tooling only; no production capture behavior changes.

## PR watcher Unicode decoding on Windows (2026-10-18)

- The shared `babysit-pr` watcher failed while decoding GitHub comments with the Windows cp1252 default (`UnicodeDecodeError`, then JSON parsing received `None`). This is a watcher issue, not an app failure.
- Launching it with `PYTHONUTF8=1` restored `WATCH_READY`. The shared helper should explicitly decode GitHub CLI JSON as UTF-8; its source is outside this repository. The active PR follow-through records the required environment setting.


## Null timing samples in the approved reader (2026-10-22, fixed)

- The old `--stats` summed null request stages and treated missing stages as zero.
  Partial failure timings now expose that incompatibility. The reader excludes
  unavailable/nonfinite samples, preserves measured zero, reports sample counts,
  and keeps every attempt in reliability counts. Plain output shows `unavailable`.
- Stdlib regressions live in `tests/test_latency_log_stats.py`; run
  `python -m unittest discover -s tests -p test_latency_log_stats.py`.


## Native baseline setup failure (2026-10-23, attribution pending)

- Three authorized attempts on the receiving machine aborted before posting any
  hotkey. No capture/paste trials ran. The generic foreground/selection error did
  not say which precondition failed. External observation saw a verified selection
  without foreground in one attempt; a later foreground request returned success
  but the fixture still aborted. Do not identify a single cause from those facts.
- The runner now logs `benchmark_setup_failed` with the same readiness snapshot:
  foreground/focus comparisons, selection range, actual/expected UTF-16 counts,
  exact text-match flag and relevant HWNDs. It records no text. `summary.json`
  retains `block_error`, including failures before the first warmup.
- Preserve the guard and failed setup artifacts. A setup abort with zero trials is
  not a fast correction or a valid baseline. Require a new idle window before
  retesting; the prior five-minute window ended with all fixture processes stopped.
