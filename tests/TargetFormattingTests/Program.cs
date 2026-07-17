using System.Diagnostics;
using UniversalSpellCheck;

const long now = 10_000;
const long freshness = 1_000;

var desktop = Context("Code", processId: 10, hwnd: 100, rootOwner: 90);
var sameDesktop = Context("CODE", processId: 10, hwnd: 101, rootOwner: 90);
var otherDesktop = Context("Code", processId: 11, hwnd: 102, rootOwner: 91);

var terminalPipeline = new TargetFormattingPipeline();
Assert(terminalPipeline.Resolve(desktop)?.Rule.Id == TerminalFormattingRule.RuleId,
    "app matching must be case-insensitive");

var exactBrowser = Browser("docs.google.com", "/document/d/abc/edit", now);
Assert(TargetMatch.Host(exactBrowser, "DOCS.GOOGLE.COM"), "exact hostname must match case-insensitively");
Assert(TargetMatch.Host(Browser("a.docs.google.com", "/", now), "docs.google.com", includeSubdomains: true),
    "explicit subdomain matching must respect a label boundary");
Assert(!TargetMatch.Host(Browser("docs.google.com.example.com", "/", now), "docs.google.com", includeSubdomains: true),
    "hostname suffix without the correct label boundary must not match");

var browserContext = Context("chrome", browser: exactBrowser);
var pathRule = Rule("docs-document", TargetFormattingMatchType.Site,
    matches: c => TargetMatch.Host(c.Browser, "docs.google.com") && c.Browser!.Path.StartsWith("/document/", StringComparison.Ordinal));
var hostRule = Rule("docs", TargetFormattingMatchType.Site,
    matches: c => TargetMatch.Host(c.Browser, "docs.google.com"));
var chromeRule = Rule("chrome", TargetFormattingMatchType.App,
    matches: c => TargetMatch.ProcessName(c, "chrome"));
var precedencePipeline = Pipeline(new[] { pathRule, hostRule, chromeRule });
Assert(precedencePipeline.Resolve(browserContext)?.Rule.Id == "docs-document",
    "path-specific site rule must win over host-only and executable rules");
Assert(Pipeline(new[] { hostRule, chromeRule }).Resolve(browserContext)?.Rule.Id == "docs",
    "site rule must win over browser executable rule");

var noMatchText = "unchanged";
var noMatch = terminalPipeline.Resolve(Context("notepad"));
Assert(noMatch is null, "unmatched app must not resolve a rule");
var noResult = FormattingResult.NotApplied(noMatchText);
Assert(ReferenceEquals(noMatchText, noResult.Text) && noResult.Operations.Count == 0,
    "no-match result must retain the input reference and report no operations");

var hookOrder = new List<string>();
var bothHooks = Rule(
    "both",
    TargetFormattingMatchType.App,
    matches: _ => true,
    hasBeforePaste: true,
    afterCopy: (text, _) =>
    {
        hookOrder.Add("after_copy");
        return Applied(text + " after", "after");
    },
    beforePaste: (text, _) =>
    {
        hookOrder.Add("before_paste");
        return Applied(text + " before", "before");
    });
var hookPipeline = Pipeline(new[] { bothHooks });
var hookMatch = hookPipeline.Resolve(desktop)!;
var after = hookPipeline.ApplyAfterCopy(hookMatch, "text");
var before = hookPipeline.ApplyBeforePaste(hookMatch, after.Text, sameDesktop);
Assert(hookOrder.SequenceEqual(new[] { "after_copy", "before_paste" }), "hooks ran out of order");
Assert(before.Text == "text after before", "both hook transformations were not retained");

var inactiveRule = Rule(
    "inactive",
    TargetFormattingMatchType.App,
    matches: _ => true,
    hasBeforePaste: true);
var inactivePipeline = Pipeline(new[] { inactiveRule });
var inactiveMatch = inactivePipeline.Resolve(desktop)!;
Assert(!inactivePipeline.ApplyAfterCopy(inactiveMatch, noMatchText).Applied,
    "after-copy hook must be independently inactive");
