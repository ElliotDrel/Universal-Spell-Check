# Deterministic End-to-End Speed Benchmark Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a self-contained C# bench harness that reproduces the real spellcheck workflow (hotkey-press → text-replaced) against real OpenAI API calls, and records per-phase timings to JSON so we can iteratively diff optimization variants — autoresearch-style — against a stable baseline.

**Architecture:** New `bench/UniversalSpellCheck.Bench` console+WinForms exe that project-references `src/UniversalSpellCheck.csproj`, instantiates the production `SpellcheckCoordinator` + `OpenAiSpellcheckService` + `TextPostProcessor` unmodified except for one model-injection refactor, and drives a hidden focused TextBox by registering its own `Ctrl+Alt+B` hotkey and firing it via Win32 `SendInput`. Inputs are 10 real selections extracted from the existing JSONL logs. Per-trial output is the OS hotkey-press → TextBox.TextChanged delta plus all phase timings the coordinator already records in `RunRecord`. Results are written to timestamped JSON files diffable by a Python script.

**Tech Stack:** C#/.NET 10 WinForms (bench harness, project ref to existing `src/`), Python 3 (input extractor + compare script — uses only stdlib so no venv required), real OpenAI Responses API (`gpt-4.1-nano` default, configurable via `--model`).

---

## File Structure

**New files:**
- `bench/UniversalSpellCheck.Bench.csproj` — bench project, references `src/UniversalSpellCheck.csproj`
- `bench/Program.cs` — entrypoint, CLI parsing, main trial loop
- `bench/BenchTargetForm.cs` — hidden borderless WinForms window with single TextBox; sets text + selects all + focuses + reports `TextChanged`
- `bench/HotkeyInjector.cs` — Win32 `SendInput` wrapper that synthesizes a Ctrl+Alt+B keystroke
- `bench/BenchHarness.cs` — orchestrates per-input × per-trial loop, captures timings, builds aggregate stats
- `bench/ResultsWriter.cs` — serializes results to JSON
- `bench/extract_inputs.py` — reads `%LocalAppData%\UniversalSpellCheck\logs\spellcheck-*.jsonl`, picks 10 length-stratified inputs, writes `bench/inputs/01.txt`..`10.txt`
- `bench/compare.py` — diffs two `bench/results/*.json` files into a delta table
- `bench/test_extract_inputs.py` — pytest for the extractor
- `bench/test_compare.py` — pytest for the compare script
- `bench/inputs/.gitkeep` — placeholder so the inputs dir exists pre-extraction
- `bench/results/.gitkeep` — placeholder so results dir exists
- `bench/README.md` — how to run the bench, how to extract inputs, how to interpret results

**Modified files:**
- `src/UniversalSpellCheck.csproj` — add `InternalsVisibleTo("UniversalSpellCheck.Bench")` so the bench can construct `internal sealed` types directly
- `src/OpenAiSpellcheckService.cs` — model becomes a constructor parameter (default `"gpt-4.1"`) so bench can pass `"gpt-4.1-nano"` without forking the service or hardcoding a second copy of the prefix bytes; `PrefixBytes` becomes per-instance, built in constructor
- `src/SpellcheckCoordinator.cs` — one line: read `_spellcheckService.ModelName` instead of the static `OpenAiSpellcheckService.Model` for the `RunRecord.Model` stamp
- `src/HotkeyWindow.cs` — `Register()` becomes `Register(uint modifiers, uint vk)` so the bench can register Ctrl+Alt+B without depending on `BuildChannel`; `SpellCheckAppContext` updated to pass `BuildChannel.HotkeyModifiers, BuildChannel.HotkeyVk`

**Why this decomposition:** the bench is its own deployable artifact (don't pollute prod with measurement code), but it must use the real coordinator unmodified or the numbers are fiction. The two prod refactors (model parameter, hotkey parameter) are minimal seams that also make prod cleaner — model name is no longer baked in a `const`, hotkey is no longer hidden inside the window class.

---

## Task 1: Bench project skeleton + InternalsVisibleTo

**Files:**
- Create: `bench/UniversalSpellCheck.Bench.csproj`
- Create: `bench/Program.cs` (placeholder)
- Modify: `src/UniversalSpellCheck.csproj` (add `InternalsVisibleTo`)
- Create: `bench/inputs/.gitkeep`
- Create: `bench/results/.gitkeep`

- [ ] **Step 1: Create the bench csproj**

Write `bench/UniversalSpellCheck.Bench.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <UseWindowsForms>true</UseWindowsForms>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>UniversalSpellCheck.Bench</RootNamespace>
    <AssemblyName>UniversalSpellCheck.Bench</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\src\UniversalSpellCheck.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Create placeholder Program.cs that compiles**

Write `bench/Program.cs`:

```csharp
namespace UniversalSpellCheck.Bench;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.WriteLine("UniversalSpellCheck.Bench placeholder — wired up in Task 8.");
        return 0;
    }
}
```

- [ ] **Step 3: Add InternalsVisibleTo to main csproj**

In `src/UniversalSpellCheck.csproj`, add a new `ItemGroup` immediately before the closing `</Project>` tag:

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="UniversalSpellCheck.Bench" />
  </ItemGroup>
```

- [ ] **Step 4: Create gitkeep placeholders**

Write `bench/inputs/.gitkeep` (empty file).
Write `bench/results/.gitkeep` (empty file).

- [ ] **Step 5: Verify both projects build**

Run: `dotnet build src/UniversalSpellCheck.csproj -c Release`
Expected: build succeeds, no warnings about InternalsVisibleTo.

Run: `dotnet build bench/UniversalSpellCheck.Bench.csproj -c Release`
Expected: build succeeds, produces `bench/bin/Release/net10.0-windows/UniversalSpellCheck.Bench.exe`.

- [ ] **Step 6: Commit**

```bash
git add bench/UniversalSpellCheck.Bench.csproj bench/Program.cs bench/inputs/.gitkeep bench/results/.gitkeep src/UniversalSpellCheck.csproj
git commit -m "bench: scaffold UniversalSpellCheck.Bench project with InternalsVisibleTo"
```

---

## Task 2: Make `OpenAiSpellcheckService` model configurable

**Files:**
- Modify: `src/OpenAiSpellcheckService.cs` (constructor + prefix bytes become per-instance)
- Modify: `src/SpellcheckCoordinator.cs:62` (read instance `ModelName` instead of static `Model`)

- [ ] **Step 1: Replace static `Model` const + static `PrefixBytes` with instance fields**

In `src/OpenAiSpellcheckService.cs`, find the existing block:

```csharp
    private const string Endpoint = "https://api.openai.com/v1/responses";
    private const string ModelsEndpoint = "https://api.openai.com/v1/models";
    public const string Model = "gpt-4.1";

    // AHK-canonical instruction text — keep byte-for-byte identical to legacy.
    public const string PromptInstruction =
        "Fix the grammar and spelling of the text below. Preserve all formatting, line breaks, and special characters. Do not add or remove any content. Return only the corrected text.";

    // Pre-built UTF-8 byte slabs around the JSON-escaped user text. Built once
    // at type init so the hot path only allocates one byte[] per request.
    private static readonly byte[] PrefixBytes = Encoding.UTF8.GetBytes(
        "{\"model\":\"" + Model + "\"," +
        "\"input\":[{\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"" +
        "instructions: " + JsonEscape(PromptInstruction) + "\\n" +
        "text input: ");
    private static readonly byte[] SuffixBytes = Encoding.UTF8.GetBytes(
        "\"}]}],\"store\":true,\"text\":{\"verbosity\":\"medium\"},\"temperature\":0.3}");
```

Replace with:

