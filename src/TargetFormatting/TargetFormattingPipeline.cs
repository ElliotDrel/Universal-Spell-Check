using System.Diagnostics;

namespace UniversalSpellCheck;

internal sealed class TargetFormattingPipeline
{
    private readonly IReadOnlyList<ITargetFormattingRule> _rules;
    private readonly Func<long> _timestampProvider;
    private readonly long _browserFreshnessTicks;

    public TargetFormattingPipeline()
        : this(
            new ITargetFormattingRule[] { new TerminalFormattingRule() },
            Stopwatch.GetTimestamp,
            browserFreshnessTicks: null)
    {
    }

    internal TargetFormattingPipeline(
        IReadOnlyList<ITargetFormattingRule> rules,
        Func<long>? timestampProvider = null,
        long? browserFreshnessTicks = null)
    {
        _rules = rules;
        _timestampProvider = timestampProvider ?? Stopwatch.GetTimestamp;
        if (rules.Any(rule => rule.MatchType == TargetFormattingMatchType.Site)
            && browserFreshnessTicks is null)
        {
            throw new ArgumentException(
                "Site rules require an explicit measured browser freshness limit.",
                nameof(browserFreshnessTicks));
        }
        _browserFreshnessTicks = browserFreshnessTicks ?? 0;
    }

    public FormattingMatch? Resolve(TargetContext context)
    {
        foreach (var rule in _rules)
        {
            if (rule.MatchType == TargetFormattingMatchType.Site && !HasFreshBrowserContext(context.Browser))
            {
                continue;
            }

            if (rule.Matches(context))
            {
                return new FormattingMatch(rule, context);
            }
        }

        return null;
    }

    private bool HasFreshBrowserContext(BrowserTargetContext? browser)
    {
        if (!TargetMatch.IsSupportedWebContext(browser)) return false;

        var age = _timestampProvider() - browser!.ReceivedAtStopwatchTicks;
        return age >= 0 && age <= _browserFreshnessTicks;
    }

    public FormattingResult ApplyAfterCopy(FormattingMatch match, string text)
    {
        try
        {
            return match.Rule.AfterCopy(text, match.StartingContext);
        }
        catch (Exception ex)
        {
            return FormattingResult.HookFailed(text, ex);
        }
    }

    public FormattingResult ApplyBeforePaste(
        FormattingMatch match,
        string text,
        TargetContext liveContext)
    {
        if (!IsSameDestination(match.StartingContext, liveContext, match.Rule))
        {
            return FormattingResult.Unsafe(text, "identity_changed");
        }

        if (!match.Rule.HasBeforePasteTransform)
        {
            return FormattingResult.NotApplied(text);
        }

        var protection = ProtectedText.ProtectForFormatting(text);
        FormattingResult formatted;
        try
        {
            formatted = match.Rule.BeforePaste(protection.Text, liveContext);
        }
        catch (Exception ex)
        {
            return FormattingResult.HookFailed(text, ex);
        }

        if (!formatted.Applied)
        {
            return formatted.FailureCode is null
                ? FormattingResult.NotApplied(text)
                : formatted with { Text = text };
        }

        var restored = ProtectedText.Restore(formatted.Text, protection);
        if (!restored.Success)
        {
            return FormattingResult.Unsafe(text, "literal_restore_failed");
        }

        return formatted with { Text = restored.Text };
    }

    public bool ValidateDestination(FormattingMatch? match, TargetContext start, TargetContext live)
    {
        return match is null
            ? start.HasSameDesktopDestination(live)
            : IsSameDestination(match.StartingContext, live, match.Rule);
    }

    private static bool IsSameDestination(
        TargetContext start,
        TargetContext live,
        ITargetFormattingRule rule)
    {
        if (!start.HasSameDesktopDestination(live) || !rule.Matches(live))
        {
            return false;
        }

        if (rule.MatchType != TargetFormattingMatchType.Site)
        {
            return true;
        }

        return start.Browser is { } startBrowser
            && live.Browser is { } liveBrowser
            && TargetMatch.IsSupportedWebContext(liveBrowser)
            && startBrowser.WindowId == liveBrowser.WindowId
            && startBrowser.TabId == liveBrowser.TabId;
    }
}
