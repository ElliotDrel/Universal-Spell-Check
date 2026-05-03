# Speed Bench Harness

## Overview

`bench/` contains a deterministic speed harness that drives the real `SpellcheckCoordinator` and records per-phase timings via a `CapturingLogger` interceptor — no mocking, no stubs, no modifications to `src/` at runtime.

Two modes:

| Mode | How it works | When to use |
|---|---|---|
| **Headless** (default) | Calls `SpellcheckCoordinator.RunHeadlessAsync` directly — no clipboard, no paste, no focus required. You can keep using your PC. | Baselines, optimization comparisons — the common case. |
| **E2E** (`--e2e`) | Uses a hidden `BenchTargetForm` + Win32 `SendInput` to fire `Ctrl+Alt+B`, driving the full clipboard-capture → paste path. Requires the PC to be idle. | Measuring clipboard/paste overhead or verifying the complete real-world flow. |

In headless mode `total_ms ≈ request_ms` — clipboard and paste phases read as 0ms. In e2e mode all phases are captured.

The e2e bench uses hotkey `Ctrl+Alt+B` and never touches `Ctrl+Alt+U` (prod) or `Ctrl+Alt+D` (dev).

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

**Headless (default — keep using your PC):**

```powershell
dotnet run --project bench/UniversalSpellCheck.Bench.csproj -- --runs 10 --warmup 2 --variant baseline-headless
```

**E2E (full clipboard + paste path — PC must be idle):**

```powershell
dotnet run --project bench/UniversalSpellCheck.Bench.csproj -- --runs 10 --warmup 2 --variant baseline-e2e --e2e
```

CLI flags:

| Flag | Default | Description |
|---|---|---|
| `--runs` | `10` | Number of measured trials per input |
| `--warmup` | `2` | Warmup trials excluded from stats |
| `--model` | `gpt-4.1-nano` | OpenAI model passed to the coordinator |
| `--variant` | `baseline` | Label embedded in the output filename |
| `--e2e` | off | Enable full e2e mode (SendInput + clipboard) |

Results are written to `bench/results/{utc}-{sha}-{variant}.json`.

In e2e mode: do not click, type, or switch windows while the bench is running — `SendInput` steals focus and clipboard interference will corrupt results.

---

## Metrics

Per trial, the harness captures:

| Field | Headless | E2E | Meaning |
|---|---|---|---|
| `total_ms` | ✓ | ✓ | Full pipeline wall-clock (coordinator start → end) |
| `coordinator_total_ms` | ✓ | ✓ | Same as total in headless; hotkey → HotPathReturned in e2e |
| `capture_ms` | 0 | ✓ | Clipboard capture phase (Ctrl+C loop) |
| `request_ms` | ✓ | ✓ | OpenAI API round-trip |
| `post_process_ms` | ✓ | ✓ | `TextPostProcessor.Process` (replacements + prompt-leak guard) |
| `paste_ms` | 0 | ✓ | Clipboard write + `SendInput` Ctrl+V |

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

1. Run headless with `--variant baseline-headless` to capture the current state.
2. Make one `src/` change.
3. Rebuild and re-run headless with a descriptive `--variant` (e.g. `--variant http2-keepalive`).
4. Compare with `compare.py`.

Isolate one variable per variant. Concurrent `src/` changes make comparisons uninterpretable.

Use headless for all optimization work — it's faster, deterministic, and removes focus/clipboard noise. Use `--e2e` only when you specifically want to measure the clipboard or paste phases.

---

## Architecture

### `BenchTargetForm` (e2e only)

Hidden borderless WinForms form (`Opacity=0.01`) containing a multiline `TextBox`. Provides the focus target that the coordinator's `Ctrl+C` / `Ctrl+V` clipboard ops are directed at. Opacity is set to `0.01` (not `0`) so the window remains a valid foreground target on Windows. Not created in headless mode.

### `HotkeyInjector` (e2e only)

Calls Win32 `SendInput` to synthesize `Ctrl+Alt+B`. Deliberately avoids `Ctrl+Alt+U` and `Ctrl+Alt+D` so the bench hotkey never collides with prod or dev. Not used in headless mode.

### `CapturingLogger`

Subclasses `DiagnosticsLogger` (`LogData` is `virtual`; `DiagnosticsLogger` is unsealed). Intercepts `spellcheck_detail` events to extract per-phase timing without modifying `SpellcheckCoordinator` or the logging path. Used by both modes.

### `BenchHarness`

Drives the per-trial loop. Dispatches to `HeadlessTrialAsync` or `E2eTrialAsync` based on the `--e2e` flag.

- **Headless path:** calls `coordinator.RunHeadlessAsync(text)` directly, then polls for `_lastTrialTimings` (populated by `CapturingLogger` when `FinalizeAsync` fires).
- **E2e path:** uses a `_pasteExpected` flag — not value equality — to distinguish the coordinator's paste from `LoadAndSelect` writes into the `TextBox`. This correctly handles no-correction inputs where output text equals input.

Warmup trials fire the full pipeline to prime the HTTP/2 connection pool and JIT; they are discarded before stats are computed.

---

## Prod `src/` changes made to support the bench

These changes were made to `src/` to allow the bench to compose against the real coordinator without forking it. They are intentional and should be preserved.

| File | Change |
|---|---|
| `DiagnosticsLogger.cs` | Class unsealed; `LogData` made `virtual` |
| `OpenAiSpellcheckService.cs` | `Model` const renamed to `DefaultModel`; new 3-arg constructor `(CachedSettings, DiagnosticsLogger, string model)` added; prod default preserved |
| `HotkeyWindow.cs` | `Register()` signature changed to `Register(uint modifiers, uint vk)`; prod call updated to pass `BuildChannel.HotkeyModifiers, BuildChannel.HotkeyVk` |
| `SpellcheckCoordinator.cs` | `RunHeadlessAsync(string inputText)` and `ExecuteHeadlessAsync` added — skips clipboard capture and paste, runs API + post-process, fires `FinalizeAsync` for timing capture |
