using System.Text.Json.Nodes;
using UniversalSpellCheck;

internal static class GoogleDocsClipboardTests
{
    internal static void Run()
    {
        const string source = "chek\nlast";
        var resolved = new JsonObject
        {
            ["dsl_spacers"] = source,
            ["dsl_styleslices"] = new JsonArray(
                new JsonObject { ["stsl_type"] = "text", ["stsl_styles"] = new JsonArray(new JsonObject { ["ts_bd"] = true }, null, null, null, null, new JsonObject { ["ts_bd"] = false }) },
                new JsonObject { ["stsl_type"] = "paragraph", ["stsl_styles"] = new JsonArray(null, null, null, null, new JsonObject { ["ps_il"] = 36 }), ["stsl_trailing"] = new JsonObject { ["ps_il"] = 72 }, ["stsl_trailingType"] = "paragraph" },
                new JsonObject { ["stsl_type"] = "list", ["stsl_styles"] = new JsonArray(null, null, null, null, new JsonObject { ["ls_nest"] = 0, ["ls_id"] = "sample-list" }), ["stsl_trailing"] = new JsonObject { ["ls_nest"] = 1, ["ls_id"] = "sample-list" } }),
            ["dsl_metastyleslices"] = new JsonArray(),
            ["dsl_suggestedinsertions"] = new JsonObject { ["sgsl_sugg"] = new JsonArray() },
            ["dsl_suggesteddeletions"] = new JsonObject { ["sgsl_sugg"] = new JsonArray() },
            ["dsl_entitypositionmap"] = new JsonObject(), ["dsl_relateddocslices"] = new JsonObject(),
            ["dsl_nestedmodelmap"] = new JsonObject(), ["dsl_drawingrevisionaccesstokenmap"] = new JsonObject(),
            ["dsl_entitymap"] = new JsonObject { ["sample-list"] = new JsonObject { ["list_id"] = "sample-list" } }
        };
        string Encode() => Convert.ToBase64String(GoogleDocsClipboard.WriteMap(new()
        {
            [GoogleDocsClipboard.SliceType] = new JsonObject { ["data"] = new JsonObject { ["resolved"] = resolved.DeepClone(), ["autotext_content"] = new JsonObject() }.ToJsonString(), ["dct"] = "kix" }.ToJsonString(),
            [GoogleDocsClipboard.ClipIdType] = "original-cache-id"
        }));
        JsonObject Decode(byte[] bytes) => JsonNode.Parse(JsonNode.Parse(GoogleDocsClipboard.ReadMap(bytes)[GoogleDocsClipboard.SliceType])!["data"]!.GetValue<string>())!["resolved"]!.AsObject();
        void Check(bool condition, string why) { if (!condition) throw new InvalidOperationException(why); }
        var updated = GoogleDocsClipboard.Rewrite(Encode(), "chek\r\nlast\r\n", "check\nlast", out var status);
        Check(status == "ok" && updated is not null, "native Docs rewrite must accept verified text");
        var output = Decode(updated!);
        Check(output["dsl_spacers"]!.GetValue<string>() == "check\nlast", "partial final paragraph must never gain a newline");
        Check(output["dsl_styleslices"]![0]!["stsl_styles"]![6]!["ts_bd"]!.GetValue<bool>() == false,
            "text style transition must follow changed text length");
        Check(output["dsl_styleslices"]![1]!["stsl_styles"]![5]!["ps_il"]!.GetValue<int>() == 36
            && output["dsl_styleslices"]![1]!["stsl_trailing"]!["ps_il"]!.GetValue<int>() == 72,
            "paragraph delimiter and partial trailing paragraph must keep their styles");
        Check(output["dsl_styleslices"]![2]!["stsl_styles"]![5]!["ls_nest"]!.GetValue<int>() == 0
            && output["dsl_styleslices"]![2]!["stsl_trailing"]!["ls_nest"]!.GetValue<int>() == 1,
            "nested list identity and final-item level must survive a correction");
        Check(!GoogleDocsClipboard.ReadMap(updated!).ContainsKey(GoogleDocsClipboard.ClipIdType), "never reuse original cached slice ID");
        var unchanged = GoogleDocsClipboard.Rewrite(Encode(), "chek\r\nlast\r\n", source, out status);
        Check(unchanged is not null && Decode(unchanged)["dsl_spacers"]!.GetValue<string>() == source,
            "unchanged text must still produce a native replacement");
        Check(GoogleDocsClipboard.Rewrite(Encode(), "different", source, out status) is null && status == "source_mismatch", "reject a different source");
        Check(GoogleDocsClipboard.Rewrite(Encode(), "chek\r\nlast\r\n", "check\nnew\nlast", out status) is null,
            "native remapping must reject invented paragraph structure");
        Check(GoogleDocsClipboard.Rewrite("AAAA", source, source, out status) is null, "reject corrupt binary");
        var unicode = GoogleDocsClipboard.Rewrite(Encode(), "chek\r\nlast\r\n", "chek \U0001F600\nlast", out status);
        Check(unicode is not null && Decode(unicode)["dsl_styleslices"]![1]!["stsl_styles"]![7]!["ps_il"]!.GetValue<int>() == 36,
            "native style offsets must count UTF-16 units for supplementary Unicode");
        resolved["dsl_spacers"] = source + "\n";
        // Captured failure: native copy also appends LF and stores the last
        // list/paragraph style on it; verified HTML has no final empty paragraph.
        foreach (var index in new[] { 1, 2 })
        {
            var style = resolved["dsl_styleslices"]![index]!;
            var styles = style["stsl_styles"]!.AsArray();
            while (styles.Count < source.Length) styles.Add(null);
            styles.Add(style["stsl_trailing"]!.DeepClone());
            style.AsObject().Remove("stsl_trailing");
            style.AsObject().Remove("stsl_trailingType");
        }
        foreach (var correction in new[] { "check\nlast", source })
        {
            var trimmed = GoogleDocsClipboard.Rewrite(Encode(), "chek\r\nlast\r\n", correction, out status);
            Check(trimmed is not null && Decode(trimmed)["dsl_spacers"]!.GetValue<string>() == correction,
                "changed and unchanged multiline replacements must remove the native copy-only LF");
            var result = Decode(trimmed!);
            Check(result["dsl_styleslices"]![1]!["stsl_trailing"]!["ps_il"]!.GetValue<int>() == 72
                && result["dsl_styleslices"]![2]!["stsl_trailing"]!["ls_nest"]!.GetValue<int>() == 1,
                "removed copy delimiter must retain final-item indentation and nesting as trailing styles");
        }
        var intentionalBreak = GoogleDocsClipboard.Rewrite(Encode(), "chek\r\nlast\r\n", "check\nlast\n", out status);
        Check(intentionalBreak is not null && Decode(intentionalBreak)["dsl_spacers"]!.GetValue<string>() == "check\nlast\n",
            "a final break retained by verified HTML must survive");
        resolved["dsl_spacers"] = "chek\n";
        foreach (var index in new[] { 1, 2 })
        {
            var style = resolved["dsl_styleslices"]![index]!;
            style["stsl_styles"]!.AsArray().RemoveAt(source.Length);
            style.AsObject().Remove("stsl_trailing");
            style.AsObject().Remove("stsl_trailingType");
        }
        foreach (var correction in new[] { "check", "chek" })
        {
            var single = GoogleDocsClipboard.Rewrite(Encode(), "chek\r\n", correction, out status);
            Check(single is not null && Decode(single)["dsl_spacers"]!.GetValue<string>() == correction,
                "single-item changed and unchanged text must paste without the native copy-only newline");
        }
        resolved["dsl_spacers"] = source;
        resolved["dsl_entitypositionmap"] = new JsonObject { ["drawing"] = 3 };
        Check(GoogleDocsClipboard.Rewrite(Encode(), "chek\r\nlast\r\n", "check\nlast", out status) is null,
            "embedded position-bearing content must not be guessed");
    }