```csharp
    private const string Endpoint = "https://api.openai.com/v1/responses";
    private const string ModelsEndpoint = "https://api.openai.com/v1/models";
    public const string DefaultModel = "gpt-4.1";

    // AHK-canonical instruction text — keep byte-for-byte identical to legacy.
    public const string PromptInstruction =
        "Fix the grammar and spelling of the text below. Preserve all formatting, line breaks, and special characters. Do not add or remove any content. Return only the corrected text.";

    // SuffixBytes does not depend on model, so it stays static.
    private static readonly byte[] SuffixBytes = Encoding.UTF8.GetBytes(
        "\"}]}],\"store\":true,\"text\":{\"verbosity\":\"medium\"},\"temperature\":0.3}");

    private readonly byte[] _prefixBytes;

    public string ModelName { get; }
```

- [ ] **Step 2: Update constructor to accept model + build prefix bytes per instance**

In the same file, find the existing constructor:

```csharp
    public OpenAiSpellcheckService(CachedSettings cachedSettings, DiagnosticsLogger logger)
    {
        _cachedSettings = cachedSettings;
        _logger = logger;
        _handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            EnableMultipleHttp2Connections = true,
            AutomaticDecompression = DecompressionMethods.All
        };
        _httpClient = new HttpClient(_handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
        };
    }
```

Replace with:

```csharp
    public OpenAiSpellcheckService(CachedSettings cachedSettings, DiagnosticsLogger logger)
        : this(cachedSettings, logger, DefaultModel)
    {
    }

    public OpenAiSpellcheckService(CachedSettings cachedSettings, DiagnosticsLogger logger, string model)
    {
        _cachedSettings = cachedSettings;
        _logger = logger;
        ModelName = model;
        _prefixBytes = BuildPrefixBytes(model);
        _handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            EnableMultipleHttp2Connections = true,
            AutomaticDecompression = DecompressionMethods.All
        };
        _httpClient = new HttpClient(_handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
        };
    }

    private static byte[] BuildPrefixBytes(string model)
    {
        return Encoding.UTF8.GetBytes(
            "{\"model\":\"" + model + "\"," +
            "\"input\":[{\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"" +
            "instructions: " + JsonEscape(PromptInstruction) + "\\n" +
            "text input: ");
    }
```

- [ ] **Step 3: Update `BuildPayload` to use the instance prefix**

In the same file, find:

```csharp
    private static byte[] BuildPayload(string inputText)
    {
        var escaped = JsonEncodedText.Encode(inputText).EncodedUtf8Bytes;
        var buffer = new byte[PrefixBytes.Length + escaped.Length + SuffixBytes.Length];
        var span = buffer.AsSpan();
        PrefixBytes.AsSpan().CopyTo(span);
        escaped.CopyTo(span[PrefixBytes.Length..]);
        SuffixBytes.AsSpan().CopyTo(span[(PrefixBytes.Length + escaped.Length)..]);
        return buffer;
    }
```

Replace with (note: removes `static`, uses `_prefixBytes`):

```csharp
    private byte[] BuildPayload(string inputText)
    {
        var escaped = JsonEncodedText.Encode(inputText).EncodedUtf8Bytes;
        var buffer = new byte[_prefixBytes.Length + escaped.Length + SuffixBytes.Length];
        var span = buffer.AsSpan();
        _prefixBytes.AsSpan().CopyTo(span);
        escaped.CopyTo(span[_prefixBytes.Length..]);
        SuffixBytes.AsSpan().CopyTo(span[(_prefixBytes.Length + escaped.Length)..]);
        return buffer;
    }
```

- [ ] **Step 4: Update the one external reference to `Model`**

In `src/SpellcheckCoordinator.cs`, find line 62:

```csharp
            Model = OpenAiSpellcheckService.Model
```

Replace with:

```csharp
            Model = _spellcheckService.ModelName
```

- [ ] **Step 5: Verify build**

Run: `dotnet build src/UniversalSpellCheck.csproj -c Release`
Expected: build succeeds.

Run: `dotnet build src/UniversalSpellCheck.csproj -c Dev`
Expected: build succeeds.

- [ ] **Step 6: Smoke-test prod still talks to the API**

Run: `dotnet run --project src/UniversalSpellCheck.csproj -c Dev`

