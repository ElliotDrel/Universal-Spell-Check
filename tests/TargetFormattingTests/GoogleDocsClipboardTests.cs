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
}
