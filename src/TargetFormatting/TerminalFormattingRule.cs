using System.Text.RegularExpressions;

namespace UniversalSpellCheck;

internal sealed class TerminalFormattingRule : ITargetFormattingRule
{
    internal const string RuleId = "terminal";
    internal const string DoubleBreakCounter = "double_break_count";
    internal const string ListItemCounter = "list_item_count";
    internal const string SoftWrapCounter = "soft_wrap_count";

    private static readonly HashSet<string> TerminalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal", "Code", "powershell", "pwsh", "cmd", "bash"
    };

    private static readonly Regex DoubleBreakRegex = new(@"\r\n\r\n[ \t]*", RegexOptions.Compiled);
    private static readonly Regex ListItemRegex = new(
        @"\r\n[ \t]+(?=[-*•][ \t]|\d+\.[ \t])",
        RegexOptions.Compiled);
    private static readonly Regex SoftWrapRegex = new(@" *\r\n[ \t]+", RegexOptions.Compiled);

    public string Id => RuleId;
    public TargetFormattingMatchType MatchType => TargetFormattingMatchType.App;
    public bool HasBeforePasteTransform => false;

    public bool Matches(TargetContext context) => TerminalProcesses.Contains(context.ProcessName);

    public FormattingResult AfterCopy(string text, TargetContext context)
    {
        var doubleBreakCount = 0;
        var listItemCount = 0;
        var softWrapCount = 0;

        var normalized = DoubleBreakRegex.Replace(text, _ =>
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

        var operations = new List<string>(3);
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
                [SoftWrapCounter] = softWrapCount
            });
    }

    public FormattingResult BeforePaste(string text, TargetContext context)
    {
        return FormattingResult.NotApplied(text);
    }
}
