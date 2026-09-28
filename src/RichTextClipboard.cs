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
            return RichTextReplacementResult.NotApplied("no_html", correctedText);
        }

        if (!TryExtractFragment(sourceHtml, out var fragment))
        {
            return RichTextReplacementResult.NotApplied("invalid_cf_html", correctedText);
        }

        // data-pm-slice is the stable marker in the captured ChatGPT selection.
        // Do not apply a structural assumption to a different HTML producer.
        if (!fragment.Contains("data-pm-slice", StringComparison.Ordinal))
        {
            return RichTextReplacementResult.NotApplied("unsupported_editor", correctedText);
        }

        if (TryCreateNestedOrderedListReplacement(
                fragment,
                sourceText,
                correctedText,
                out var nestedListReplacement))
        {
            return nestedListReplacement;
        }

        if (TryCreateStructuredListReplacement(
                fragment,
                sourceText,
                correctedText,
                out var structuredListReplacement))
        {
            return structuredListReplacement;
        }

        var paragraphs = ParagraphRegex().Matches(fragment);
        if (paragraphs.Count == 0 || !string.IsNullOrWhiteSpace(ParagraphRegex().Replace(fragment, "")))
        {
            return RichTextReplacementResult.NotApplied("unsupported_fragment", correctedText);
        }

        var sourceParagraphs = paragraphs
            .Select(match => WebUtility.HtmlDecode(match.Groups["text"].Value))
            .Where(text => text.Length > 0)
            .ToArray();
        var sourceSections = SplitSections(sourceText);
        var correctedSections = SplitSections(correctedText);

        // ProseMirror serializes a selected ordered-list item as a bare <p>
        // while its Unicode flavor adds the generated "1. " marker. Retaining
        // that Unicode flavor would make ChatGPT paste a nested list. Accept
        // only the exact one-item shape and replace its body with a neutral
        // paragraph slice.
        var listItem = false;
        if (!sourceParagraphs.SequenceEqual(sourceSections, StringComparer.Ordinal)
            && TryNormalizeListItem(
                fragment,
                paragraphs.Count,
                sourceParagraphs,
                sourceSections,
                correctedSections,
                out var normalizedSourceSections,
                out var normalizedCorrectedSections))
        {
            sourceSections = normalizedSourceSections;
            correctedSections = normalizedCorrectedSections;
            listItem = true;
        }

        if (!sourceParagraphs.SequenceEqual(sourceSections, StringComparer.Ordinal)
            || correctedSections.Length != sourceParagraphs.Length)
        {
            return RichTextReplacementResult.NotApplied("model_mismatch", correctedText, paragraphs.Count);
        }

        // The model often trims spaces at paragraph edges. Those spaces belong
        // to the selected text, so keep them while changing only its words.
        for (var i = 0; i < correctedSections.Length; i++)
        {
            var source = sourceParagraphs[i];
            var leading = source.Length - source.TrimStart(' ', '\t').Length;
            var trailing = source.Length - source.TrimEnd(' ', '\t').Length;
            correctedSections[i] = source[..leading]
                + correctedSections[i].Trim(' ', '\t')
                + source[(source.Length - trailing)..];
        }

        var breaks = SectionBreakRegex().Matches(correctedText.Replace("\r\n", "\n", StringComparison.Ordinal));
        var normalizedText = string.Join("", correctedSections.Select((section, i) =>
            i < breaks.Count ? section + breaks[i].Value : section));

        if (listItem)
        {
            // ChatGPT treats every HTML paragraph/list slice as a new block
            // when pasted over selected list-item text. Paste only the body as
            // Unicode text so it replaces the selection inside the existing
            // item instead of creating another list node.
            return RichTextReplacementResult.CreatePlainText(correctedSections[0], paragraphs.Count);
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

        return RichTextReplacementResult.CreateHtml(
            normalizedText,
            BuildCfHtml(rebuilt.ToString()),
            paragraphs.Count);
    }

    private static bool TryCreateStructuredListReplacement(
        string fragment,
        string sourceText,
        string correctedText,
        out RichTextReplacementResult replacement)
    {
        replacement = RichTextReplacementResult.NotApplied("unsupported_fragment", correctedText);
        if (!fragment.StartsWith("<ol", StringComparison.OrdinalIgnoreCase)
            && !fragment.StartsWith("<ul", StringComparison.OrdinalIgnoreCase)
            && !fragment.StartsWith("<li", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var paragraphs = ParagraphRegex().Matches(fragment);
        if (paragraphs.Count < 2)
        {
            return false;
        }

        var structureOnly = ParagraphRegex().Replace(fragment, "");
        if (!string.IsNullOrWhiteSpace(ListStructureTagRegex().Replace(structureOnly, "")))
        {
            return false;
        }

        var sourceLines = sourceText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (sourceLines.Length != paragraphs.Count)
        {
            return false;
        }

        var prefixes = new string[sourceLines.Length];
        for (var i = 0; i < sourceLines.Length; i++)
        {
            SplitGeneratedListPrefix(sourceLines[i], out prefixes[i], out var sourceBody);
            if (!string.Equals(
                    WebUtility.HtmlDecode(paragraphs[i].Groups["text"].Value),
                    sourceBody,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        var correctedLines = correctedText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var correctedBodies = new string[sourceLines.Length];
        var correctedIndex = 0;
        for (var i = 0; i < sourceLines.Length; i++)
        {
            while (correctedIndex < correctedLines.Length
                && correctedLines[correctedIndex].Length == 0
                && prefixes[i].Length > 0)
            {
                correctedIndex++;
            }

            if (correctedIndex >= correctedLines.Length)
            {
                return false;
            }

            SplitGeneratedListPrefix(
                correctedLines[correctedIndex++],
                out var correctedPrefix,
                out correctedBodies[i]);
            if (!string.Equals(prefixes[i], correctedPrefix, StringComparison.Ordinal))
            {
                return false;
            }
        }

        while (correctedIndex < correctedLines.Length && correctedLines[correctedIndex].Length == 0)
        {
            correctedIndex++;
        }

        if (correctedIndex != correctedLines.Length)
        {
            return false;
        }

        var rebuilt = new StringBuilder(fragment.Length + correctedText.Length - sourceText.Length);
        var cursor = 0;
        for (var i = 0; i < paragraphs.Count; i++)
        {
            var text = paragraphs[i].Groups["text"];
            rebuilt.Append(fragment, cursor, text.Index - cursor);
            rebuilt.Append(WebUtility.HtmlEncode(correctedBodies[i]));
            cursor = text.Index + text.Length;
        }
        rebuilt.Append(fragment, cursor, fragment.Length - cursor);

        var normalizedText = string.Join(
            "\n",
            prefixes.Zip(correctedBodies, (prefix, body) => prefix + body));
        replacement = RichTextReplacementResult.CreateStructuredListHtml(
            normalizedText,
            BuildCfHtml(rebuilt.ToString()),
            paragraphs.Count);
        return true;
    }

    private static bool TryCreateNestedOrderedListReplacement(
        string fragment,
        string sourceText,
        string correctedText,
        out RichTextReplacementResult replacement)
    {
        replacement = RichTextReplacementResult.NotApplied("unsupported_fragment", correctedText);

        var match = NestedOrderedListRegex().Match(fragment);
        if (!match.Success || match.Length != fragment.Length)
        {
            return false;
        }

        var sourceLines = sourceText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var correctedLines = correctedText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (sourceLines.Length != 2
            || correctedLines.Length != 2
            || !TrySplitOrderedListMarker(sourceLines[0], out var sourceParentMarker, out var sourceParentBody)
            || !TrySplitOrderedListMarker(sourceLines[1], out var sourceChildMarker, out var sourceChildBody)
            || !TrySplitOrderedListMarker(correctedLines[0], out var correctedParentMarker, out var correctedParentBody)
            || !TrySplitOrderedListMarker(correctedLines[1], out var correctedChildMarker, out var correctedChildBody)
            || !string.Equals(sourceParentMarker, correctedParentMarker, StringComparison.Ordinal)
            || !string.Equals(sourceChildMarker, correctedChildMarker, StringComparison.Ordinal)
            || !string.Equals(WebUtility.HtmlDecode(match.Groups["parent"].Value), sourceParentBody, StringComparison.Ordinal)
            || !string.Equals(WebUtility.HtmlDecode(match.Groups["child"].Value), sourceChildBody, StringComparison.Ordinal))
        {
            return false;
        }

        var parent = match.Groups["parent"];
        var child = match.Groups["child"];
        var rebuilt = new StringBuilder(fragment.Length + correctedText.Length - sourceText.Length);
        rebuilt.Append(fragment, 0, parent.Index);
        rebuilt.Append(WebUtility.HtmlEncode(correctedParentBody));
        rebuilt.Append(fragment, parent.Index + parent.Length, child.Index - parent.Index - parent.Length);
        rebuilt.Append(WebUtility.HtmlEncode(correctedChildBody));
        rebuilt.Append(fragment, child.Index + child.Length, fragment.Length - child.Index - child.Length);

        replacement = RichTextReplacementResult.CreateNestedListHtml(
            correctedText,
            BuildCfHtml(rebuilt.ToString()));
        return true;
    }

    private static string[] SplitSections(string text)
    {
        return SectionBreakRegex().Split(text.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static bool TryNormalizeListItem(
        string fragment,
        int paragraphCount,
        string[] sourceParagraphs,
        string[] sourceSections,
        string[] correctedSections,
        out string[] normalizedSourceSections,
        out string[] normalizedCorrectedSections)
    {
        normalizedSourceSections = Array.Empty<string>();
        normalizedCorrectedSections = Array.Empty<string>();

        if (paragraphCount != 1
            || sourceParagraphs.Length != 1
            || sourceSections.Length != 1
            || correctedSections.Length != 1
            || (!fragment.Contains("&quot;ordered_list&quot;", StringComparison.Ordinal)
                && !fragment.Contains("&quot;list&quot;", StringComparison.Ordinal))
            || !TrySplitGeneratedListPrefix(sourceSections[0], out var sourceMarker, out var sourceBody)
            || !TrySplitGeneratedListPrefix(correctedSections[0], out var correctedMarker, out var correctedBody)
            || !string.Equals(sourceMarker, correctedMarker, StringComparison.Ordinal)
            || !string.Equals(sourceParagraphs[0], sourceBody, StringComparison.Ordinal))
        {
            return false;
        }

        normalizedSourceSections = new[] { sourceBody };
        normalizedCorrectedSections = new[] { correctedBody };
        return true;
    }

    private static bool TrySplitGeneratedListPrefix(string value, out string prefix, out string body)
    {
        SplitGeneratedListPrefix(value, out prefix, out body);
        return prefix.Length > 0;
    }

    private static bool TrySplitOrderedListMarker(string value, out string marker, out string body)
    {
        marker = "";
        body = "";

        var index = 0;
        while (index < value.Length && value[index] is ' ' or '\t')
        {
            index++;
        }

        var digitStart = index;
        while (index < value.Length && char.IsAsciiDigit(value[index]))
        {
            index++;
        }

        if (index == digitStart
            || index + 1 >= value.Length
            || value[index] != '.'
            || value[index + 1] != ' ')
        {
            return false;
        }

        marker = value[..(index + 2)];
        body = value[(index + 2)..];
        return true;
    }

    private static void SplitGeneratedListPrefix(string value, out string prefix, out string body)
    {
        if (TrySplitOrderedListMarker(value, out prefix, out body))
        {
            return;
        }

        var index = 0;
        while (index < value.Length && value[index] is ' ' or '\t')
        {
            index++;
        }

        if (index + 1 < value.Length && value[index] == '-' && value[index + 1] == ' ')
        {
            prefix = value[..(index + 2)];
            body = value[(index + 2)..];
            return;
        }

        prefix = "";
        body = value;
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

    [GeneratedRegex("<p\\b[^>]*>(?:(?<text>[^<]*)|<span\\b[^>]*>(?<text>[^<]*)</span>)</p>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ParagraphRegex();

    [GeneratedRegex("<li\\b[^>]*data-pm-slice=\\\"[^\\\"]+\\\"[^>]*><p><span\\b[^>]*>(?<parent>[^<]*)</span></p><ol\\b[^>]*><li><p><span\\b[^>]*>(?<child>[^<]*)</span></p></li></ol></li>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NestedOrderedListRegex();

    [GeneratedRegex("</?(?:ol|ul|li)\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ListStructureTagRegex();

    [GeneratedRegex("\\n{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex SectionBreakRegex();

    [GeneratedRegex("(?m)^StartFragment:(?<offset>\\d+)\\r?$", RegexOptions.CultureInvariant)]
    private static partial Regex StartFragmentRegex();

    [GeneratedRegex("(?m)^EndFragment:(?<offset>\\d+)\\r?$", RegexOptions.CultureInvariant)]
    private static partial Regex EndFragmentRegex();
}

internal sealed record RichTextReplacementResult(
    string Text,
    string Html,
    string Mode,
    bool Attempted,
    bool Applied,
    string Reason,
    int ParagraphCount)
{
    public static RichTextReplacementResult NotApplied(
        string reason,
        string text = "",
        int paragraphCount = 0) => new(
        Text: text,
        Html: "",
        Mode: "none",
        Attempted: reason is not "no_html" and not "not_attempted",
        Applied: false,
        Reason: reason,
        ParagraphCount: paragraphCount);

    public static RichTextReplacementResult CreateHtml(
        string text,
        string html,
        int paragraphCount) => new(
        Text: text,
        Html: html,
        Mode: "html",
        Attempted: true,
        Applied: true,
        Reason: "",
        ParagraphCount: paragraphCount);

    public static RichTextReplacementResult CreatePlainText(string text, int paragraphCount) => new(
        Text: text,
        Html: "",
        Mode: "list_body_text",
        Attempted: true,
        Applied: true,
        Reason: "",
        ParagraphCount: paragraphCount);

    public static RichTextReplacementResult CreateNestedListHtml(string text, string html) => new(
        Text: text,
        Html: html,
        Mode: "nested_list_html",
        Attempted: true,
        Applied: true,
        Reason: "",
        ParagraphCount: 2);

    public static RichTextReplacementResult CreateStructuredListHtml(
        string text,
        string html,
        int paragraphCount) => new(
        Text: text,
        Html: html,
        Mode: "structured_list_html",
        Attempted: true,
        Applied: true,
        Reason: "",
        ParagraphCount: paragraphCount);
}
