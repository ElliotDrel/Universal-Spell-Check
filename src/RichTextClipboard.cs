using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace UniversalSpellCheck;

// Re-emits the small, unambiguous subset of CF_HTML that ChatGPT's ProseMirror
// editor places on the clipboard. Plain-text paste turns its paragraph
// separators into extra empty paragraphs, so preserve the original paragraph
// structure when the corrected text maps to it exactly.
internal static partial class RichTextClipboard
{
    private const string HtmlPrefix = "<html>\r\n<body>\r\n<!--StartFragment-->";
    private const string HtmlSuffix = "<!--EndFragment-->\r\n</body>\r\n</html>";
    private const int CfHtmlHeaderLength = 105;

    public static RichTextReplacementResult TryCreateReplacement(
        string sourceHtml,
        string sourceText,
        string correctedText)
    {
        if (sourceHtml.Length == 0)
        {
            return RichTextReplacementResult.NotApplied("no_html");
        }

        if (!TryExtractFragment(sourceHtml, out var fragment))
        {
            return RichTextReplacementResult.NotApplied("invalid_cf_html");
        }

        // data-pm-slice is the stable marker in the captured ChatGPT selection.
        // Do not apply a structural assumption to a different HTML producer.
        if (!fragment.Contains("data-pm-slice", StringComparison.Ordinal))
        {
            return RichTextReplacementResult.NotApplied("unsupported_editor");
        }

        var paragraphs = ParagraphRegex().Matches(fragment);
        if (paragraphs.Count == 0 || !string.IsNullOrWhiteSpace(ParagraphRegex().Replace(fragment, "")))
        {
            return RichTextReplacementResult.NotApplied("unsupported_fragment");
        }

        var sourceParagraphs = paragraphs
            .Select(match => WebUtility.HtmlDecode(match.Groups["text"].Value))
            .Where(text => text.Length > 0)
            .ToArray();
        var sourceSections = SplitSections(sourceText);
        var correctedSections = SplitSections(correctedText);

        if (!sourceParagraphs.SequenceEqual(sourceSections, StringComparer.Ordinal)
            || correctedSections.Length != sourceParagraphs.Length)
        {
            return RichTextReplacementResult.NotApplied("model_mismatch", paragraphs.Count);
        }

        var rebuilt = new StringBuilder(fragment.Length + correctedText.Length - sourceText.Length);
        var cursor = 0;
        var sectionIndex = 0;
        foreach (Match paragraph in paragraphs)
        {
            var text = paragraph.Groups["text"];
            rebuilt.Append(fragment, cursor, text.Index - cursor);
            if (text.Length == 0)
            {
                rebuilt.Append(text.Value);
            }
            else
            {
                rebuilt.Append(WebUtility.HtmlEncode(correctedSections[sectionIndex++]));
            }

            cursor = text.Index + text.Length;
        }
        rebuilt.Append(fragment, cursor, fragment.Length - cursor);

        return RichTextReplacementResult.CreateApplied(BuildCfHtml(rebuilt.ToString()), paragraphs.Count);
    }

    private static string[] SplitSections(string text)
    {
        return SectionBreakRegex().Split(text.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static bool TryExtractFragment(string cfHtml, out string fragment)
    {
        var startMatch = StartFragmentRegex().Match(cfHtml);
        var endMatch = EndFragmentRegex().Match(cfHtml);
        if (!startMatch.Success || !endMatch.Success
            || !int.TryParse(startMatch.Groups["offset"].Value, out var start)
            || !int.TryParse(endMatch.Groups["offset"].Value, out var end)
            || start < 0 || end < start)
        {
            fragment = "";
            return false;
        }

        var bytes = Encoding.UTF8.GetBytes(cfHtml);
        if (end > bytes.Length)
        {
            fragment = "";
            return false;
        }

        fragment = Encoding.UTF8.GetString(bytes, start, end - start);
        return true;
    }

    private static string BuildCfHtml(string fragment)
    {
        var html = HtmlPrefix + fragment + HtmlSuffix;
        var startHtml = CfHtmlHeaderLength;
        var startFragment = startHtml + Encoding.UTF8.GetByteCount(HtmlPrefix);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = startHtml + Encoding.UTF8.GetByteCount(html);
        var header = $"Version:0.9\r\nStartHTML:{startHtml:D10}\r\nEndHTML:{endHtml:D10}\r\nStartFragment:{startFragment:D10}\r\nEndFragment:{endFragment:D10}\r\n";

        return Encoding.UTF8.GetByteCount(header) == CfHtmlHeaderLength
            ? header + html
            : throw new InvalidOperationException("CF_HTML header length changed.");
    }

    [GeneratedRegex("<p\\b[^>]*>(?<text>[^<]*)</p>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ParagraphRegex();

    [GeneratedRegex("\\n{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex SectionBreakRegex();

    [GeneratedRegex("(?m)^StartFragment:(?<offset>\\d+)\\r?$", RegexOptions.CultureInvariant)]
    private static partial Regex StartFragmentRegex();

    [GeneratedRegex("(?m)^EndFragment:(?<offset>\\d+)\\r?$", RegexOptions.CultureInvariant)]
    private static partial Regex EndFragmentRegex();
}

internal sealed record RichTextReplacementResult(
    string Html,
    bool Attempted,
    bool Applied,
    string Reason,
    int ParagraphCount)
{
    public static RichTextReplacementResult NotApplied(string reason, int paragraphCount = 0) => new(
        Html: "",
        Attempted: reason is not "no_html" and not "not_attempted",
        Applied: false,
        Reason: reason,
        ParagraphCount: paragraphCount);

    public static RichTextReplacementResult CreateApplied(string html, int paragraphCount) => new(
        Html: html,
        Attempted: true,
        Applied: true,
        Reason: "",
        ParagraphCount: paragraphCount);
}
