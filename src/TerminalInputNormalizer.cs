using System.Text.RegularExpressions;

namespace UniversalSpellCheck;

internal static class TerminalInputNormalizer
{
    private static readonly HashSet<string> TerminalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal", "Code", "powershell", "pwsh", "cmd", "bash"
    };

    // Step 1: Double CRLF + optional whitespace → paragraph break (preserve intentional blank lines).
    private static readonly Regex DoubleBreakRegex = new(@"\r\n\r\n[ \t]*", RegexOptions.Compiled);

    // Step 2: CRLF + whitespace + list marker → newline + marker (preserve bullet/numbered lists).
    // Lookahead keeps the marker itself; only the indent is consumed.
    private static readonly Regex ListItemRegex = new(@"\r\n[ \t]+(?=[-*•][ \t]|\d+\.[ \t])", RegexOptions.Compiled);

    // Step 3: Remaining CRLF + whitespace → single space (collapse soft-wrap continuation).
    // Tabs covered alongside spaces for terminals that indent with tabs.
    private static readonly Regex SoftWrapRegex = new(@" *\r\n[ \t]+", RegexOptions.Compiled);

    public static TerminalNormResult Normalize(string text, string? processName)
    {
        if (processName is null || !TerminalProcesses.Contains(processName))
            return TerminalNormResult.NotApplied(text);

        var normalized = DoubleBreakRegex.Replace(text, "\n\n");
        normalized = ListItemRegex.Replace(normalized, "\n");
        normalized = SoftWrapRegex.Replace(normalized, " ");

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
