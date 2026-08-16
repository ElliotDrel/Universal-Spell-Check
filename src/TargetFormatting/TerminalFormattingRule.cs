using System.Text.RegularExpressions;

namespace UniversalSpellCheck;

internal sealed class TerminalFormattingRule : ITargetFormattingRule
{
    internal const string RuleId = "terminal";
    internal const string DoubleBreakCounter = "double_break_count";
    internal const string ListItemCounter = "list_item_count";
    internal const string SoftWrapCounter = "soft_wrap_count";
    internal const string TerminalMarkerCounter = "terminal_marker_count";

    private static readonly HashSet<string> TerminalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal", "Code", "powershell", "pwsh", "cmd", "bash"
    };

    private static readonly Regex DoubleBreakRegex = new(@"\r\n\r\n[ \t]*", RegexOptions.Compiled);
    private static readonly Regex ListItemRegex = new(
        @"\r\n[ \t]+(?=[-*•][ \t]|\d+\.[ \t])",
        RegexOptions.Compiled);
    private static readonly Regex SoftWrapRegex = new(@" *\r\n[ \t]+", RegexOptions.Compiled);
    private static readonly Regex WrappedFilePathRegex = new(
        """
        (?:
            [A-Za-z]:\\
            |
            \\\\[^\\\s]+\\
        )
        (?:[^\r\n\"'<>|:*?]|\r\n[ \t])+
        """,
        RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);
    private static readonly Regex StandaloneTerminalMarkerLineRegex = new(
        @"\r\n[ \t]*\u258E[ \t]*\r\n",
        RegexOptions.Compiled);
    private static readonly Regex TerminalMarkerRegex = new("\u258E", RegexOptions.Compiled);

    public string Id => RuleId;
    public TargetFormattingMatchType MatchType => TargetFormattingMatchType.App;
    public bool HasBeforePasteTransform => false;

    public bool Matches(TargetContext context) => TerminalProcesses.Contains(context.ProcessName);

    public FormattingResult AfterCopy(string text, TargetContext context)
    {
        var doubleBreakCount = 0;
        var listItemCount = 0;
        var softWrapCount = 0;
        var terminalMarkerCount = 0;
        var literalSoftWrapCount = 0;

        var normalized = StandaloneTerminalMarkerLineRegex.Replace(text, _ =>
        {
            terminalMarkerCount++;
            return "\r\n\r\n";
        });
        normalized = TerminalMarkerRegex.Replace(normalized, _ =>
        {
            terminalMarkerCount++;
            return string.Empty;
        });
        normalized = RepairWrappedLiterals(normalized, out literalSoftWrapCount);
        normalized = DoubleBreakRegex.Replace(normalized, _ =>
        {
            doubleBreakCount++;
            return "\n\n";
        });
        normalized = ListItemRegex.Replace(normalized, _ =>
        {
            listItemCount++;
            return "\n";
        });
        normalized = SoftWrapRegex.Replace(normalized, _ =>
        {
            softWrapCount++;
            return " ";
        });

        var charsRemoved = text.Length - normalized.Length;
        if (charsRemoved == 0)
        {
            return FormattingResult.NotApplied(text);
        }

        var operations = new List<string>(4);
        if (terminalMarkerCount > 0) operations.Add("remove_terminal_marker");
        if (literalSoftWrapCount > 0) operations.Add("repair_wrapped_literal");
        if (doubleBreakCount > 0) operations.Add("normalize_double_break");
        if (listItemCount > 0) operations.Add("normalize_list_item");
        if (softWrapCount > 0) operations.Add("collapse_soft_wrap");

        return new FormattingResult(
            normalized,
            Applied: true,
            CharsAdded: 0,
            CharsRemoved: charsRemoved,
            Operations: operations,
            Counters: new Dictionary<string, int>
            {
                [DoubleBreakCounter] = doubleBreakCount,
                [ListItemCounter] = listItemCount,
                [SoftWrapCounter] = softWrapCount,
                [TerminalMarkerCounter] = terminalMarkerCount
            });
    }

    public FormattingResult BeforePaste(string text, TargetContext context)
    {
        return FormattingResult.NotApplied(text);
    }

    private static string RepairWrappedLiterals(string text, out int literalSoftWrapCount)
    {
        var count = 0;

        string RemoveSoftWraps(string literal) => SoftWrapRegex.Replace(literal, _ =>
        {
            count++;
            return string.Empty;
        });

        text = WrappedFilePathRegex.Replace(text, match => RemoveSoftWraps(match.Value));
        literalSoftWrapCount = count;
        return text;
    }
}
