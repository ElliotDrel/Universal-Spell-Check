using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UniversalSpellCheck;

// Native paragraph/list metadata preserves indentation. The verified HTML
// text determines boundaries: Docs can append a copy-only LF to its slice too.
internal static class GoogleDocsClipboard
{
    internal const string SliceType = "application/x-vnd.google-docs-document-slice-clip+wrapped";
    internal const string ClipIdType = "application/x-vnd.google-docs-internal-clip-id";
    internal const string WindowsFormat = "Chromium Web Custom MIME Data Format";

    internal static byte[]? Rewrite(string base64, string copiedText, string replacementText, out string status)
    {
        status = "absent";
        if (base64.Length == 0) return null;
        try
        {
            var map = ReadMap(Convert.FromBase64String(base64));
            if (!map.TryGetValue(SliceType, out var wrapped)) { status = "unsupported_mime"; return null; }
            var wrapper = JsonNode.Parse(wrapped)!.AsObject();
            var slice = JsonNode.Parse(wrapper["data"]!.GetValue<string>())!.AsObject();
            var resolved = slice["resolved"]!.AsObject();
            var source = resolved["dsl_spacers"]!.GetValue<string>();
            var copied = copiedText.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (source != copied && source + "\n" != copied)
                { status = "source_mismatch"; return null; }
            var target = replacementText;
            // Use the mapper's verified paragraph structure, never restore a
            // separator just because the native copy contains it.
            var trimCopyBreak = source.EndsWith('\n')
                && source.Count(c => c == '\n') == target.Count(c => c == '\n') + 1;
            var alignmentSource = trimCopyBreak ? source[..^1] : source;
            if (alignmentSource.Count(c => c == '\n') != target.Count(c => c == '\n'))
                { status = "paragraph_mismatch"; return null; }
            // Position-bearing embedded content needs its own verified mapping.
            foreach (var key in new[] { "dsl_entitypositionmap", "dsl_relateddocslices", "dsl_nestedmodelmap", "dsl_drawingrevisionaccesstokenmap" })
                if (resolved[key] is not JsonObject obj || obj.Count != 0)
                    { status = "unsupported_embedded_content"; return null; }
            foreach (var key in new[] { "dsl_suggestedinsertions", "dsl_suggesteddeletions" })
                if (resolved[key]?["sgsl_sugg"] is not JsonArray suggestions || suggestions.Count != 0)
                    { status = "unsupported_suggestions"; return null; }
            if (slice["autotext_content"] is not JsonObject autotext || autotext.Count != 0)
                { status = "unsupported_autotext"; return null; }
            var positions = MapPositions(alignmentSource, target);
            if (positions is null) { status = "paragraph_edit"; return null; }
            foreach (var key in new[] { "dsl_styleslices", "dsl_metastyleslices" })
            {
                if (resolved[key] is not JsonArray styles) { status = "unsupported_styles"; return null; }
                foreach (var style in styles)
                {
                    var old = style!["stsl_styles"]!.AsArray();
                    var mapped = new JsonArray();
                    for (var i = 0; i < old.Count; i++)
                    {
                        if (old[i] is null) continue;
                        if (i > source.Length) { status = "invalid_style_offset"; return null; }
                        if (trimCopyBreak && i == source.Length - 1
                            && style["stsl_type"]!.GetValue<string>() is "paragraph" or "list")
                        {
                            // A removed delimiter's style belongs to the final
                            // partial paragraph, as in Docs' own partial slices.
                            style["stsl_trailing"] = old[i]!.DeepClone();
                            style["stsl_trailingType"] = style["stsl_type"]!.DeepClone();
                            continue;
                        }
                        var position = positions[Math.Min(i, alignmentSource.Length)];
                        while (mapped.Count <= position) mapped.Add(null);
                        mapped[position] = old[i]!.DeepClone();
                    }
                    style["stsl_styles"] = mapped;
                }
            }
            resolved["dsl_spacers"] = target;
            wrapper["data"] = slice.ToJsonString();
            map[SliceType] = wrapper.ToJsonString();
            // Never point the replacement back at the source's cached slice.
            map.Remove(ClipIdType);
            status = "ok";
            return WriteMap(map);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException or NullReferenceException or InvalidDataException or EndOfStreamException or ArgumentException)
        { status = "invalid_native_data"; return null; }
    }

    private static int[]? MapPositions(string source, string target)
    {
        var positions = new int[source.Length + 1];
        var from = 0;
        var to = 0;
        foreach (var edit in RichTextClipboard.ComputeAlignmentDiff(source, target))
        {
            if (edit.Kind != UI.TextDiffKind.Equal && edit.Text.Contains('\n')) return null;
            if (edit.Kind == UI.TextDiffKind.Insert) { to += edit.Text.Length; continue; }
            for (var i = 0; i < edit.Text.Length; i++)
                positions[from++] = edit.Kind == UI.TextDiffKind.Equal ? to++ : to;
        }
        if (from != source.Length || to != target.Length) return null;
        positions[0] = 0;
        positions[^1] = target.Length;
        return positions;
    }

    // Chromium custom_data_helper writes a Pickle header, entry count, then
    // UTF-16 key/value pairs, each padded to a four-byte boundary.
    internal static Dictionary<string, string> ReadMap(byte[] bytes)
    {
        if (bytes.Length < 8 || bytes.Length > 1024 * 1024) throw new InvalidDataException();
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt32() != bytes.Length - 4) throw new InvalidDataException();
        var count = reader.ReadUInt32();
        if (count > 64) throw new InvalidDataException();
        string ReadString()
        {
            var chars = reader.ReadInt32();
            if (chars < 0 || chars > (stream.Length - stream.Position) / 2) throw new InvalidDataException();
            var text = Encoding.Unicode.GetString(reader.ReadBytes(chars * 2));
            stream.Position = (stream.Position + 3) & ~3L;
            if (stream.Position > stream.Length) throw new InvalidDataException();
            return text;
        }
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
            if (!result.TryAdd(ReadString(), ReadString())) throw new InvalidDataException();
        if (stream.Position != stream.Length) throw new InvalidDataException();
        return result;
    }

    internal static byte[] WriteMap(Dictionary<string, string> map)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0);
        writer.Write(map.Count);
        foreach (var pair in map)
            foreach (var text in new[] { pair.Key, pair.Value })
            {
                writer.Write(text.Length);
                writer.Write(Encoding.Unicode.GetBytes(text));
                while ((stream.Position & 3) != 0) writer.Write((byte)0);
            }
        if (stream.Length > 1024 * 1024) throw new InvalidDataException();
        stream.Position = 0;
        writer.Write((int)stream.Length - 4);
        return stream.ToArray();
    }
}