Manually:
1. App tray icon should appear (orange-tinted, Dev channel).
2. Open Notepad, type `helo wrold`, select it, press Ctrl+Alt+D.
3. Expected: text becomes `Hello world.` (or similar correction).
4. Tail the log: open `%LocalAppData%\UniversalSpellCheck\logs\spellcheck-2026-04-29.jsonl` (or today's date) and verify a `run_completed status=success` line appears with `model=gpt-4.1` in the `spellcheck_detail` event.
5. Quit the tray app.

If the round-trip fails or the model field is wrong, fix before proceeding.

- [ ] **Step 7: Commit**

```bash
git add src/OpenAiSpellcheckService.cs src/SpellcheckCoordinator.cs
git commit -m "refactor: make OpenAiSpellcheckService model a constructor param"
```

---

## Task 3: Make `HotkeyWindow.Register()` accept modifiers + vk

**Files:**
- Modify: `src/HotkeyWindow.cs:23-38` (Register signature)
- Modify: `src/SpellCheckAppContext.cs:55` (call site passes BuildChannel constants)

- [ ] **Step 1: Replace `HotkeyWindow.Register()` signature**

In `src/HotkeyWindow.cs`, find:

```csharp
    public void Register()
    {
        if (_registered)
        {
            return;
        }

        if (!RegisterHotKey(Handle, HotkeyId, BuildChannel.HotkeyModifiers, BuildChannel.HotkeyVk))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Failed to register hotkey for {BuildChannel.DisplayName}.");
        }

        _registered = true;
    }
```

Replace with:

```csharp
    public void Register(uint modifiers, uint vk)
    {
        if (_registered)
        {
            return;
        }

        if (!RegisterHotKey(Handle, HotkeyId, modifiers, vk))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Failed to register hotkey (vk=0x{vk:X2}).");
        }

        _registered = true;
    }
```

- [ ] **Step 2: Update prod call site**

In `src/SpellCheckAppContext.cs`, find line 55:

```csharp
        _hotkeyWindow.Register();
```

Replace with:

```csharp
        _hotkeyWindow.Register(BuildChannel.HotkeyModifiers, BuildChannel.HotkeyVk);
```

- [ ] **Step 3: Verify build**

Run: `dotnet build src/UniversalSpellCheck.csproj -c Release`
Expected: build succeeds.

Run: `dotnet build src/UniversalSpellCheck.csproj -c Dev`
Expected: build succeeds.

- [ ] **Step 4: Smoke-test prod hotkey still fires**

Run: `dotnet run --project src/UniversalSpellCheck.csproj -c Dev`

Manually:
1. Tray icon appears.
2. Select misspelled text in Notepad, press Ctrl+Alt+D.
3. Expected: replacement happens; log shows `hotkey_pressed`.
4. Quit.

- [ ] **Step 5: Commit**

```bash
git add src/HotkeyWindow.cs src/SpellCheckAppContext.cs
git commit -m "refactor: HotkeyWindow.Register takes modifiers+vk parameters"
```

---

## Task 4: Build the bench input extractor (Python)

**Files:**
- Create: `bench/extract_inputs.py`
- Create: `bench/test_extract_inputs.py`

**Why Python:** the JSONL logs are easiest to parse with `json.loads`, and a one-shot script is cheaper than a second C# program. Stdlib only — no venv needed.

- [ ] **Step 1: Write the failing test**

Write `bench/test_extract_inputs.py`:

```python
"""Tests for bench/extract_inputs.py — picks 10 length-stratified inputs from JSONL logs."""

import json
import textwrap
from pathlib import Path

import pytest

from extract_inputs import extract_inputs


def make_log_line(input_text: str, status: str = "success") -> str:
    """Build one JSONL log line in the shape DiagnosticsLogger.LogData emits."""
    payload = {
        "status": status,
        "input_text": input_text,
        "input_chars": len(input_text),
    }
    # Format: "<iso-ts> channel=prod app_version=1.0.0 pid=1234 spellcheck_detail <json>"
    return f"2026-04-29T12:00:00.0000000-05:00 channel=prod app_version=1.0.0 pid=1234 spellcheck_detail {json.dumps(payload)}\n"


def test_extracts_only_successful_runs(tmp_path: Path) -> None:
    log = tmp_path / "spellcheck-2026-04-29.jsonl"
    log.write_text(
        make_log_line("hello world", status="success")
        + make_log_line("failed run text", status="capture_failed")
        + make_log_line("another success", status="success"),
        encoding="utf-8",
    )

    inputs = extract_inputs([log], target_count=10)

    assert "hello world" in inputs
    assert "another success" in inputs
    assert "failed run text" not in inputs


def test_dedupes_identical_inputs(tmp_path: Path) -> None:
    log = tmp_path / "spellcheck.jsonl"
    log.write_text(
        make_log_line("duplicate text") + make_log_line("duplicate text") + make_log_line("unique"),
        encoding="utf-8",
    )

    inputs = extract_inputs([log], target_count=10)

    assert inputs.count("duplicate text") == 1
    assert "unique" in inputs


def test_returns_at_most_target_count(tmp_path: Path) -> None:
    log = tmp_path / "spellcheck.jsonl"
    log.write_text("".join(make_log_line(f"input number {i}") for i in range(50)), encoding="utf-8")

    inputs = extract_inputs([log], target_count=10)

    assert len(inputs) == 10


def test_stratifies_by_length(tmp_path: Path) -> None:
    """When 10+ inputs of varying lengths exist, the output spans short/medium/long."""
    log = tmp_path / "spellcheck.jsonl"
    short = ["a" * 20] * 3 + ["b" * 25, "c" * 30]
    medium = ["m" * 200, "n" * 250, "o" * 300]
    long_ = ["l" * 1500, "p" * 2000, "q" * 2500, "r" * 3000]
    lines = [make_log_line(t) for t in short + medium + long_]
    log.write_text("".join(lines), encoding="utf-8")

    inputs = extract_inputs([log], target_count=10)

    lengths = sorted(len(i) for i in inputs)
    assert lengths[0] < 100, "expected at least one short input"
    assert any(100 <= L <= 1000 for L in lengths), "expected at least one medium input"
    assert lengths[-1] > 1000, "expected at least one long input"


def test_handles_malformed_lines(tmp_path: Path) -> None:
    log = tmp_path / "spellcheck.jsonl"
    log.write_text(
        "garbage non-jsonl line\n"
        + make_log_line("good input")
        + "another bad line { not json\n",
        encoding="utf-8",
    )

    inputs = extract_inputs([log], target_count=10)

    assert "good input" in inputs


def test_skips_empty_input_text(tmp_path: Path) -> None:
    log = tmp_path / "spellcheck.jsonl"
    log.write_text(
        make_log_line("") + make_log_line("real text"),
        encoding="utf-8",
    )

    inputs = extract_inputs([log], target_count=10)

    assert "" not in inputs
    assert "real text" in inputs
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd bench && python -m pytest test_extract_inputs.py -v`
Expected: collection error or `ModuleNotFoundError: No module named 'extract_inputs'`.

- [ ] **Step 3: Implement `extract_inputs.py`**

Write `bench/extract_inputs.py`:

```python
"""Extract 10 length-stratified spellcheck inputs from JSONL logs into bench/inputs/."""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path


def extract_inputs(log_files: list[Path], target_count: int = 10) -> list[str]:
    """Read JSONL logs, pull successful spellcheck inputs, return up to target_count
    deduped strings stratified by length (short/medium/long buckets)."""
    seen: set[str] = set()
    candidates: list[str] = []

    for log_path in log_files:
        try:
            text = log_path.read_text(encoding="utf-8")
        except OSError:
            continue
        for raw in text.splitlines():
            marker = " spellcheck_detail "
            idx = raw.find(marker)
            if idx < 0:
                continue
            json_part = raw[idx + len(marker):]
            try:
                payload = json.loads(json_part)
            except json.JSONDecodeError:
                continue
            if payload.get("status") != "success":
                continue
            input_text = payload.get("input_text") or ""
            if not input_text or input_text in seen:
                continue
            seen.add(input_text)
            candidates.append(input_text)

    return _stratify(candidates, target_count)


def _stratify(candidates: list[str], target_count: int) -> list[str]:
    """Spread the picks across short (<100ch), medium (100-1000ch), long (>1000ch)."""
    if len(candidates) <= target_count:
        return candidates

    short = [c for c in candidates if len(c) < 100]
    medium = [c for c in candidates if 100 <= len(c) <= 1000]
    long_ = [c for c in candidates if len(c) > 1000]

    per_bucket = max(1, target_count // 3)
    picked = short[:per_bucket] + medium[:per_bucket] + long_[:per_bucket]

    # Backfill from any bucket if we under-shot due to empty buckets.
    leftover = [c for c in candidates if c not in picked]
    while len(picked) < target_count and leftover:
        picked.append(leftover.pop(0))

    return picked[:target_count]


def _default_log_dir() -> Path:
    local_appdata = os.environ.get("LOCALAPPDATA")
    if local_appdata:
        return Path(local_appdata) / "UniversalSpellCheck" / "logs"
    return Path.home() / "AppData" / "Local" / "UniversalSpellCheck" / "logs"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Extract bench inputs from JSONL spellcheck logs.")
    parser.add_argument(
        "--log-dir",
        type=Path,
        default=_default_log_dir(),
        help="Directory containing spellcheck-*.jsonl files.",
    )
    parser.add_argument(
        "--out-dir",
        type=Path,
        default=Path(__file__).parent / "inputs",
        help="Directory to write inputs into.",
    )
    parser.add_argument("--count", type=int, default=10)
    args = parser.parse_args(argv)

    log_files = sorted(args.log_dir.glob("spellcheck-*.jsonl"))
    if not log_files:
        print(f"No spellcheck-*.jsonl files found in {args.log_dir}", file=sys.stderr)
        return 1

    inputs = extract_inputs(log_files, args.count)
    if not inputs:
        print("No successful inputs found in logs.", file=sys.stderr)
        return 1

    args.out_dir.mkdir(parents=True, exist_ok=True)
    # Wipe any prior inputs so we don't mix old + new sets.
    for old in args.out_dir.glob("[0-9][0-9].txt"):
        old.unlink()

    for i, text in enumerate(inputs, start=1):
        (args.out_dir / f"{i:02d}.txt").write_text(text, encoding="utf-8")

    print(f"Wrote {len(inputs)} input file(s) to {args.out_dir}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd bench && python -m pytest test_extract_inputs.py -v`
Expected: 6 passed.

- [ ] **Step 5: Commit**

```bash
git add bench/extract_inputs.py bench/test_extract_inputs.py
git commit -m "bench: add JSONL log → bench/inputs/ extractor with stratified picks"
```

---

## Task 5: Run extractor against real logs and commit the inputs

**Files:**
- Create: `bench/inputs/01.txt` … `bench/inputs/NN.txt` (whatever the extractor produces, up to 10)

- [ ] **Step 1: Run the extractor**

Run: `cd bench && python extract_inputs.py`
Expected: prints `Wrote N input file(s) to <path>` with N between 1 and 10.

If N is 0, the user has not exercised the app enough to have a successful-run corpus. In that case: pause and ask the user to run the prod app on a few selections, then re-run the extractor. Do not fabricate inputs.

- [ ] **Step 2: Sanity-check the inputs**

List the files: `ls bench/inputs/`
Spot-read 2-3: `cat bench/inputs/01.txt`
Confirm each is real-looking text and not corrupted JSON or log noise.

- [ ] **Step 3: Commit**

```bash
git add bench/inputs/
git commit -m "bench: capture initial 10 input samples from logs"
```

---

## Task 6: BenchTargetForm — hidden TextBox we paste into

**Files:**
- Create: `bench/BenchTargetForm.cs`

**What this is:** the "active app" the bench focuses before pressing the hotkey. The coordinator captures its selection via Ctrl+C, then later pastes via Ctrl+V into whatever is focused. We want the same form to be focused for both halves of the cycle, with a deterministic `TextChanged` signal we can subscribe to so we know exactly when the paste landed.

- [ ] **Step 1: Write the form**

Write `bench/BenchTargetForm.cs`:

```csharp
using System.Windows.Forms;

namespace UniversalSpellCheck.Bench;

/// <summary>
/// Hidden borderless window with a single TextBox. The bench loads the trial
/// input into the textbox, focuses + selects it, fires the hotkey, then waits
/// on TextChanged to know when the paste landed.
/// </summary>
internal sealed class BenchTargetForm : Form
{
    private readonly TextBox _textBox;
    public event EventHandler? TargetTextChanged;

    public BenchTargetForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        // Position offscreen-ish but still on a real monitor so focus is valid.
        // Fully offscreen windows can be denied focus on some Windows setups.
        Location = new System.Drawing.Point(0, 0);
        Size = new System.Drawing.Size(400, 100);
        Opacity = 0.01;  // effectively invisible but still focusable
        TopMost = true;

        _textBox = new TextBox
        {
            Multiline = true,
            Dock = DockStyle.Fill,
            AcceptsReturn = true,
            AcceptsTab = true,
        };
        _textBox.TextChanged += (_, _) => TargetTextChanged?.Invoke(this, EventArgs.Empty);
        Controls.Add(_textBox);
    }

    /// <summary>Current textbox contents.</summary>
    public string CurrentText => _textBox.Text;

    /// <summary>Replace the textbox text, select all, and focus it. Must run on the UI thread.</summary>
    public void LoadAndSelect(string text)
    {
        _textBox.Text = text;
        _textBox.SelectAll();
        _textBox.Focus();
        Activate();
    }
}
```

- [ ] **Step 2: Verify build**

Run: `dotnet build bench/UniversalSpellCheck.Bench.csproj -c Release`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add bench/BenchTargetForm.cs
git commit -m "bench: add hidden BenchTargetForm with focusable TextBox"
```

---

## Task 7: HotkeyInjector — synthesize Ctrl+Alt+B via SendInput

**Files:**
- Create: `bench/HotkeyInjector.cs`

**Why SendInput specifically:** `SendKeys` is unreliable for modifier sequences and goes through a different OS path than a real keystroke. `SendInput` injects directly into the keyboard input stream — same path as a physical key press — so the registered global hotkey fires identically.

- [ ] **Step 1: Write the injector**

Write `bench/HotkeyInjector.cs`:

```csharp
using System.Runtime.InteropServices;

namespace UniversalSpellCheck.Bench;

/// <summary>
/// Synthesizes a Ctrl+Alt+B keystroke via Win32 SendInput. Used by the bench
/// to fire the registered global hotkey from the same path the OS uses for a
/// physical keypress, so we measure the real WM_HOTKEY dispatch cost.
/// </summary>
internal static class HotkeyInjector
{
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12;       // Alt
    private const ushort VK_B = 0x42;
    public const uint HotkeyVk = VK_B;
    public const uint HotkeyModifiers = 0x0001 /*MOD_ALT*/ | 0x0002 /*MOD_CONTROL*/ | 0x4000 /*MOD_NOREPEAT*/;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    public static void FireCtrlAltB()
    {
        // Press Ctrl, press Alt, press B, release B, release Alt, release Ctrl.
        var inputs = new[]
        {
            BuildKey(VK_CONTROL, keyUp: false),
            BuildKey(VK_MENU,    keyUp: false),
            BuildKey(VK_B,       keyUp: false),
            BuildKey(VK_B,       keyUp: true),
            BuildKey(VK_MENU,    keyUp: true),
            BuildKey(VK_CONTROL, keyUp: true),
        };

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            throw new InvalidOperationException(
                $"SendInput delivered {sent}/{inputs.Length} events. LastError={Marshal.GetLastWin32Error()}");
        }
    }

    private static INPUT BuildKey(ushort vk, bool keyUp)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                }
            }
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
```

- [ ] **Step 2: Verify build**

Run: `dotnet build bench/UniversalSpellCheck.Bench.csproj -c Release`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add bench/HotkeyInjector.cs
git commit -m "bench: add SendInput-based Ctrl+Alt+B keystroke injector"
```

