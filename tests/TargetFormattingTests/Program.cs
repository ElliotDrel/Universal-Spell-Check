using System.Diagnostics;
using System.Text;
using UniversalSpellCheck;

const long now = 10_000;
const long freshness = 1_000;

const string chatGptSourceText = "make 2 di fone for this. \u201cSo I tried to fix it myself.\u201d  \n\n\n\nmake image 1 rn (4 versions like always)";
const string chatGptCorrectedText = "make 2 different ones for this. \u201cSo I tried to fix it myself.\u201d\n\nmake image 1 right now (4 versions like always)";
const string chatGptFragment = "<p data-pm-slice=\"1 1 []\">make 2 di fone for this. \u201cSo I tried to fix it myself.\u201d  </p><p></p><p>make image 1 rn (4 versions like always)</p>";
var richTextReplacement = RichTextClipboard.TryCreateReplacement(
    CfHtml(chatGptFragment),
    chatGptSourceText,
    chatGptCorrectedText);
Assert(richTextReplacement.Applied,
    "simple ChatGPT ProseMirror paragraphs must retain their HTML structure on replacement");
Assert(richTextReplacement.Text == chatGptCorrectedText && richTextReplacement.Mode == "html",
    "ordinary paragraph replacement must retain corrected Unicode text alongside HTML");
Assert(richTextReplacement.Html.Contains(
    "</p><p></p><p>make image 1 right now (4 versions like always)</p>",
    StringComparison.Ordinal),
    "the empty ChatGPT paragraph must not become four plain-text newlines on paste");
Assert(richTextReplacement.Html.Contains(
    "make 2 different ones for this. \u201cSo I tried to fix it myself.\u201d</p>",
    StringComparison.Ordinal),
    "the corrected first paragraph must be written into the source HTML");
Assert(Fragment(richTextReplacement.Html).Contains(
    "</p><p></p><p>make image 1 right now (4 versions like always)</p>",
    StringComparison.Ordinal),
    "CF_HTML offsets must locate the UTF-8 fragment after multi-byte text");

var nestedMarkup = RichTextClipboard.TryCreateReplacement(
    CfHtml("<p data-pm-slice=\"1 1 []\">one <strong>two</strong></p>"),
    "one two",
    "one too");
Assert(!nestedMarkup.Applied && nestedMarkup.Reason == "unsupported_fragment",
    "rich-text paste must fall back rather than flattening or guessing nested markup");

var structureChanged = RichTextClipboard.TryCreateReplacement(
    CfHtml(chatGptFragment),
    chatGptSourceText,
    "make 2 different ones for this. \u201cSo I tried to fix it myself.\u201d\n\nextra paragraph\n\nmake image 1 right now (4 versions like always)");
Assert(!structureChanged.Applied && structureChanged.Reason == "model_mismatch",
    "a model output with a different paragraph count must safely use the plain-text fallback");

const string orderedListSourceText = "1. actaull cycle time is how time it takes to pop out each item per flow unit.";
const string orderedListCorrectedText = "1. Actual cycle time is how long it takes to pop out each item per flow unit.";
const string orderedListFragment = "<p data-pm-slice=\"1 1 [&quot;ordered_list&quot;,{&quot;spread&quot;:false,&quot;startingNumber&quot;:1,&quot;start&quot;:0,&quot;end&quot;:245},&quot;regular_list_item&quot;,{&quot;start&quot;:0,&quot;end&quot;:245}]\">actaull cycle time is how time it takes to pop out each item per flow unit.</p>";
var orderedListReplacement = RichTextClipboard.TryCreateReplacement(
    CfHtml(orderedListFragment),
    orderedListSourceText,
    orderedListCorrectedText);
Assert(orderedListReplacement.Applied,
    "a ChatGPT ordered-list slice with its generated Unicode marker must use a body-only replacement");
