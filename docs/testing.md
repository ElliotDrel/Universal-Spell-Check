# Testing

Three layers, cheapest first. Test at the lowest layer that would fail, and extend an existing
table before adding a new test.

## 1. Unit and table tests (`cargo test`)

Run for both channels: `cargo test --manifest-path rust/Cargo.toml`, then again with
`--no-default-features`. The tables that matter:

- `rust/tests/fixtures/formatting-contracts.jsonl`: rich-text and Docs native cases with exact expected text, HTML and mode.
- `bench/cases.json` + `bench/replay.json`: the ten bench cases with recorded model output and the exact paste each must produce. `frozen_baseline_processing_matches` checks them without a desktop.
- `bench/perf-budget.json`: the request size of each bench case (see the gate below).

No test touches the real clipboard, settings, keys or network. Live API tests are `#[ignore]`d
and need an explicit environment variable.

## 2. End to end (`--bench`)

```
cargo build --manifest-path rust/Cargo.toml --release
rust/target/release/universal-spell-check.exe --bench [--runs 10] [--warmup 2] [--case <id>] [--live]
```

This drives the whole app the way a user does. It opens an editor window, selects each case,
presses the real Ctrl+Alt+F24 chord with `SendInput` and lets the production worker copy the
selection (served as text plus HTML, like a rich app), correct it, rebuild formatting and paste
with Ctrl+V. Each trial passes only when the editor received exactly the expected text and the
clipboard carried exactly the expected HTML. It prints per-case medians of `user_ms`
(keypress to paste landing) and the main phases, writes `summary.json`, and exits nonzero on any
failure.

- **Replay (default)**: the API answer comes from `bench/replay.json`. Free, deterministic and safe; it uses isolated settings and no key.
- **`--live`**: real requests with the build's channel key and model, read only. Dev usually has no key, so build with `--no-default-features` to use Prod's. Use it for model, prompt or network work; `api_ms` then dominates (about 1.6 s of a 1.7 s correction). Successful answers are saved to `recorded.json` in `bench/replay.json` format.
- It needs an unlocked, idle desktop and takes the foreground for about 10 seconds; typing during a run makes it fail rather than corrupt anything. Tray, dashboard and overlay are not drawn.
- To add a case: append it to `bench/cases.json`, run `--live --case <id>` once, copy its entry from `recorded.json` into `bench/replay.json`, and regenerate the budget.

Replay local latency is about 65 ms, most of it the 50 ms clipboard settle inside `paste_ms`.
Real-use latency is about 95% API time; check it with `logs.py --stats`.

## 3. Performance gate (CI)

`.github/workflows/ci.yml` copies T3 Code's budget pattern. Every PR runs:

- **`perf-budget`**: fails if any value in `bench/perf-budget.json` is larger than on the base branch. `perf_budget_matches_request_bytes` keeps that file equal to the real payload size, so it can't be edited down. Request size drives API time, which the replay bench can't see. After an intended payload change, regenerate it with `USC_UPDATE_BUDGET=1 cargo test perf_budget`.
- **`perf`**: builds the base branch (in its own checkout) and the PR in Prod mode on the same Windows runner and runs `python bench/perf.py --base <base.exe> --base-root base --candidate <pr.exe>`, so each build is checked against its own `bench/replay.json`. It alternates the two builds over four rounds so machine noise hits both, posts the per-case table as a PR comment, and fails when the geometric mean of the per-case ratios is slower by more than both 3 ms and 5% (one case doubling moves it about 7%; noise in one case averages out). Identical builds differ by under 1 ms.

Either gate can be overridden only by Elliot adding the `perf-regression-accepted` label. Run
`perf.py` locally before pushing a hot-path change.

## Manual checks still worth doing

After changes to capture, paste or a specific app's formatting, use the Dev build (Ctrl+Alt+D)
in the real target app (Notepad, Chrome textareas, ChatGPT, Google Docs, T3 Code, a terminal),
then read the run with `read-logs`. The update flow has no automated test: the `deploy` skill's
post-release install and version check is its only coverage, so test update changes by hand,
including a restart with the feed unreachable after a download.