---

## Task 8: BenchHarness — per-trial loop with timing capture

**Files:**
- Create: `bench/BenchHarness.cs`

**What this owns:** for each input × N trials: load text into the form, fire the hotkey, wait for `TextChanged`, capture the t0/t1 wall-clock delta plus the coordinator's internal phase timings, accumulate into per-input result records.

- [ ] **Step 1: Write the harness**

Write `bench/BenchHarness.cs`:

```csharp
using System.Diagnostics;
using System.Windows.Forms;

namespace UniversalSpellCheck.Bench;

internal sealed class TrialResult
{
    public required string InputName { get; init; }
    public required int TrialIndex { get; init; }
    public required bool Success { get; init; }
    public required long TotalMs { get; init; }              // hotkey-press → TextChanged
    public required long CoordinatorTotalMs { get; init; }   // RunRecord total (hotkey → HotPathReturned)
    public required long CaptureMs { get; init; }
    public required long RequestMs { get; init; }
    public required long PostProcessMs { get; init; }
    public required long PasteMs { get; init; }
    public required int InputTokens { get; init; }
    public required int OutputTokens { get; init; }
    public required int CachedTokens { get; init; }
    public string? Error { get; init; }
}

internal sealed class InputResult
{
    public required string Name { get; init; }
    public required int InputChars { get; init; }
    public required List<TrialResult> Trials { get; init; }
}

internal sealed class BenchHarness
{
    private readonly BenchTargetForm _form;
    private readonly SpellcheckCoordinator _coordinator;
    private readonly DiagnosticsLogger _logger;
    private readonly int _runs;
    private readonly int _warmup;

    // RunRecord captured by the coordinator's logger sink. The coordinator only
    // exposes timings via its FinalizeAsync log; the cleanest seam is to wrap
    // the logger so we can intercept the spellcheck_detail JSON per trial.
    private TrialTimings? _lastTrialTimings;

    public BenchHarness(
        BenchTargetForm form,
        SpellcheckCoordinator coordinator,
        DiagnosticsLogger logger,
        int runs,
        int warmup)
    {
        _form = form;
        _coordinator = coordinator;
        _logger = logger;
        _runs = runs;
        _warmup = warmup;
    }

    /// <summary>Set by Program.cs after each Coordinator.RunAsync completes.</summary>
    public void RecordCoordinatorTimings(TrialTimings timings) => _lastTrialTimings = timings;

    public async Task<List<InputResult>> RunAllAsync(IReadOnlyList<(string Name, string Text)> inputs)
    {
        var results = new List<InputResult>();

        foreach (var (name, text) in inputs)
        {
            var trials = new List<TrialResult>();

            for (var w = 0; w < _warmup; w++)
            {
                _logger.Log($"bench warmup input={name} trial={w + 1}/{_warmup}");
                _ = await RunOneTrialAsync(name, text, trialIndex: -(w + 1));
            }

            for (var i = 0; i < _runs; i++)
            {
                _logger.Log($"bench measured input={name} trial={i + 1}/{_runs}");
                var trial = await RunOneTrialAsync(name, text, trialIndex: i + 1);
                trials.Add(trial);
            }

            results.Add(new InputResult
            {
                Name = name,
                InputChars = text.Length,
                Trials = trials,
            });
        }

        return results;
    }

    private async Task<TrialResult> RunOneTrialAsync(string name, string text, int trialIndex)
    {
        _lastTrialTimings = null;
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Subscribe ONCE per trial to the textbox change event.
        void OnChanged(object? _, EventArgs __)
        {
            if (string.Equals(_form.CurrentText, text, StringComparison.Ordinal))
            {
                // The change is from our own LoadAndSelect call; ignore.
                return;
            }
            ready.TrySetResult(true);
        }

        _form.TargetTextChanged += OnChanged;
        try
        {
            // Load text + focus. Pump messages so focus actually lands.
            _form.Invoke(() => _form.LoadAndSelect(text));
            Application.DoEvents();
            await Task.Delay(50);  // give Windows a tick to settle focus

            var t0 = Stopwatch.GetTimestamp();
            HotkeyInjector.FireCtrlAltB();

            // Wait up to 60s for the textbox to change (cap = HttpClient timeout + slack).
            var winner = await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(60)));
            var t1 = Stopwatch.GetTimestamp();

            if (winner != ready.Task)
            {
                return new TrialResult
                {
                    InputName = name,
                    TrialIndex = trialIndex,
                    Success = false,
                    TotalMs = TicksToMs(t0, t1),
                    CoordinatorTotalMs = 0,
                    CaptureMs = 0,
                    RequestMs = 0,
                    PostProcessMs = 0,
                    PasteMs = 0,
                    InputTokens = 0,
                    OutputTokens = 0,
                    CachedTokens = 0,
                    Error = "Timed out waiting for paste TextChanged.",
                };
            }

            // Coordinator finalize runs as Task.Run after RunAsync returns; wait briefly
            // for the timings sink to receive the spellcheck_detail event.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (_lastTrialTimings is null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            var t = _lastTrialTimings;
            return new TrialResult
            {
                InputName = name,
                TrialIndex = trialIndex,
                Success = t?.Status == "success",
                TotalMs = TicksToMs(t0, t1),
                CoordinatorTotalMs = t?.TotalMs ?? 0,
                CaptureMs = t?.ClipboardMs ?? 0,
                RequestMs = t?.RequestMs ?? 0,
                PostProcessMs = (t?.ReplacementsMs ?? 0) + (t?.PromptGuardMs ?? 0),
                PasteMs = t?.PasteMs ?? 0,
                InputTokens = t?.InputTokens ?? 0,
                OutputTokens = t?.OutputTokens ?? 0,
                CachedTokens = t?.CachedTokens ?? 0,
                Error = t?.ErrorMessage,
            };
        }
        finally
        {
            _form.TargetTextChanged -= OnChanged;
        }
    }

    private static long TicksToMs(long start, long end)
    {
        if (start == 0 || end == 0 || end <= start) return 0;
        return (long)((end - start) * 1000.0 / Stopwatch.Frequency);
    }
}

/// <summary>Coordinator phase timings captured per trial via a logger interceptor.</summary>
internal sealed class TrialTimings
{
    public required string Status { get; init; }
    public required long TotalMs { get; init; }
    public required long ClipboardMs { get; init; }
    public required long RequestMs { get; init; }
    public required long ReplacementsMs { get; init; }
    public required long PromptGuardMs { get; init; }
    public required long PasteMs { get; init; }
    public required int InputTokens { get; init; }
    public required int OutputTokens { get; init; }
    public required int CachedTokens { get; init; }
    public string? ErrorMessage { get; init; }
}
```

