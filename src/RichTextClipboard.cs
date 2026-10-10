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
            if (TryAlignTextNodes(fragment, sourceText, correctedText, out var aligned))
                return aligned;
            return RichTextReplacementResult.NotApplied("unsupported_fragment", aligned.Text);
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
            if (TryAlignTextNodes(fragment, sourceText, correctedText, out var aligned))
                return aligned;
            return RichTextReplacementResult.NotApplied("model_mismatch", aligned.Text, paragraphs.Count);
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

    // Align edits to the original HTML text nodes. The clipboard's Unicode
    // flavor adds list markers and Markdown-style link wrappers that are not
    // present in CF_HTML, so only edits whose source characters map to an
    // actual node may be written back into markup.
    private static bool TryAlignTextNodes(
        string fragment,
        string sourceText,
        string correctedText,
        out RichTextReplacementResult replacement)
    {
        replacement = RichTextReplacementResult.NotApplied("unsupported_fragment", correctedText);
        var nodes = new List<HtmlTextNode>();
        for (var i = 0; i < fragment.Length;)
        {
            if (fragment[i] == '<')
            {
                var tagEnd = FindTagEnd(fragment, i);
                if (tagEnd < 0)
                    return false;
                i = tagEnd + 1;
                continue;
            }

            var htmlStart = i;
            while (i < fragment.Length && fragment[i] != '<')
                i++;
            var decoded = WebUtility.HtmlDecode(fragment[htmlStart..i]);
            if (decoded.Length == 0)
                continue;
            nodes.Add(new HtmlTextNode(htmlStart, i - htmlStart, -1, decoded));
        }

        if (nodes.Count == 0)
            return false;

        var sourceCursor = 0;
        for (var i = 0; i < nodes.Count; i++)
        {
            var candidate = sourceText.IndexOf(nodes[i].Text, sourceCursor, StringComparison.Ordinal);
            var belongsToLaterNode = false;
            for (var j = i + 1; j < nodes.Count; j++)
            {
                var future = sourceText.IndexOf(nodes[j].Text, sourceCursor, StringComparison.Ordinal);
                if (future >= 0 && future < candidate
                    && future + nodes[j].Text.Length >= candidate + nodes[i].Text.Length)
                {
                    belongsToLaterNode = true;
                    break;
                }
            }

            // ChatGPT can omit visible headings from its Unicode clipboard
            // flavor. Keep such HTML nodes untouched instead of mapping a
            // repeated word in a later list item to the missing heading.
            // A later standalone space or typo can occur earlier in Unicode;
            // that is not evidence that this whole node was omitted. Only a
            // later node containing the candidate can claim its characters.
            if (candidate < 0 || belongsToLaterNode)
                continue;
            nodes[i] = nodes[i] with { TextStart = candidate };
            sourceCursor = candidate + nodes[i].Text.Length;
        }

        if (nodes.All(node => node.TextStart < 0))
            return false;

        var completeCorrectedText = RecoverMissingHtmlText(correctedText, sourceText, nodes);
        replacement = RichTextReplacementResult.NotApplied("unsupported_fragment", completeCorrectedText);

        var output = nodes.Select(_ => new StringBuilder()).ToArray();
        var sourcePosition = 0;
        var deletedWhitespaceStart = -1;
        foreach (var edit in ComputeAlignmentDiff(sourceText, correctedText))
        {
            switch (edit.Kind)
            {
                case UI.TextDiffKind.Equal:
                    deletedWhitespaceStart = -1;
                    foreach (var character in edit.Text)
                    {
                        var owner = FindTextNode(nodes, sourcePosition++);
                        if (owner >= 0)
                            output[owner].Append(character);
                    }
                    break;
                case UI.TextDiffKind.Delete:
                    foreach (var character in edit.Text)
                    {
                        if (FindTextNode(nodes, sourcePosition) < 0)
                        {
                            if (!char.IsWhiteSpace(character))
                                return false;
                            if (deletedWhitespaceStart < 0)
                                deletedWhitespaceStart = sourcePosition;
                        }
                        else
                        {
                            deletedWhitespaceStart = -1;
                        }
                        sourcePosition++;
                    }
                    break;
                case UI.TextDiffKind.Insert:
                    var target = FindInsertionNode(nodes, sourcePosition);
                    // LCS deletes the clipboard's trailing newline before inserting
                    // final punctuation. Anchor that insertion at the removed suffix,
                    // rather than beyond the last HTML text node.
                    if (target < 0 && sourcePosition == sourceText.Length && deletedWhitespaceStart >= 0)
                        target = FindInsertionNode(nodes, deletedWhitespaceStart);
                    if (target < 0)
                    {
                        if (edit.Text.All(char.IsWhiteSpace))
                            break;
                        return false;
                    }
                    if (edit.Text.Contains('\n') || edit.Text.Contains('\r'))
                        return false;
                    output[target].Append(edit.Text);
                    deletedWhitespaceStart = -1;
                    break;
            }
        }

        if (sourcePosition != sourceText.Length)
            return false;

        var rebuilt = new StringBuilder(fragment.Length + correctedText.Length - sourceText.Length);
        var htmlCursor = 0;
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            rebuilt.Append(fragment, htmlCursor, node.HtmlStart - htmlCursor);
            rebuilt.Append(node.TextStart < 0 || output[i].ToString() == node.Text
                ? fragment.Substring(node.HtmlStart, node.HtmlLength)
                : WebUtility.HtmlEncode(output[i].ToString()));
            htmlCursor = node.HtmlStart + node.HtmlLength;
        }
        rebuilt.Append(fragment, htmlCursor, fragment.Length - htmlCursor);

        replacement = RichTextReplacementResult.CreateAlignedHtml(
            completeCorrectedText,
            BuildCfHtml(rebuilt.ToString()),
            ParagraphRegex().Matches(fragment).Count);
        return true;
    }

    private static int FindTextNode(IReadOnlyList<HtmlTextNode> nodes, int position)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].TextStart >= 0
                && position >= nodes[i].TextStart && position < nodes[i].TextStart + nodes[i].Text.Length)
                return i;
        }
        return -1;
    }

    private static IReadOnlyList<UI.TextDiffSegment> ComputeAlignmentDiff(string sourceText, string correctedText)
    {
        // Keep the dashboard's bounded diff, but do not charge unchanged
        // selection edges against its matrix budget during rich reconstruction.
        if ((long)sourceText.Length * correctedText.Length <= 1_000_000)
            return UI.InlineTextDiff.ComputeChars(sourceText, correctedText);

        var prefix = 0;
        while (prefix < Math.Min(sourceText.Length, correctedText.Length)
            && sourceText[prefix] == correctedText[prefix])
            prefix++;
        var suffix = 0;
        while (suffix < Math.Min(sourceText.Length, correctedText.Length) - prefix
            && sourceText[^(suffix + 1)] == correctedText[^(suffix + 1)])
            suffix++;

        var segments = new List<UI.TextDiffSegment>();
        if (prefix > 0)
            segments.Add(new(sourceText[..prefix], UI.TextDiffKind.Equal));
        segments.AddRange(UI.InlineTextDiff.ComputeChars(
            sourceText.Substring(prefix, sourceText.Length - prefix - suffix),
            correctedText.Substring(prefix, correctedText.Length - prefix - suffix)));
        if (suffix > 0)
            segments.Add(new(sourceText[^suffix..], UI.TextDiffKind.Equal));
        return segments;
    }

    private static int FindInsertionNode(IReadOnlyList<HtmlTextNode> nodes, int position)
    {
        var atStart = FindTextNode(nodes, position);
        if (atStart >= 0)
            return atStart;
        for (var i = nodes.Count - 1; i >= 0; i--)
        {
            if (nodes[i].TextStart >= 0
                && position == nodes[i].TextStart + nodes[i].Text.Length)
                return i;
        }
        return -1;
    }

    private static string RecoverMissingHtmlText(
        string correctedText,
        string sourceText,
        IReadOnlyList<HtmlTextNode> nodes)
    {
        var recovered = correctedText;
        for (var i = nodes.Count - 1; i >= 0; i--)
        {
            if (nodes[i].TextStart >= 0 || string.IsNullOrWhiteSpace(nodes[i].Text))
                continue;

            var nextSourcePosition = sourceText.Length;
            for (var j = i + 1; j < nodes.Count; j++)
            {
                if (nodes[j].TextStart >= 0)
                {
                    nextSourcePosition = nodes[j].TextStart;
                    break;
                }
            }
            var correctedPosition = MapSourceOffset(sourceText, correctedText, nextSourcePosition);
            recovered = recovered.Insert(correctedPosition, nodes[i].Text + "\n\n");
        }
        return recovered;
    }

    private static int MapSourceOffset(string sourceText, string correctedText, int targetPosition)
    {
        var sourcePosition = 0;
        var correctedPosition = 0;
        foreach (var edit in ComputeAlignmentDiff(sourceText, correctedText))
        {
            if (edit.Kind == UI.TextDiffKind.Insert)
            {
                correctedPosition += edit.Text.Length;
                continue;
            }
            if (targetPosition <= sourcePosition + edit.Text.Length)
                return correctedPosition + (edit.Kind == UI.TextDiffKind.Equal
                    ? targetPosition - sourcePosition
                    : 0);
            sourcePosition += edit.Text.Length;
            if (edit.Kind == UI.TextDiffKind.Equal)
                correctedPosition += edit.Text.Length;
        }
        return correctedPosition;
    }

    private static int FindTagEnd(string html, int start)
    {
        var quote = '\0';
        for (var i = start + 1; i < html.Length; i++)
        {
            if (quote == '\0' && html[i] is '\'' or '"')
                quote = html[i];
            else if (html[i] == quote)
                quote = '\0';
            else if (html[i] == '>' && quote == '\0')
                return i;
        }
        return -1;
    }

    private sealed record HtmlTextNode(int HtmlStart, int HtmlLength, int TextStart, string Text);

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

    public static RichTextReplacementResult CreateAlignedHtml(
        string text,
        string html,
        int paragraphCount) => new(
        Text: text,
        Html: html,
        Mode: "aligned_html",
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
