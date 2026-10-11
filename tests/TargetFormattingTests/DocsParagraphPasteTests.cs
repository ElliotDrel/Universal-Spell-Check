using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using UniversalSpellCheck;

internal static class DocsParagraphPasteTests
{
    internal static void Run(Func<string, string> cfHtml)
    {
        void Check(bool valid, string why) { if (!valid) throw new InvalidOperationException(why); }
        const string html = "<b id=\"docs-internal-guid-test\" style=\"font-weight:normal;\"><ol><li><p><span style=\"font-weight:700\">tail chek</span></p></li></ol><ol><li><h2>Heading</h2><ol><li><p><a href=\"https://example.test\">last item</a></p></li></ol></li></ol></b>";
        const string copy = "tail chek\r\nHeading\r\nlast item\r\n";
        foreach (var correction in new[] { "tail check\nHeading\nlast item", "tail chek\nHeading\nlast item" })
        {
            var edits = RichTextClipboard.CreateDocsParagraphEdits(cfHtml(html), copy, correction);
            Check(edits is { Length: 3 }, "changed and unchanged selections must plan every paragraph");
            Check(string.Join("\n", edits!.Select(e => e.Text)) == correction, "planned paragraph text must preserve all selected content");
            Check(edits![0].Source == "tail chek" && edits[1].Source == edits[1].Text,
                "partial first paragraph and unchanged middle paragraph must both be included");
            Check(edits[0].Html.Contains("font-weight:700") && edits[2].Html.Contains("https://example.test"),
                "inline bold and hyperlinks must survive text-only paragraph mapping");
            foreach (var edit in edits)
                Check(!edit.Source.Contains('\n') && !edit.Text.Contains('\n')
                    && !Regex.IsMatch(edit.Html, @"</?(?:ol|ul|li|p|h[1-6]|br)\b", RegexOptions.IgnoreCase)
                    && !edit.Html.Contains("docs-internal-guid-"),
                    "no edit may replace a delimiter, import list/heading blocks, or reuse Docs' cached copy ID");
        }
        var one = cfHtml("<b id=\"docs-internal-guid-test\"><p>tail chek</p></b>");
        Check(RichTextClipboard.IsCopiedDocsParagraph(one, "tail chek\r\n", "tail chek"),
            "verified one-paragraph copy may contain Docs' synthetic final CRLF");
        Check(!RichTextClipboard.IsCopiedDocsParagraph(one, "ail chek\r\n", "tail chek")
            && !RichTextClipboard.IsCopiedDocsParagraph(one, "tail chek \r\n", "tail chek")
            && !RichTextClipboard.IsCopiedDocsParagraph(cfHtml(html), "tail chek", "tail chek"),
            "wrong endpoint, whitespace differences and structural selections must never authorize a paste");
        Check(RichTextClipboard.CreateDocsParagraphEdits(cfHtml(html), copy, "tail check\nHeading\nlast\nitem") is null,
            "changed paragraph counts must decline planning");
        var empty = html.Replace("<h2>Heading</h2>", "<h2><br></h2>");
        Check(RichTextClipboard.CreateDocsParagraphEdits(cfHtml(empty), "tail chek\r\n\r\nlast item\r\n", "tail check\n\nlast item") is null,
            "empty paragraphs require a separately verified selection contract");
        var soft = html.Replace("tail chek", "tail<br>chek");
        Check(RichTextClipboard.CreateDocsParagraphEdits(cfHtml(soft), "tail\r\nchek\r\nHeading\r\nlast item\r\n", "tail\ncheck\nHeading\nlast item") is null,
            "soft breaks cannot be mistaken for paragraph delimiters");

        var styles = new JsonArray(
            new JsonObject { ["stsl_type"] = "paragraph", ["stsl_styles"] = new JsonArray(null, new JsonObject { ["ps_il"] = 72 }, null, new JsonObject { ["ps_il"] = 36 }), ["stsl_trailing"] = new JsonObject { ["ps_il"] = 72 } },
            new JsonObject { ["stsl_type"] = "list", ["stsl_styles"] = new JsonArray(null, new JsonObject { ["ls_id"] = "sample", ["ls_nest"] = 1 }, null, new JsonObject { ["ls_id"] = "sample", ["ls_nest"] = 0 }), ["stsl_trailing"] = new JsonObject { ["ls_id"] = "sample", ["ls_nest"] = 1 } });
        string Encode(string text) => Convert.ToBase64String(GoogleDocsClipboard.WriteMap(new()
        {
            [GoogleDocsClipboard.SliceType] = new JsonObject { ["data"] = new JsonObject { ["resolved"] = new JsonObject
                { ["dsl_spacers"] = text, ["dsl_styleslices"] = styles.DeepClone() } }.ToJsonString() }.ToJsonString()
        }));
        var before = GoogleDocsClipboard.ParagraphLayout(Encode("a\nb\nc"), 3);
        Check(before is not null, "native post-paste layout comparison must resolve final partial styles");
        // Observed editor failure: level 0 becomes 1, following level 1 becomes 2.
        styles[1]!["stsl_styles"]![3]!["ls_nest"] = 1;
        styles[1]!["stsl_trailing"]!["ls_nest"] = 2;
        Check(GoogleDocsClipboard.ParagraphLayout(Encode("a\nb\nc"), 3) != before,
            "editor native recopy must detect contextual list rebasing even when Unicode text is correct");
        Check(GoogleDocsClipboard.ParagraphLayout(Encode("a\nb\nc"), 2) is null,
            "a layout signature must not silently omit a final paragraph");
        styles.Clear();
        Check(GoogleDocsClipboard.ParagraphLayout(Encode("a\nb\nc"), 3) is null,
            "empty style arrays cannot falsely verify editor layout");
        Check(GoogleDocsClipboard.ParagraphLayout("AAAA", 3) is null,
            "missing/corrupt post-paste layout cannot be reported as verified");
    }
}