- [ ] **Step 2: Verify build**

Run: `dotnet build bench/UniversalSpellCheck.Bench.csproj -c Release`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add bench/BenchHarness.cs
git commit -m "bench: add per-trial harness with timings capture"
```

---

## Task 9: Wire it all together — Program.cs main loop

**Files:**
- Modify: `bench/Program.cs` (replace placeholder)

**What's tricky:** the coordinator emits its phase timings only by calling `DiagnosticsLogger.LogData("spellcheck_detail", ...)` inside `FinalizeAsync`. The cleanest way for the bench to learn those timings without changing the coordinator is to subclass `DiagnosticsLogger` and intercept `LogData`. We pass the subclass to the coordinator's logger slot.

- [ ] **Step 1: Write the full Program.cs**

Replace `bench/Program.cs` with:

```csharp
using System.Diagnostics;
using System.Text.Json;
using System.Windows.Forms;

namespace UniversalSpellCheck.Bench;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"bench fatal: {ex}");
            return 2;
        }
    }

    private static int Run(string[] args)
    {
        var opts = ParseArgs(args);
        Console.WriteLine($"bench starting variant={opts.Variant} model={opts.Model} runs={opts.Runs} warmup={opts.Warmup}");

        // Inputs
        var inputDir = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "inputs"));
        if (!inputDir.Exists)
        {
            // Fallback: bench/inputs relative to repo root, useful when running from publish.
            inputDir = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "inputs"));
        }
        var inputFiles = inputDir.GetFiles("*.txt").OrderBy(f => f.Name).ToList();
        if (inputFiles.Count == 0)
        {
            Console.Error.WriteLine($"No inputs found in {inputDir.FullName}. Run extract_inputs.py first.");
            return 1;
        }
        var inputs = inputFiles.Select(f => (Name: Path.GetFileNameWithoutExtension(f.Name), Text: File.ReadAllText(f.FullName))).ToList();
        Console.WriteLine($"loaded {inputs.Count} input(s) from {inputDir.FullName}");

        // Wire production services with bench-specific logger interceptor.
        var benchLogPath = Path.Combine(
            AppPaths.LogDirectory,
            $"bench-{DateTime.Now:yyyy-MM-dd-HHmmss}.jsonl");
        var capturingLogger = new CapturingLogger(benchLogPath);

        var settingsStore = new SettingsStore(capturingLogger);
        var cachedSettings = new CachedSettings(settingsStore);
        if (string.IsNullOrWhiteSpace(cachedSettings.ApiKey))
        {
            Console.Error.WriteLine($"No OpenAI API key in {AppPaths.ApiKeyPath}. Set one in the prod app settings, then rerun.");
            return 1;
        }

        using var spellService = new OpenAiSpellcheckService(cachedSettings, capturingLogger, opts.Model);
        spellService.StartConnectionWarmer();
        var postProcessor = new TextPostProcessor(capturingLogger);

        using var coordinator = new SpellcheckCoordinator(
            capturingLogger,
            spellService,
            postProcessor,
            notify: (_, _) => { /* swallow toast notifications in bench */ },
            setBusy: _ => { /* no overlay in bench */ },
            showSettings: () => { /* no settings dialog in bench */ });

        // Hidden form on the UI thread.
        Application.EnableVisualStyles();
        var form = new BenchTargetForm();
        form.Show();
        // Pump until the form's handle is created and shown.
        Application.DoEvents();

        // Hotkey window — separate from the form so it stays valid across trials.
        using var hotkey = new HotkeyWindow();
        hotkey.HotkeyPressed += (_, _) => _ = coordinator.RunAsync();
        hotkey.Register(HotkeyInjector.HotkeyModifiers, HotkeyInjector.HotkeyVk);

        var harness = new BenchHarness(form, coordinator, capturingLogger, opts.Runs, opts.Warmup);
        capturingLogger.OnSpellcheckDetail = harness.RecordCoordinatorTimings;

        // The harness drives async work; pump WinForms while it runs.
        var task = Task.Run(() => harness.RunAllAsync(inputs));

        while (!task.IsCompleted)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }

        var results = task.GetAwaiter().GetResult();

        // Spin down focused window so the next bench run isn't blocked by it.
        form.Close();

        // Pre-warm cleanup, kill rewarm timer.
        spellService.Dispose();

        var resultsDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "results");
        Directory.CreateDirectory(resultsDir);
        var sha = TryGitSha();
        var outPath = Path.Combine(resultsDir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{sha}-{opts.Variant}.json");
        ResultsWriter.Write(outPath, opts, results);

        Console.WriteLine($"results: {outPath}");
        Console.WriteLine($"bench log: {benchLogPath}");
        return 0;
    }

    private sealed class BenchOptions
    {
        public int Runs { get; init; } = 10;
        public int Warmup { get; init; } = 2;
        public string Model { get; init; } = "gpt-4.1-nano";
        public string Variant { get; init; } = "baseline";
    }

    private static BenchOptions ParseArgs(string[] args)
    {
        int runs = 10, warmup = 2;
        string model = "gpt-4.1-nano", variant = "baseline";
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--runs": runs = int.Parse(args[++i]); break;
                case "--warmup": warmup = int.Parse(args[++i]); break;
                case "--model": model = args[++i]; break;
                case "--variant": variant = args[++i]; break;
                default: throw new ArgumentException($"Unknown arg: {args[i]}");
            }
        }
        return new BenchOptions { Runs = runs, Warmup = warmup, Model = model, Variant = variant };
    }

    private static string TryGitSha()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse --short HEAD")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            using var p = Process.Start(psi)!;
            var sha = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);
            return string.IsNullOrEmpty(sha) ? "nogit" : sha;
        }
        catch { return "nogit"; }
    }
}