Assert(!inactivePipeline.ApplyBeforePaste(inactiveMatch, noMatchText, sameDesktop).Applied,
    "before-paste hook must be independently inactive");

var throwingRule = Rule(
    "throws",
    TargetFormattingMatchType.App,
    matches: _ => true,
    hasBeforePaste: true,
    afterCopy: (_, _) => throw new InvalidOperationException("after"),
    beforePaste: (_, _) => throw new InvalidOperationException("before"));
var throwingPipeline = Pipeline(new[] { throwingRule });
var throwingMatch = throwingPipeline.Resolve(desktop)!;
var afterThrow = throwingPipeline.ApplyAfterCopy(throwingMatch, noMatchText);
var beforeThrow = throwingPipeline.ApplyBeforePaste(throwingMatch, noMatchText, sameDesktop);
Assert(ReferenceEquals(afterThrow.Text, noMatchText)
    && afterThrow.FailureCode == "hook_threw"
    && afterThrow.FailureType == nameof(InvalidOperationException),
    "thrown after-copy hook must retain unchanged text");
Assert(ReferenceEquals(beforeThrow.Text, noMatchText)
    && beforeThrow.FailureCode == "hook_threw"
    && beforeThrow.FailureType == nameof(InvalidOperationException),
    "thrown before-paste hook must retain unchanged text");

var missingBrowser = Context("chrome", browser: null);
var staleBrowser = Context("chrome", browser: Browser("docs.google.com", "/", now - freshness - 1));
Assert(Pipeline(new[] { hostRule }).Resolve(missingBrowser) is null,
    "missing browser context must not trigger a site rule");
Assert(Pipeline(new[] { hostRule }).Resolve(staleBrowser) is null,
    "stale browser context must not trigger a site rule");

Assert(hookPipeline.ValidateDestination(hookMatch, desktop, sameDesktop),
    "owned child windows with the same PID and root owner must remain valid");
Assert(!hookPipeline.ValidateDestination(hookMatch, desktop, otherDesktop),
    "different process/root-owner identity must fail validation");

var sitePipeline = Pipeline(new[] { pathRule });
var siteMatch = sitePipeline.Resolve(browserContext)!;
var switchedTab = browserContext with { Browser = exactBrowser with { TabId = 8 } };
var sameRuleNavigation = browserContext with
{
    Browser = exactBrowser with { Path = "/document/d/other/edit", ReceivedAtStopwatchTicks = now + 100 }
};
Assert(!sitePipeline.ValidateDestination(siteMatch, browserContext, switchedTab),
    "switching browser tabs must fail destination validation");
Assert(sitePipeline.ValidateDestination(siteMatch, browserContext, sameRuleNavigation),
    "navigation that remains inside the frozen rule must stay valid");

const string literals = "Keep https://example.com/a_b and C:\\work\\notes.md byte-for-byte.";
var markdownRule = Rule(
    "markdown",
    TargetFormattingMatchType.App,
    matches: _ => true,
    hasBeforePaste: true,
    beforePaste: (text, _) => Applied(text.Replace("byte-for-byte", "unchanged", StringComparison.Ordinal), "rewrite"));
var markdownPipeline = Pipeline(new[] { markdownRule });
var markdownMatch = markdownPipeline.Resolve(desktop)!;
var literalResult = markdownPipeline.ApplyBeforePaste(markdownMatch, literals, sameDesktop);
Assert(literalResult.Text == "Keep https://example.com/a_b and C:\\work\\notes.md unchanged.",
    "before-paste formatting must preserve protected literals byte-for-byte");

foreach (var corruption in new[] { "missing", "duplicate" })
{
    var corruptRule = Rule(
        corruption,
        TargetFormattingMatchType.App,
        matches: _ => true,
        hasBeforePaste: true,
        beforePaste: (text, _) =>
        {
            var start = text.IndexOf('\uE000');
            var end = text.IndexOf('\uE001', start) + 1;
            var placeholder = text[start..end];
            var corrupted = corruption == "missing"
                ? text.Replace(placeholder, "", StringComparison.Ordinal)
                : text.Replace(placeholder, placeholder + placeholder, StringComparison.Ordinal);
            return Applied(corrupted, corruption);
        });
    var corruptPipeline = Pipeline(new[] { corruptRule });
    var result = corruptPipeline.ApplyBeforePaste(corruptPipeline.Resolve(desktop)!, literals, sameDesktop);
    Assert(result.AbortPaste && result.FailureCode == "literal_restore_failed",
        $"{corruption} formatting placeholder must abort before paste");
}

