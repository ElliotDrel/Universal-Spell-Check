# Historical C# and Rust measurements

Measurements collected October 16–17, 2026, on one Windows machine. Curated
October 20 from the frozen local evidence. These describe the historical C#
reference and Rust candidate, not the performance of today's production version.

## Contents and verification

- `cases.json` and `fixtures.json`: ten public synthetic inputs and frozen responses.
- `trials.jsonl`: all 2,000 canonical attempts, including warmups and failures.
  Retains case, index, success, quality agreement, timings, output and error.
- `manifests.json`: workload hashes, build provenance, runtime and measurement
  settings. Machine names and local filesystem paths are omitted.
- `summary.json`: counts and successful-only timing distributions recomputed
  from the attempts and checked against the original summaries.
- `startup.json`: all 22 canonical startup samples and their measurement boundary.
- `source-checksums.json`: hashes and relative paths of the source files used.
- `checksums.json`: SHA-256 checksums of the committed data files.

From the repository root, run:

```text
python bench/records/2026-10-rust-port/verify.py
```

This checks data integrity, complete per-case coverage, attempt uniqueness,
failures, warmups and summary statistics without launching either app.

## Results and limits

The October 16 baseline is separate from the October 17 matched comparison.
Its local desktop block includes two measured failures and one warmup failure;
all are retained. Never pool the two dates into one matched sample.

The matched comparison ran C#/Rust/Rust/C# sequentially: 30 measured and ten
warmup attempts per desktop block, 200 measured and 20 warmups per compute block.

| Boundary | C# block medians, ms | Rust block medians, ms |
|---|---:|---:|
| Registered hotkey message through verified editor paste, recorded API output | 256.634 / 262.748 | 89.136 / 87.026 |
| Same desktop boundary with live API | 1,094.421 / 1,079.575 | 888.976 / 838.941 |
| Text processing and rich mapping, no desktop/network | 0.28190 / 0.23405 | 0.09175 / 0.14525 |
| OS process start through hotkey/tray readiness | 596.056 / 636.925 | 80.587 / 75.606 |

All matched desktop attempts and warmups passed editor replacement verification.
Exact handwritten text agreement is separate: the frozen rich-list response
contains extra spaces. Preserve those quality differences rather than treating
pipeline success as perfect text agreement.

Desktop timing begins at acknowledged posted `WM_HOTKEY`, excluding physical
key delivery and human modifier release. It verifies native editor text and
clipboard HTML, not browser visual rendering. Developer logging was enabled;
the full shared evidence store reported `storage_limit`, so this does not
measure normal screenshot capture overhead. Startup samples use warm caches
and exclude installed updater checks and automatic dashboard rendering.

Live API variation prevents attributing the entire live difference to Rust.
Live p95 did not consistently improve. These are observed distributions with
no statistical-significance or cold-boot claim.

## Retention

This portable record is the committed measurement reference. Local frozen
binaries, excluded diagnostic blocks and original source evidence remain under
ignored `benchmark_runs/` for historical replay; they are not required to inspect
or verify this record. They are not part of this Git checkout on another machine.

Superseded `BEFORE` snapshots and the old `usc-history-cleanup-backup` bundle
were removed after curation. That bundle was mostly duplicate private logs and
historical scratch material, not benchmark evidence to publish. No API keys,
settings files, production logs, browser profiles, screenshots or executables
are included here. Existing benchmark code and synthetic cases remain versioned
under `bench/`; new benchmark checkpoints should commit a similarly bounded
record instead of leaving the only usable report in ignored output directories.