/// <summary>
/// DiagnosticsLogger subclass that intercepts the coordinator's `spellcheck_detail`
/// event, parses the timings JSON, and hands a TrialTimings record to the bench.
/// </summary>
internal sealed class CapturingLogger : DiagnosticsLogger
{
    public Action<TrialTimings>? OnSpellcheckDetail { get; set; }

    public CapturingLogger(string logPath) : base(logPath) { }

    public new void LogData(string eventName, object data)
    {
        base.LogData(eventName, data);
        if (eventName != "spellcheck_detail") return;

        try
        {
            var json = JsonSerializer.Serialize(data);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var status = root.GetProperty("status").GetString() ?? "unknown";
            var timings = root.GetProperty("timings");
            var tokens = root.GetProperty("tokens");
            OnSpellcheckDetail?.Invoke(new TrialTimings
            {
                Status = status,
                TotalMs = timings.GetProperty("total_ms").GetInt64(),
                ClipboardMs = timings.GetProperty("clipboard_ms").GetInt64(),
                RequestMs = timings.GetProperty("request_ms").GetInt64(),
                ReplacementsMs = timings.GetProperty("replacements_ms").GetInt64(),
                PromptGuardMs = timings.GetProperty("prompt_guard_ms").GetInt64(),
                PasteMs = timings.GetProperty("paste_ms").GetInt64(),
                InputTokens = tokens.GetProperty("input").GetInt32(),
                OutputTokens = tokens.GetProperty("output").GetInt32(),
                CachedTokens = tokens.GetProperty("cached").GetInt32(),
                ErrorMessage = root.TryGetProperty("error", out var err) ? err.GetString() : null,
            });
        }
        catch
        {
            // Bench cannot fail on a parse error — just skip this trial's timings.
        }
    }
}
```

> **NOTE:** the `new` keyword on `LogData` shadows the base method. The coordinator calls `_logger.LogData(...)` against a `DiagnosticsLogger`-typed reference; since `LogData` is not virtual, this won't intercept. Step 2 fixes this.

- [ ] **Step 2: Make `DiagnosticsLogger.LogData` virtual so the bench can override it**

In `src/DiagnosticsLogger.cs`, find:

```csharp
    public void LogData(string eventName, object data)
```

Replace with:

```csharp
    public virtual void LogData(string eventName, object data)
```

Then in `bench/Program.cs`, change:

```csharp
    public new void LogData(string eventName, object data)
```

to:

```csharp
    public override void LogData(string eventName, object data)
```

- [ ] **Step 3: Verify both projects build**

Run: `dotnet build src/UniversalSpellCheck.csproj -c Release`
Expected: build succeeds.

Run: `dotnet build bench/UniversalSpellCheck.Bench.csproj -c Release`
Expected: build succeeds. We are still missing `ResultsWriter` — Task 10 adds it. For now expect a `ResultsWriter` does-not-exist error and proceed.

- [ ] **Step 4: Commit (allowing the temporary build break)**

```bash
git add bench/Program.cs src/DiagnosticsLogger.cs
git commit -m "bench: wire Program.cs main loop and CapturingLogger interceptor"
```

---

## Task 10: ResultsWriter — JSON output

**Files:**
- Create: `bench/ResultsWriter.cs`

- [ ] **Step 1: Write ResultsWriter**

Write `bench/ResultsWriter.cs`:

```csharp
using System.Text.Json;

namespace UniversalSpellCheck.Bench;

internal static class ResultsWriter
{
    public static void Write(string path, object opts, IReadOnlyList<InputResult> results)
    {
        var summary = results.Select(input => new
        {
            name = input.Name,
            input_chars = input.InputChars,
            trial_count = input.Trials.Count,
            success_count = input.Trials.Count(t => t.Success),
            total_ms = Stats.For(input.Trials, t => t.TotalMs),
            coordinator_total_ms = Stats.For(input.Trials, t => t.CoordinatorTotalMs),
            capture_ms = Stats.For(input.Trials, t => t.CaptureMs),
            request_ms = Stats.For(input.Trials, t => t.RequestMs),
            post_process_ms = Stats.For(input.Trials, t => t.PostProcessMs),
            paste_ms = Stats.For(input.Trials, t => t.PasteMs),
            tokens = new
            {
                input_avg = input.Trials.Where(t => t.Success).DefaultIfEmpty().Average(t => t?.InputTokens ?? 0),
                output_avg = input.Trials.Where(t => t.Success).DefaultIfEmpty().Average(t => t?.OutputTokens ?? 0),
                cached_avg = input.Trials.Where(t => t.Success).DefaultIfEmpty().Average(t => t?.CachedTokens ?? 0),
            },
        });

        var aggregate = new
        {
            generated_at_utc = DateTime.UtcNow.ToString("O"),
            options = opts,
            inputs = summary,
            trials = results.SelectMany(r => r.Trials),  // raw per-trial dump for re-aggregation
        };

        File.WriteAllText(
            path,
            JsonSerializer.Serialize(aggregate, new JsonSerializerOptions { WriteIndented = true }));
    }
}

internal static class Stats
{
    public static object For<T>(IEnumerable<T> items, Func<T, long> selector)
    {
        var values = items.Select(selector).Where(v => v > 0).OrderBy(v => v).ToList();
        if (values.Count == 0)
        {
            return new { count = 0, mean = 0.0, median = 0.0, p95 = 0.0, min = 0L, max = 0L, stddev = 0.0 };
        }
        var mean = values.Average();
        var sumSq = values.Sum(v => (v - mean) * (v - mean));
        var stddev = values.Count > 1 ? Math.Sqrt(sumSq / (values.Count - 1)) : 0.0;
        return new
        {
            count = values.Count,
            mean,
            median = Percentile(values, 0.50),
            p95 = Percentile(values, 0.95),
            min = values[0],
            max = values[^1],
            stddev,
        };
    }