    // Full mapper path: the Unicode paragraph model gates the native slice,
    // independently of whether the CF_HTML block shape is understood.
    internal static void RunSelections(Func<string, string> cfHtml)
    {
        void Check(bool condition, string why) { if (!condition) throw new InvalidOperationException(why); }
        JsonArray Sparse(int length, params (int Index, JsonObject Style)[] entries)
        {
            var styles = new JsonArray();
            for (var i = 0; i < length; i++) styles.Add(null);
            foreach (var (index, style) in entries) styles[index] = style;
            return styles;
        }
        // Native copy includes the final item's LF; paragraph styles sit on each LF.
        const string source = "Planing\nalpha\nbta link\ngama\ndelta\n";
        JsonObject Paragraph(int heading) => heading == 0
            ? new JsonObject { ["ps_hd"] = 0 }
            : new JsonObject { ["ps_hd"] = heading, ["ps_hdid"] = "h.sample" };
        JsonObject Item(int nest) => new() { ["ls_id"] = "sample-list", ["ls_nest"] = nest };
        var resolved = new JsonObject
        {
            ["dsl_spacers"] = source,
            ["dsl_styleslices"] = new JsonArray(
                new JsonObject { ["stsl_type"] = "text", ["stsl_styles"] = Sparse(source.Length + 1, (0, new JsonObject { ["ts_fs"] = 16 }), (8, new JsonObject { ["ts_fs"] = 11 }), (23, new JsonObject { ["ts_bd"] = true }), (27, new JsonObject { ["ts_bd"] = false })) },
                new JsonObject { ["stsl_type"] = "link", ["stsl_styles"] = Sparse(source.Length, (18, new JsonObject { ["lnks_link"] = "https://example.test" }), (22, new JsonObject { ["lnks_link"] = null })) },
                new JsonObject { ["stsl_type"] = "paragraph", ["stsl_styles"] = Sparse(source.Length, (7, Paragraph(2)), (13, Paragraph(0)), (22, Paragraph(0)), (27, Paragraph(0)), (33, Paragraph(0))) },
                new JsonObject { ["stsl_type"] = "list", ["stsl_styles"] = Sparse(source.Length, (13, Item(0)), (22, Item(1)), (27, Item(0)), (33, Item(0))) }),
            ["dsl_metastyleslices"] = new JsonArray(),
            ["dsl_suggestedinsertions"] = new JsonObject { ["sgsl_sugg"] = new JsonArray() },
            ["dsl_suggesteddeletions"] = new JsonObject { ["sgsl_sugg"] = new JsonArray() },
            ["dsl_entitypositionmap"] = new JsonObject(), ["dsl_relateddocslices"] = new JsonObject(),
            ["dsl_nestedmodelmap"] = new JsonObject(), ["dsl_drawingrevisionaccesstokenmap"] = new JsonObject(),
            ["dsl_entitymap"] = new JsonObject { ["sample-list"] = new JsonObject { ["list_id"] = "sample-list" } }
        };
        var native = Convert.ToBase64String(GoogleDocsClipboard.WriteMap(new()
        {
            [GoogleDocsClipboard.SliceType] = new JsonObject { ["data"] = new JsonObject { ["resolved"] = resolved, ["autotext_content"] = new JsonObject() }.ToJsonString(), ["dct"] = "kix" }.ToJsonString(),
            [GoogleDocsClipboard.ClipIdType] = "original-cache-id"
        }));
        JsonObject Decode(byte[] bytes) => JsonNode.Parse(JsonNode.Parse(GoogleDocsClipboard.ReadMap(bytes)[GoogleDocsClipboard.SliceType])!["data"]!.GetValue<string>())!["resolved"]!.AsObject();
        JsonNode? At(JsonObject result, int slice, int index) => result["dsl_styleslices"]![slice]!["stsl_styles"]!.AsArray() is var a && index < a.Count ? a[index] : null;
        const string copy = "Planing\r\nalpha\r\nbta link\r\ngama\r\ndelta\r\n";
        const string html = "<b style=\"font-weight:normal\" id=\"docs-internal-guid-test\"><h2 dir=\"ltr\"><span style=\"font-size:16pt\">Planing</span></h2><ul><li aria-level=\"1\"><p><span>alpha</span></p></li><li aria-level=\"2\"><p><span>bta </span><a href=\"https://example.test\"><span>link</span></a></p></li><li aria-level=\"1\"><p><span style=\"font-weight:700\">gama</span></p></li><li aria-level=\"1\"><p><span>delta</span></p></li></ul></b>";

        var changed = RichTextClipboard.TryCreateReplacement(cfHtml(html), copy, "Planning\nalpha\nbeta link\ngamma\ndelta", native);
        Check(changed.Mode == "docs_native" && changed.NativeDocsStatus == "ok" && changed.Html.Length > 0
            && changed.Text == "Planning\nalpha\nbeta link\ngamma\ndelta", "heading + list selection must reach the native slice");
        var output = Decode(changed.NativeDocsBytes!);
        Check(output["dsl_spacers"]!.GetValue<string>() == changed.Text, "native text must equal pasted Unicode with no copy-only LF");
        Check(At(output, 2, 8)?["ps_hdid"]?.GetValue<string>() == "h.sample" && At(output, 2, 8)?["ps_hd"]?.GetValue<int>() == 2,
            "heading level and heading ID must stay on the heading's paragraph delimiter");
        Check(At(output, 3, 24)?["ls_nest"]?.GetValue<int>() == 1 && At(output, 3, 30)?["ls_nest"]?.GetValue<int>() == 0,
            "list nesting must follow the remapped item delimiters");
        Check(output["dsl_styleslices"]![2]!["stsl_trailing"]?["ps_hd"]?.GetValue<int>() == 0
            && output["dsl_styleslices"]![3]!["stsl_trailing"]?["ls_id"]?.GetValue<string>() == "sample-list",
            "removed copy-only LF must hand the final item's paragraph/list style to stsl_trailing");
        Check(At(output, 1, 20)?["lnks_link"]?.GetValue<string>() == "https://example.test" && At(output, 0, 25)?["ts_bd"]?.GetValue<bool>() == true
            && At(output, 0, 9)?["ts_fs"]?.GetValue<int>() == 11,
            "link, bold and heading font transitions must follow the corrected text");

        var unchanged = RichTextClipboard.TryCreateReplacement(cfHtml(html), copy, "Planing\nalpha\nbta link\ngama\ndelta\n", native);
        Check(unchanged.Mode == "docs_native" && Decode(unchanged.NativeDocsBytes!)["dsl_spacers"]!.GetValue<string>() == source[..^1]
            && At(Decode(unchanged.NativeDocsBytes!), 2, 7)?["ps_hdid"]?.GetValue<string>() == "h.sample",
            "unchanged heading + list text must still write a native replacement");

        // The same native terminal LF can be a copy separator for one heading
        // or a real break before a final blank paragraph. Verified HTML decides.
        var singleResolved = resolved.DeepClone().AsObject();
        singleResolved["dsl_spacers"] = "Planing\n";
        foreach (var style in singleResolved["dsl_styleslices"]!.AsArray())
        {
            var styles = style!["stsl_styles"]!.AsArray();
            while (styles.Count > 8) styles.RemoveAt(styles.Count - 1);
        }
        string EncodeSingle() => Convert.ToBase64String(GoogleDocsClipboard.WriteMap(new()
        {
            [GoogleDocsClipboard.SliceType] = new JsonObject { ["data"] = new JsonObject { ["resolved"] = singleResolved.DeepClone(), ["autotext_content"] = new JsonObject() }.ToJsonString() }.ToJsonString()
        }));
        const string singleHeadingHtml = "<b id=\"docs-internal-guid-test\"><h2><span>Planing</span></h2></b>";
        var wholeHeading = RichTextClipboard.TryCreateReplacement(cfHtml(singleHeadingHtml), "Planing\r\n", "Planning", EncodeSingle());
        Check(wholeHeading.Mode == "docs_native" && wholeHeading.Text == "Planning"
            && Decode(wholeHeading.NativeDocsBytes!)["dsl_styleslices"]![2]!["stsl_trailing"]?["ps_hd"]?.GetValue<int>() == 2
            && Decode(wholeHeading.NativeDocsBytes!)["dsl_styleslices"]![2]!["stsl_trailing"]?["ps_hdid"]?.GetValue<string>() == "h.sample",
            "a single heading's copied delimiter must become trailing heading metadata, without a newline");
        singleResolved["dsl_styleslices"]![2]!["stsl_trailing"] = Paragraph(0);
        singleResolved["dsl_styleslices"]![2]!["stsl_trailingType"] = "paragraph";
        var finalBlankHtml = singleHeadingHtml.Replace("</b>", "<p><br></p></b>");
        foreach (var blankCopy in new[] { "Planing\r\n", "Planing\r\n\r\n" })
        {
            var finalBlank = RichTextClipboard.TryCreateReplacement(cfHtml(finalBlankHtml), blankCopy, "Planning\n", EncodeSingle());
            var finalNative = Decode(finalBlank.NativeDocsBytes!);
            Check(finalBlank.Mode == "docs_native" && finalBlank.Text == "Planning\n"
                && finalNative["dsl_spacers"]!.GetValue<string>() == finalBlank.Text
                && At(finalNative, 2, 8)?["ps_hd"]?.GetValue<int>() == 2
                && finalNative["dsl_styleslices"]![2]!["stsl_trailing"]?["ps_hd"]?.GetValue<int>() == 0,
                "intentional final blank and preceding heading style must survive native paste with or without the extra copied separator");
        }

        // A block shape the HTML serializer does not model must not block a
        // verified native slice; Unicode and native are still written.
        var unknownBlock = html.Replace("<h2 dir=\"ltr\">", "<div>").Replace("</h2>", "</div>");
        var nativeOnly = RichTextClipboard.TryCreateReplacement(cfHtml(unknownBlock), copy, "Planning\nalpha\nbeta link\ngamma\ndelta", native);
        Check(nativeOnly.Applied && nativeOnly.Mode == "docs_native" && nativeOnly.Html.Length == 0
            && Decode(nativeOnly.NativeDocsBytes!)["dsl_spacers"]!.GetValue<string>() == nativeOnly.Text,
            "verified native slice must be used even when CF_HTML cannot be verified");
        var neither = RichTextClipboard.TryCreateReplacement(cfHtml(unknownBlock), copy, "Planning\nalpha\nbeta link\ngamma\ndelta\n");
        Check(!neither.Applied && neither.Text == "Planning\nalpha\nbeta link\ngamma\ndelta",
            "plain fallback must not paste the copy-only terminal separator");
        Check(RichTextClipboard.TryCreateReplacement(cfHtml(html), copy, "Planning\nalpha\nbeta\nlink\ngamma\ndelta", native) is { Applied: false, NativeDocsBytes: null },
            "neither flavor may accept a changed paragraph count");
    }
}
