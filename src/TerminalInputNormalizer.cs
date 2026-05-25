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