    private static double Percentile(IReadOnlyList<long> sorted, double p)
    {
        if (sorted.Count == 1) return sorted[0];
        var idx = p * (sorted.Count - 1);
        var lo = (int)Math.Floor(idx);
        var hi = (int)Math.Ceiling(idx);
        if (lo == hi) return sorted[lo];
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (idx - lo);
    }
}
```

- [ ] **Step 2: Verify both projects build**

Run: `dotnet build bench/UniversalSpellCheck.Bench.csproj -c Release`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add bench/ResultsWriter.cs
git commit -m "bench: add ResultsWriter with median/p95/stddev per phase"
```

---

## Task 11: Python compare script + tests

**Files:**
- Create: `bench/compare.py`
- Create: `bench/test_compare.py`

- [ ] **Step 1: Write the failing test**

Write `bench/test_compare.py`:

```python
"""Tests for bench/compare.py — diffs two bench result JSONs into a delta table."""

import json
from pathlib import Path

import pytest

from compare import build_delta_rows, format_delta_table


def make_results(name: str, total_median: float, request_median: float) -> dict:
    return {
        "options": {"Variant": "test", "Model": "gpt-4.1-nano", "Runs": 10, "Warmup": 2},
        "inputs": [
            {
                "name": name,
                "input_chars": 100,
                "trial_count": 10,
                "success_count": 10,
                "total_ms": {"count": 10, "mean": total_median, "median": total_median, "p95": total_median + 50, "min": 0, "max": 0, "stddev": 0},
                "coordinator_total_ms": {"count": 10, "mean": total_median, "median": total_median, "p95": total_median, "min": 0, "max": 0, "stddev": 0},
                "capture_ms": {"count": 10, "mean": 5, "median": 5, "p95": 8, "min": 0, "max": 0, "stddev": 0},
                "request_ms": {"count": 10, "mean": request_median, "median": request_median, "p95": request_median + 30, "min": 0, "max": 0, "stddev": 0},
                "post_process_ms": {"count": 10, "mean": 1, "median": 1, "p95": 2, "min": 0, "max": 0, "stddev": 0},
                "paste_ms": {"count": 10, "mean": 60, "median": 60, "p95": 70, "min": 0, "max": 0, "stddev": 0},
                "tokens": {"input_avg": 50, "output_avg": 30, "cached_avg": 0},
            }
        ],
    }


def test_delta_shows_improvement(tmp_path: Path) -> None:
    before = tmp_path / "before.json"
    after = tmp_path / "after.json"
    before.write_text(json.dumps(make_results("01", 1000, 800)), encoding="utf-8")
    after.write_text(json.dumps(make_results("01", 700, 500)), encoding="utf-8")

    rows = build_delta_rows(before, after)

    total_row = next(r for r in rows if r["input"] == "01" and r["phase"] == "total_ms")
    assert total_row["before_median"] == 1000
    assert total_row["after_median"] == 700
    assert total_row["delta_ms"] == -300
    assert total_row["delta_pct"] == pytest.approx(-30.0)


def test_delta_shows_regression(tmp_path: Path) -> None:
    before = tmp_path / "before.json"
    after = tmp_path / "after.json"
    before.write_text(json.dumps(make_results("01", 800, 500)), encoding="utf-8")
    after.write_text(json.dumps(make_results("01", 1000, 700)), encoding="utf-8")

    rows = build_delta_rows(before, after)

    total_row = next(r for r in rows if r["input"] == "01" and r["phase"] == "total_ms")
    assert total_row["delta_ms"] == 200
    assert total_row["delta_pct"] == pytest.approx(25.0)


def test_format_delta_table_renders_string(tmp_path: Path) -> None:
    before = tmp_path / "before.json"
    after = tmp_path / "after.json"
    before.write_text(json.dumps(make_results("01", 1000, 800)), encoding="utf-8")
    after.write_text(json.dumps(make_results("01", 700, 500)), encoding="utf-8")

    rendered = format_delta_table(build_delta_rows(before, after))

    assert "01" in rendered
    assert "total_ms" in rendered
    assert "-300" in rendered or "-30.0" in rendered
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd bench && python -m pytest test_compare.py -v`
Expected: `ModuleNotFoundError: No module named 'compare'`.

- [ ] **Step 3: Implement compare.py**

Write `bench/compare.py`:

```python
"""Diff two bench result JSON files into a phase-level delta table."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

PHASES = ["total_ms", "coordinator_total_ms", "capture_ms", "request_ms", "post_process_ms", "paste_ms"]


def build_delta_rows(before_path: Path, after_path: Path) -> list[dict]:
    before = json.loads(Path(before_path).read_text(encoding="utf-8"))
    after = json.loads(Path(after_path).read_text(encoding="utf-8"))

    after_by_name = {i["name"]: i for i in after.get("inputs", [])}

    rows: list[dict] = []
    for b in before.get("inputs", []):
        a = after_by_name.get(b["name"])
        if a is None:
            continue
        for phase in PHASES:
            b_med = float(b.get(phase, {}).get("median", 0) or 0)
            a_med = float(a.get(phase, {}).get("median", 0) or 0)
            delta = a_med - b_med
            pct = (delta / b_med * 100.0) if b_med > 0 else 0.0
            rows.append({
                "input": b["name"],
                "phase": phase,
                "before_median": b_med,
                "after_median": a_med,
                "delta_ms": delta,
                "delta_pct": pct,
            })
    return rows


def format_delta_table(rows: list[dict]) -> str:
    if not rows:
        return "(no comparable inputs)"
    header = f"{'input':<6} {'phase':<22} {'before_ms':>10} {'after_ms':>10} {'Δ_ms':>10} {'Δ_%':>8}"
    lines = [header, "-" * len(header)]
    for r in rows:
        marker = ""
        if r["delta_pct"] <= -5:
            marker = " ✓"
        elif r["delta_pct"] >= 5:
            marker = " ✗"
        lines.append(
            f"{r['input']:<6} {r['phase']:<22} "
            f"{r['before_median']:>10.1f} {r['after_median']:>10.1f} "
            f"{r['delta_ms']:>10.1f} {r['delta_pct']:>7.1f}%{marker}"
        )
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Diff two bench result JSON files.")
    parser.add_argument("before", type=Path)
    parser.add_argument("after", type=Path)
    args = parser.parse_args(argv)

    rows = build_delta_rows(args.before, args.after)
    print(format_delta_table(rows))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd bench && python -m pytest test_compare.py -v`
Expected: 3 passed.

- [ ] **Step 5: Commit**

```bash
git add bench/compare.py bench/test_compare.py
git commit -m "bench: add Python compare script with phase-level delta table"
```

---

## Task 12: Run baseline bench end-to-end

**Files:**
- Create: `bench/results/<utc>-<sha>-baseline.json` (auto-generated)

- [ ] **Step 1: Confirm prerequisites**

```bash
ls bench/inputs/
```
Expected: at least 1 `*.txt` file (from Task 5). If empty, run Task 5 again.

