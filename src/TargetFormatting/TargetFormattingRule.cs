namespace UniversalSpellCheck;

internal enum TargetFormattingMatchType
{
    App,
    Site
}

internal interface ITargetFormattingRule
{
    string Id { get; }
    TargetFormattingMatchType MatchType { get; }
    bool HasBeforePasteTransform { get; }
    bool Matches(TargetContext context);
    FormattingResult AfterCopy(string text, TargetContext context);
    FormattingResult BeforePaste(string text, TargetContext context);
}

internal sealed record FormattingResult(
    string Text,
    bool Applied,
    int CharsAdded,
    int CharsRemoved,
    IReadOnlyList<string> Operations,
    IReadOnlyDictionary<string, int>? Counters = null,
    string? FailureCode = null,
    string? FailureType = null,
    bool AbortPaste = false)
{
    public static FormattingResult NotApplied(string text) => new(
        text,
        Applied: false,
        CharsAdded: 0,
        CharsRemoved: 0,
        Operations: Array.Empty<string>());

    public static FormattingResult HookFailed(string text, Exception exception) => new(
        text,
        Applied: false,
        CharsAdded: 0,
        CharsRemoved: 0,
        Operations: Array.Empty<string>(),
        FailureCode: "hook_threw",
        FailureType: exception.GetType().Name);

    public static FormattingResult Unsafe(string text, string failureCode) => new(
        text,
        Applied: false,
        CharsAdded: 0,
        CharsRemoved: 0,
        Operations: Array.Empty<string>(),
        FailureCode: failureCode,
        AbortPaste: true);
}

internal sealed record FormattingMatch(
    ITargetFormattingRule Rule,
    TargetContext StartingContext);
