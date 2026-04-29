# Speed Bench Harness

## Overview

`bench/` contains a deterministic-ish end-to-end speed harness that drives the real `SpellcheckCoordinator` via a hidden WinForms `TextBox` and Win32 `SendInput` hotkey injection. It measures wall-clock time from hotkey-press to `TextBox.TextChanged` and records per-phase timings via a `CapturingLogger` interceptor — no mocking, no stubs, no modifications to `src/` at runtime.

The bench uses hotkey `Ctrl+Alt+B` and never touches `Ctrl+Alt+U` (prod) or `Ctrl+Alt+D` (dev), so all three can coexist without collision.

---

## Inputs (`bench/inputs.json`)

20 inputs stored as:

```json
[{"id": "01", "text": "..."}, ...]
```

Three equal-weight categories:

| Category | What it exercises |
|---|---|
| General spelling/grammar | Core correction path |
| Brand-name replacements | `replacements.json` post-processing (`OpenAI`, `GitHub`, `buildPurdue`, etc.) |
| URL protection | Prompt-leak guard; URLs must pass through unchanged |

To refresh inputs from real production logs:

```powershell
python bench/extract_inputs.py
```

---

## Running the bench

```powershell
dotnet build bench/UniversalSpellCheck.Bench.csproj -c Release
dotnet run --project bench/UniversalSpellCheck.Bench.csproj -c Release -- --runs 10 --warmup 2 --variant baseline
```

CLI flags:

| Flag | Default | Description |
|---|---|---|
| `--runs` | required | Number of measured trials |
| `--warmup` | `0` | Warmup trials excluded from stats |
| `--model` | `gpt-4.1-nano` | OpenAI model passed to the coordinator |
| `--variant` | `baseline` | Label embedded in the output filename |

Results are written to `bench/results/{utc}-{sha}-{variant}.json`.

Do not click, type, or switch windows while the bench is running — `SendInput` steals focus and clipboard interference will corrupt results.

---

## Metrics

Per trial, the harness captures:

| Field | Meaning |
|---|---|
| `total_ms` | Hotkey-press → `TextBox.TextChanged` (wall-clock) |
| `coordinator_total_ms` | Inside `SpellcheckCoordinator.RunAsync` start → end |
| `capture_ms` | Clipboard capture phase |
| `request_ms` | OpenAI API round-trip |
| `post_process_ms` | `TextPostProcessor.Process` |
| `paste_ms` | Clipboard write + `SendInput` Ctrl+V |

Per-phase stats reported: median, p95, mean, stddev, min, max. Only successful trials are included in stats — failed trials are counted separately.

---

## Comparing runs

```powershell
python bench/compare.py bench/results/<before>.json bench/results/<after>.json
```

Output shows delta per phase. Interpretation:

| Delta | Signal |
|---|---|
| >= +5% improvement | Real gain (marked ✓) |
| >= +5% regression | Real regression (marked ✗) |
| < 5% either direction | Noise — discard |

Network jitter is irreducible; 10 trials gives ±5% confidence at the median. For clean signal, use `--runs 20` or more.

---

## Optimization workflow

1. Run with `--variant baseline` to capture the current state.
2. Make one `src/` change.
3. Rebuild and re-run with a descriptive `--variant` (e.g. `--variant pre-warm-http2`).
4. Compare the two result files with `compare.py`.

Isolate one variable per variant. Concurrent `src/` changes make comparisons uninterpretable.

---

## Architecture

### `BenchTargetForm`

Hidden borderless WinForms form (`Opacity=0.01`) containing a multiline `TextBox`. Provides the focus target that the coordinator's `Ctrl+C` / `Ctrl+V` clipboard ops are directed at. Opacity is set to `0.01` (not `0`) so the window remains a valid foreground target on Windows.

### `HotkeyInjector`

Calls Win32 `SendInput` to synthesize `Ctrl+Alt+B`. Deliberately avoids `Ctrl+Alt+U` and `Ctrl+Alt+D` so the bench hotkey never collides with prod or dev.

### `CapturingLogger`

Subclasses `DiagnosticsLogger` (`LogData` is `virtual`; `DiagnosticsLogger` is unsealed). Intercepts `spellcheck_detail` events to extract per-phase timing without modifying `SpellcheckCoordinator` or the logging path.

### `BenchHarness`

Drives the per-trial loop. Uses a `_pasteExpected` flag — not value equality — to distinguish the coordinator's paste from `BenchHarness`'s own `LoadAndSelect` write into the `TextBox`. This correctly handles inputs where the corrected text equals the input (no-correction cases).

Warmup trials fire the full pipeline to prime the HTTP/2 connection pool and JIT; they are discarded before stats are computed.

---

## Prod `src/` changes made to support the bench

These changes were made to `src/` to allow the bench to compose against the real coordinator without forking it. They are intentional and should be preserved.

| File | Change |
|---|---|
| `DiagnosticsLogger.cs` | Class unsealed; `LogData` made `virtual` |
| `OpenAiSpellcheckService.cs` | `Model` const renamed to `DefaultModel`; new 3-arg constructor `(CachedSettings, DiagnosticsLogger, string model)` added; prod default preserved |
| `HotkeyWindow.cs` | `Register()` signature changed to `Register(uint modifiers, uint vk)`; prod call updated to pass `BuildChannel.HotkeyModifiers, BuildChannel.HotkeyVk` |