var terminalCases = new[]
{
    ("double breaks", "one\r\n\r\n  two", "one\n\ntwo"),
    ("list items", "one\r\n  - two\r\n\t3. three", "one\n- two\n3. three"),
    ("soft wrap", "one \r\n  two", "one two"),
    ("URL and path", "See https://x.com/a\r\n  and C:\\foo\\bar", "See https://x.com/a and C:\\foo\\bar")
};
foreach (var (name, input, expected) in terminalCases)
{
    var match = terminalPipeline.Resolve(desktop)!;
    var result = terminalPipeline.ApplyAfterCopy(match, input);
    Assert(result.Text == expected, $"terminal parity failed for {name}");
}
var bareCrLf = "one\r\ntwo";
var bareResult = terminalPipeline.ApplyAfterCopy(terminalPipeline.Resolve(desktop)!, bareCrLf);
Assert(!bareResult.Applied && ReferenceEquals(bareCrLf, bareResult.Text),
    "bare CRLF must remain unchanged and retain the input reference");
Assert(terminalPipeline.Resolve(Context("notepad")) is null, "non-terminal process must not normalize");

const int iterations = 250_000;
var noMatchContext = Context("notepad");
var stopwatch = Stopwatch.StartNew();
for (var i = 0; i < iterations; i++)
{
    _ = terminalPipeline.Resolve(noMatchContext);
}
stopwatch.Stop();
var microsecondsPerResolve = stopwatch.Elapsed.TotalMilliseconds * 1000 / iterations;
Assert(microsecondsPerResolve < 1000, "no-match resolver exceeded the sub-millisecond contract");

Console.WriteLine(
    $"Target formatting tests passed. No-match resolver: {microsecondsPerResolve:N3} us/call over {iterations:N0} calls.");

TargetFormattingPipeline Pipeline(IReadOnlyList<ITargetFormattingRule> rules)
    => new(rules, () => now, freshness);

TargetContext Context(
    string processName,
    int processId = 10,
    int hwnd = 100,
    int rootOwner = 90,
    BrowserTargetContext? browser = null)
    => new(processName, processId, (IntPtr)hwnd, (IntPtr)rootOwner, "title", browser);

BrowserTargetContext Browser(string host, string path, long receivedAt)
    => new("chrome", true, 4, 7, "https", host, path, receivedAt, 1234);

FormattingResult Applied(string text, string operation)
    => new(text, true, 0, 0, new[] { operation });

DelegateRule Rule(
    string id,
    TargetFormattingMatchType matchType,
    Func<TargetContext, bool> matches,
    bool hasBeforePaste = false,
    Func<string, TargetContext, FormattingResult>? afterCopy = null,
    Func<string, TargetContext, FormattingResult>? beforePaste = null)
    => new(id, matchType, matches, hasBeforePaste, afterCopy, beforePaste);

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

internal sealed class DelegateRule(
    string id,
    TargetFormattingMatchType matchType,
    Func<TargetContext, bool> matches,
    bool hasBeforePaste,
    Func<string, TargetContext, FormattingResult>? afterCopy,
    Func<string, TargetContext, FormattingResult>? beforePaste) : ITargetFormattingRule
{
    public string Id => id;
    public TargetFormattingMatchType MatchType => matchType;
    public bool HasBeforePasteTransform => hasBeforePaste;
    public bool Matches(TargetContext context) => matches(context);
    public FormattingResult AfterCopy(string text, TargetContext context)
        => afterCopy?.Invoke(text, context) ?? FormattingResult.NotApplied(text);
    public FormattingResult BeforePaste(string text, TargetContext context)
        => beforePaste?.Invoke(text, context) ?? FormattingResult.NotApplied(text);
}