Assert(orderedListReplacement.Mode == "list_body_text"
    && orderedListReplacement.Html.Length == 0
    && orderedListReplacement.Text == "Actual cycle time is how long it takes to pop out each item per flow unit.",
    "the replacement must contain no generated marker or HTML block structure");

const string orderedListSpanFragment = "<p data-pm-slice=\"1 1 [&quot;ordered_list&quot;,{&quot;spread&quot;:false,&quot;startingNumber&quot;:1,&quot;start&quot;:0,&quot;end&quot;:500},&quot;regular_list_item&quot;,{&quot;start&quot;:202,&quot;end&quot;:500}]\"><span>because direct labor cost is high</span></p>";
var orderedListSpanReplacement = RichTextClipboard.TryCreateReplacement(
    CfHtml(orderedListSpanFragment),
    "1. because direct labor cost is high",
    "1. Because direct labor cost is high.");
Assert(orderedListSpanReplacement.Applied
    && orderedListSpanReplacement.Mode == "list_body_text"
    && orderedListSpanReplacement.Html.Length == 0
    && orderedListSpanReplacement.Text == "Because direct labor cost is high.",
    "a simple span-wrapped list item must use the same body-only replacement");

const string nestedOrderedListFragment = "<p data-pm-slice=\"1 1 [&quot;ordered_list&quot;,{&quot;spread&quot;:false,&quot;startingNumber&quot;:1,&quot;end&quot;:297},&quot;regular_list_item&quot;,{&quot;end&quot;:297},&quot;ordered_list&quot;,null,&quot;regular_list_item&quot;,{&quot;end&quot;:297}]\"><span>chanaged nested item</span></p>";
var nestedOrderedListReplacement = RichTextClipboard.TryCreateReplacement(
    CfHtml(nestedOrderedListFragment),
    "   1. chanaged nested item",
    "   1. changed nested item");
Assert(nestedOrderedListReplacement.Applied
    && nestedOrderedListReplacement.Mode == "list_body_text"
    && nestedOrderedListReplacement.Html.Length == 0
    && nestedOrderedListReplacement.Text == "changed nested item",
    "an indented nested list marker must be stripped before the body-only replacement");

const string parentWithChildFragment = "<li data-pm-slice=\"2 4 [&quot;ordered_list&quot;,{&quot;spread&quot;:true,&quot;startingNumber&quot;:1,&quot;start&quot;:0,&quot;end&quot;:618}]\"><p><span>teast parent</span></p><ol data-spread=\"false\" start=\"1\"><li><p><span>chanaged child</span></p></li></ol></li>";
var parentWithChildReplacement = RichTextClipboard.TryCreateReplacement(
    CfHtml(parentWithChildFragment),
    "1. teast parent\n   1. chanaged child",
    "1. test parent\n   1. changed child");
Assert(parentWithChildReplacement.Applied
    && parentWithChildReplacement.Mode == "nested_list_html"
    && parentWithChildReplacement.Text == "1. test parent\n   1. changed child"
    && Fragment(parentWithChildReplacement.Html) == "<li data-pm-slice=\"2 4 [&quot;ordered_list&quot;,{&quot;spread&quot;:true,&quot;startingNumber&quot;:1,&quot;start&quot;:0,&quot;end&quot;:618}]\"><p><span>test parent</span></p><ol data-spread=\"false\" start=\"1\"><li><p><span>changed child</span></p></li></ol></li>",
    "a selected parent and child must retain the native nested-list subtree while replacing both bodies");

var changedListMarker = RichTextClipboard.TryCreateReplacement(
    CfHtml(orderedListFragment),
    orderedListSourceText,
    "2. Actual cycle time is how long it takes to pop out each item per flow unit.");
Assert(!changedListMarker.Applied && changedListMarker.Reason == "model_mismatch",
    "a changed generated list marker must fall back rather than attach to the wrong list item");

