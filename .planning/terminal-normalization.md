# Plan: Terminal Input Normalization

## Context

When the user copies their typed messages from a Windows terminal (Windows Terminal or VS Code integrated terminal) to spell-check, the terminal inserts `\r\n  ` (CRLF + 2 leading spaces) at every line break — both soft-wrap points (terminal width overflow) and intentional Enter presses. This artifact passes verbatim to the AI and gets echoed back in the output, producing ugly multi-line pasted text with errant indentation. The fix is a pre-processing step that joins these broken lines before the API call.

**Key finding from log data**: Both soft-wrapped lines and user-pressed Enters produce identical `\r\n  ` bytes in terminal-copied text — indistinguishable at the byte level. Joining them all with a single space produces clean prose, which is the right behavior for the user's use case (prose messages to Claude Code).

**Scope**: terminal processes only (`WindowsTerminal`, `Code`, `powershell`, `pwsh`, `cmd`, `bash`). Not applied in headless mode (bench/test paths have no active-window context).

---

## Files

### New: `src/TerminalInputNormalizer.cs`

Stateless normalizer, mirroring the shape of `TextPostProcessor`.

```csharp
using System.Text.RegularExpressions;

namespace UniversalSpellCheck;

internal static class TerminalInputNormalizer
{
    private static readonly HashSet<string> TerminalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal", "Code", "powershell", "pwsh", "cmd", "bash"
    };

    // Trailing spaces + CRLF + leading spaces = terminal soft-wrap artifact.
    private static readonly Regex WrapRegex = new(@" *\r\n +", RegexOptions.Compiled);

    public static TerminalNormResult Normalize(string text, string? processName)
    {
        if (processName is null || !TerminalProcesses.Contains(processName))
            return TerminalNormResult.NotApplied(text);

        var normalized = WrapRegex.Replace(text, " ");
        var charsRemoved = text.Length - normalized.Length;

        if (charsRemoved == 0)
            return TerminalNormResult.NotApplied(text);

        return new TerminalNormResult
        {
            Text = normalized,
            Applied = true,
            CharsRemoved = charsRemoved,
            ProcessName = processName
        };
    }
}

internal sealed class TerminalNormResult
{
    public string Text { get; init; } = "";
    public bool Applied { get; init; }
    public int CharsRemoved { get; init; }
    public string? ProcessName { get; init; }

    public static TerminalNormResult NotApplied(string text) => new() { Text = text };
}
```

**Regex rationale**: ` *\r\n +` requires at least one space AFTER the CRLF. Bare `\r\n` (no trailing spaces) is left untouched to preserve any edge-case intentional breaks. Optional spaces before the CRLF consume terminal trailing whitespace so joins don't produce double-spaces.

---

### Modified: `src/RunRecord.cs`

Add one field at the bottom of the "Post-process" section:

```csharp
// Pre-process
public TerminalNormResult TerminalNorm { get; set; } = TerminalNormResult.NotApplied("");
```

---

### Modified: `src/SpellcheckCoordinator.cs`

**1. Constructor** — add `TerminalInputNormalizer` is static, no injection needed.

**2. `ExecuteHotPathAsync`** — after line 181 (`record.InputText = capture.Text;`), insert:

```csharp
var norm = TerminalInputNormalizer.Normalize(capture.Text!, record.ActiveWindowAtStart.ProcessName);
record.TerminalNorm = norm;
var textToSpellcheck = norm.Applied ? norm.Text : capture.Text!;
```

Change line 185 to:
```csharp
var spell = await _spellcheckService.SpellcheckAsync(textToSpellcheck, record);
```

**3. `FinalizeAsync` — human-readable line** — append after `active_process=...`:
```csharp
(r.TerminalNorm.Applied ? $"terminal_normalized=true terminal_norm_chars_removed={r.TerminalNorm.CharsRemoved} " : "")
```

**4. `FinalizeAsync` — `spellcheck_detail` JSON** — add a new key in the JSON object:
```csharp
terminal_normalization = new
{
    applied = r.TerminalNorm.Applied,
    chars_removed = r.TerminalNorm.CharsRemoved,
    process = r.TerminalNorm.ProcessName ?? ""
},
```

Place it alongside the `replacements` and `prompt_leak` blocks.

`ExecuteHeadlessAsync` — **no change**. Headless has no active-window context.

---

### Modified: `docs/replacements-and-logging.md`

Add a short section describing the new pre-processing step (runs before the API call, terminal-only) so the doc reflects the full pipeline order: normalize → API → post-process.

---

## Verification

1. **Build Dev channel**: `dotnet run --project src/UniversalSpellCheck.csproj -c Dev`
2. In Windows Terminal or VS Code terminal, type a message longer than terminal width (so it soft-wraps) and spell-check it with Ctrl+Alt+D.
3. Confirm the pasted output is clean single-line prose with no `\r\n` artifacts.
4. Open today's log, find the `spellcheck_detail` entry — verify `terminal_normalization.applied = true` and `chars_removed > 0`.
5. Check `run_completed` line — verify `terminal_normalized=true` appears.
6. In a non-terminal app (e.g., Notepad), spell-check text with intentional newlines — verify `terminal_normalization.applied = false` and newlines are preserved.