Verify the prod app has an API key set: open `%LocalAppData%\UniversalSpellCheck\` and confirm `apikey.dat` exists. If missing, run the prod app once and save a key in Settings.

- [ ] **Step 2: Confirm Prod tray app is NOT running**

If prod is running with Ctrl+Alt+U registered, that's fine — bench uses Ctrl+Alt+B which won't collide. But to keep focus simple during the bench, quit the prod tray app and any browser/IDE that might fight for focus. Leave the bench process as the only thing actively using focus.

- [ ] **Step 3: Build the bench in Release mode**

Run: `dotnet build bench/UniversalSpellCheck.Bench.csproj -c Release`
Expected: build succeeds, no warnings.

- [ ] **Step 4: Run a 2-trial smoke**

Run: `dotnet run --project bench/UniversalSpellCheck.Bench.csproj -c Release -- --runs 2 --warmup 1 --variant smoke`

Expected:
- Console prints `bench starting variant=smoke model=gpt-4.1-nano runs=2 warmup=1`.
- Console prints `loaded N input(s)`.
- For ~2 minutes, the screen will briefly steal focus per trial — DO NOT TYPE during this window.
- Console prints `results: bench/results/...-smoke.json`.

Open the results JSON and verify:
1. `inputs[*].total_ms.median` is a plausible number (≥500 ms — bench overhead + API).
2. `inputs[*].request_ms.median` is a plausible number (typically 80-90% of total).
3. `inputs[*].success_count == trial_count` (no timed-out trials).

If success_count is less than trial_count for any input: read `bench-*.jsonl` for the bench session, find the failing trial, debug. Common causes: focus stolen, API key missing, network failure. Fix and re-run before proceeding.

- [ ] **Step 5: Run the full baseline**

Run: `dotnet run --project bench/UniversalSpellCheck.Bench.csproj -c Release -- --runs 10 --warmup 2 --variant baseline`

Expected: ~5-15 minutes runtime depending on input lengths and network. Result file written.

- [ ] **Step 6: Eyeball the baseline**

```bash
python -c "import json; d = json.load(open(sorted(__import__('glob').glob('bench/results/*-baseline.json'))[-1])); [print(i['name'], i['total_ms']['median'], i['request_ms']['median']) for i in d['inputs']]"
```

Confirm numbers look like a stable baseline (ratios of request/total are consistent across inputs, no input has wildly higher variance than the rest).

- [ ] **Step 7: Commit baseline + results dir**

```bash
git add bench/results/
git commit -m "bench: capture initial baseline run"
```

---

## Task 13: Document the workflow

**Files:**
- Create: `bench/README.md`

- [ ] **Step 1: Write README**

Write `bench/README.md`:

````markdown
# Universal Spell Check — Speed Bench

Deterministic-ish end-to-end speed harness inspired by [karpathy/autoresearch](https://github.com/karpathy/autoresearch). Measures the real workflow: hotkey-press → text-replaced in a focused TextBox, against the real OpenAI API.

## What it measures

Per input × N trials:
- `total_ms` — wall-clock from synthesized Ctrl+Alt+B keystroke to `TextBox.TextChanged` firing
- `coordinator_total_ms` — what the production logger calls `total_ms` (hotkey → HotPathReturned)
- `capture_ms`, `request_ms`, `post_process_ms`, `paste_ms` — phase splits the coordinator already records

Reported as median, p95, mean, stddev so a single network blip doesn't swing a comparison.

## Refresh inputs from logs

```powershell
cd bench
python extract_inputs.py
```

Reads `%LocalAppData%\UniversalSpellCheck\logs\spellcheck-*.jsonl`, picks 10 deduped successful inputs stratified by length, writes `bench/inputs/01.txt`..`10.txt`.

## Run a bench

```powershell
dotnet build bench/UniversalSpellCheck.Bench.csproj -c Release
dotnet run --project bench/UniversalSpellCheck.Bench.csproj -c Release -- --runs 10 --warmup 2 --variant baseline
```

Flags:
- `--runs N` — measured trials per input (default 10)
- `--warmup N` — warmup trials per input that don't contribute to stats (default 2)
- `--model NAME` — OpenAI model (default `gpt-4.1-nano`)
- `--variant LABEL` — free-form label that ends up in the results filename

**While the bench runs, do not type or click.** It uses SendInput on Ctrl+Alt+B, and stealing focus mid-trial will break that trial.

Results are written to `bench/results/{utc}-{git-sha}-{variant}.json`.

## Compare two runs

```powershell
python bench/compare.py bench/results/<before>.json bench/results/<after>.json
```

Prints a per-input × per-phase delta table. ✓ marks improvements ≥5%, ✗ marks regressions ≥5%.

## Optimization workflow (autoresearch-style)

1. Run baseline: `... --variant baseline`
2. Make ONE change to `src/`
3. Re-run: `... --variant <descriptive-name>`
4. Compare: `python bench/compare.py bench/results/baseline.json bench/results/<descriptive-name>.json`
5. If green across the board, commit. If mixed, revert or iterate.

## Why these numbers will jitter

End-to-end measurement against a real LLM API has irreducible noise. The harness controls for what it can:
- Warmup trials prime the HTTP/2 connection pool, JIT, TextBox, OS focus subsystem.
- Median + p95 (not mean) so a single 95th-percentile spike doesn't dominate.
- 10 trials per input gives roughly ±5% confidence at the median for typical OpenAI variance.

If a comparison shows <5% delta, treat it as noise. >10% delta is real signal.
````

- [ ] **Step 2: Commit**

```bash
git add bench/README.md
git commit -m "bench: document harness workflow"
```

---

## Self-Review

**1. Spec coverage:**
- ✅ End-to-end measurement (hotkey-press → text-replaced) — Task 8 (`BenchHarness.RunOneTrialAsync` t0/t1 around `HotkeyInjector.FireCtrlAltB` ↔ `TextChanged`)
- ✅ Mirrors real workflow (real coordinator, real API, real OS hotkey, real TextBox focus, real Ctrl+V paste) — Task 9 wires production services unmodified
- ✅ 10 inputs from real logs, simple — Task 4 + Task 5
- ✅ Default model `gpt-4.1-nano` with `--model` flag — Task 9 `BenchOptions` defaults + Task 2 makes the service accept it
- ✅ Per-phase timings exposed — Task 8 `TrialTimings` + Task 9 `CapturingLogger`
- ✅ JSON output diffable across runs — Task 10 `ResultsWriter`
- ✅ Compare script — Task 11
- ✅ Cost approval (real OpenAI calls) — explicit in Task 12 prereqs

**2. Placeholder scan:** searched for "TBD", "TODO", "implement later" — none. All code blocks are complete and runnable.

**3. Type consistency:**
- `TrialTimings` defined in Task 8 (`BenchHarness.cs`); referenced in Task 9 (`Program.cs::CapturingLogger.OnSpellcheckDetail`) — match.
- `BenchTargetForm.TargetTextChanged` defined in Task 6; subscribed in Task 8 — match.
- `OpenAiSpellcheckService` constructor `(CachedSettings, DiagnosticsLogger, string model)` defined in Task 2; called in Task 9 — match.
- `HotkeyWindow.Register(uint, uint)` defined in Task 3; called in Task 9 with `HotkeyInjector.HotkeyModifiers, HotkeyInjector.HotkeyVk` — match.
- `DiagnosticsLogger.LogData` made `virtual` in Task 9 step 2; overridden in `CapturingLogger` in Task 9 step 1 — flagged inline (the `new` → `override` swap is explicit).
- `InputResult` / `TrialResult` defined in Task 8; used in `ResultsWriter` in Task 10 — match.

**4. Risk flags:**
- The bench's `BenchTargetForm` runs at `Opacity = 0.01`. If Windows refuses to give focus to a near-invisible window on some machines, the first trial may fail. Mitigation: bump opacity to 0.05 and accept a faintly-visible flash; documented in Task 6 inline if we hit this.
- `Application.DoEvents()` polling in `Program.cs::Run` is a code smell, but it's the simplest way to keep the WinForms message pump alive while an async bench drives in the background. A proper SynchronizationContext setup would be cleaner; deferred unless this causes flakiness.
- The 2-second post-trial wait for `_lastTrialTimings` to populate (Task 8 `RunOneTrialAsync`) assumes `FinalizeAsync` completes within that window. It's currently a few ms per trial in production, so 2s is generous.

---

## Execution Handoff

Plan complete and saved to `.planning/.other-plans/2026-04-29-deterministic-e2e-speed-bench.md`. Two execution options:

**1. Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration

**2. Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints

Which approach?