var desktop = Context("Code", processId: 10, hwnd: 100, rootOwner: 90);
var sameDesktop = Context("CODE", processId: 10, hwnd: 101, rootOwner: 90);

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
var switchedApp = Context("notepad", processId: 11, hwnd: 102, rootOwner: 91);
var afterSwitch = hookPipeline.ApplyBeforePaste(hookMatch, "text", switchedApp);
Assert(afterSwitch.Text == "text before",
    "switching apps must not stop the frozen before-paste hook");

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
    ("terminal markers", "one\r\n  \u258E two\r\n  \u258E\r\n  \u258E three", "one two\n\nthree"),
    ("URL and path", "See https://x.com/a\r\n  and C:\\foo\\bar", "See https://x.com/a and C:\\foo\\bar"),
    ("wrapped file path", "Open \"C:\\Users\\Elliot\\Downloads\\buildpurdue-meeting-t\r\n  ranscript.txt\".", "Open \"C:\\Users\\Elliot\\Downloads\\buildpurdue-meeting-transcript.txt\".")
};
foreach (var (name, input, expected) in terminalCases)
{
    var match = terminalPipeline.Resolve(desktop)!;
    var result = terminalPipeline.ApplyAfterCopy(match, input);
    Assert(result.Text == expected, $"terminal parity failed for {name}");
}
var wrappedPathResult = terminalPipeline.ApplyAfterCopy(
    terminalPipeline.Resolve(desktop)!,
    "Open \"C:\\Users\\Elliot\\Downloads\\buildpurdue-meeting-t\r\n  ranscript.txt\".");
var wrappedPathProtection = ProtectedText.Protect(wrappedPathResult.Text);
Assert(wrappedPathProtection.Entries.Count == 1
    && wrappedPathProtection.Entries[0].Kind == ProtectedLiteralKind.FilePath
    && wrappedPathProtection.Entries[0].Value == "\"C:\\Users\\Elliot\\Downloads\\buildpurdue-meeting-transcript.txt\"",
    "terminal normalization must repair wrapped file paths before literal protection");
Assert(ProtectedText.Restore(wrappedPathProtection.Text, wrappedPathProtection).Text == wrappedPathResult.Text,
    "repaired file paths must restore byte-for-byte after the model response");
var markerResult = terminalPipeline.ApplyAfterCopy(terminalPipeline.Resolve(desktop)!, "one\r\n  \u258E two");
Assert(markerResult.Operations.Contains("remove_terminal_marker")
    && markerResult.Counters![TerminalFormattingRule.TerminalMarkerCounter] == 1,
    "terminal marker normalization must report a stable operation and counter");
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

string CfHtml(string fragment)
{
    const string prefix = "<html>\r\n<body>\r\n<!--StartFragment-->";
    const string suffix = "<!--EndFragment-->\r\n</body>\r\n</html>";
    var html = prefix + fragment + suffix;
    const int headerLength = 105;
    var startFragment = headerLength + Encoding.UTF8.GetByteCount(prefix);
    var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
    var endHtml = headerLength + Encoding.UTF8.GetByteCount(html);
    return $"Version:0.9\r\nStartHTML:{headerLength:D10}\r\nEndHTML:{endHtml:D10}\r\nStartFragment:{startFragment:D10}\r\nEndFragment:{endFragment:D10}\r\n{html}";
}

string Fragment(string cfHtml)
{
    var start = HeaderOffset(cfHtml, "StartFragment:");
    var end = HeaderOffset(cfHtml, "EndFragment:");
    var bytes = Encoding.UTF8.GetBytes(cfHtml);
    return Encoding.UTF8.GetString(bytes, start, end - start);
}

int HeaderOffset(string cfHtml, string name)
{
    var start = cfHtml.IndexOf(name, StringComparison.Ordinal) + name.Length;
    var end = cfHtml.IndexOf("\r\n", start, StringComparison.Ordinal);
    return int.Parse(cfHtml[start..end]);
}

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
