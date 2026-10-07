using System.Reflection;
using System.Text.Json;
using UniversalSpellCheck;

internal static class FormattingReplay
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static int Run(string[] args)
    {
        try
        {
            string input;
            if (args is ["--replay-stdin"])
                input = Console.In.ReadToEnd();
            else if (args is ["--replay-file", var path])
                input = File.ReadAllText(path);
            else
                throw new InvalidDataException("Use --replay-stdin or --replay-file PATH.");
            using var document = JsonDocument.Parse(input);
            var output = Replay(document.RootElement);
            Console.WriteLine(JsonSerializer.Serialize(output, JsonOptions));
            return output.expected_matches is false ? 1 : 0;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { error = ex.Message }));
            return 2;
        }
    }

    public static ReplayOutput Replay(JsonElement root,
        Func<string, string, string, RichTextReplacementResult>? mapper = null)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Replay input must be one JSON object.");
        if (root.TryGetProperty("kind", out var kind)
            && (kind.ValueKind != JsonValueKind.String || kind.GetString() != "formatting_replay_case"
                || !root.TryGetProperty("schema_version", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var schemaVersion) || schemaVersion != 1))
            throw new InvalidDataException("Unsupported replay case schema.");
        if (!root.TryGetProperty("detail", out var detail) || detail.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Expected one read-logs JSON row or saved replay case.");
        var sourceHtml = RequiredText(detail, "clipboard_html", "clipboard_html_chars");
        var formats = detail.TryGetProperty("clipboard_formats", out var formatValue)
            && formatValue.ValueKind == JsonValueKind.String ? formatValue.GetString() ?? "" : "";
        if (sourceHtml.Length == 0 && formats.Contains("HTML Format", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Source HTML was offered but its empty capture is unconfirmed.");
        var sourceText = RequiredText(detail, "input_text", "input_chars");
        var correctedText = RequiredText(detail, "output_text", "output_chars");
        RichTextReplacementResult replacement;
        string? mapperExceptionType = null;
        try
        {
            replacement = (mapper ?? RichTextClipboard.TryCreateReplacement)(sourceHtml, sourceText, correctedText);
        }
        catch (Exception ex)
        {
            replacement = RichTextReplacementResult.NotApplied("rich_text_failed", correctedText);
            mapperExceptionType = ex.GetType().Name;
        }
        var current = new ReplayResult(replacement.Text, replacement.Html, replacement.Mode, replacement.Reason,
            replacement.Attempted, replacement.Applied, replacement.ParagraphCount);
        bool? recordedMatches = null;
        if (OptionalComplete(detail, "paste_text", out var recordedText)
            && OptionalComplete(detail, "paste_html", out var recordedHtml))
            recordedMatches = current.text == recordedText && current.html == recordedHtml;
        bool? expectedMatches = null;
        if (root.TryGetProperty("expected", out var expected))
        {
            var expectedText = RequiredText(expected, "text");
            var expectedHtml = RequiredText(expected, "html");
            var expectedMode = RequiredText(expected, "mode");
            var expectedReason = RequiredText(expected, "reason");
            expectedMatches = current.text == expectedText && current.html == expectedHtml
                && current.mode == expectedMode && current.reason == expectedReason;
        }
        var source = root.TryGetProperty("source", out var savedSource) ? savedSource.Clone()
            : JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>(
                root.EnumerateObject().Where(p => p.Name is "ts" or "channel" or "version" or "pid")
                    .Select(p => new KeyValuePair<string, JsonElement>(p.Name, p.Value.Clone()))));
        var warnings = new List<string>
        {
            "Replays the rich-text mapper using recorded post-processed output; no API, clipboard write, or destination paste.",
            "RTF and destination rendering are not replayed. Retest the updated app in the original editor."
        };
        if (mapperExceptionType is not null)
            warnings.Add($"Mapper threw {mapperExceptionType}; reproduced production rich_text_failed plain-text fallback.");
        if (sourceHtml.Length == 0 && formats.Length == 0)
            warnings.Add("Source HTML availability is unknown because clipboard formats were not captured.");
        if (recordedMatches is null)
            warnings.Add("Complete recorded paste payloads are unavailable; historical comparison was skipped.");
        if (detail.TryGetProperty("developer_evidence", out _))
            warnings.Add("Inline log replay only. Use read-logs --save-replay-case to load full developer evidence.");
        return new ReplayOutput(1, "rich_text_mapper", BuildChannel.AppVersion,
            typeof(RichTextClipboard).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            source, current, recordedMatches, expectedMatches, mapperExceptionType, warnings);
    }

    private static string RequiredText(JsonElement detail, string field, string? countField = null)
    {
        if (detail.ValueKind != JsonValueKind.Object
            || !detail.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Missing replay evidence: {field}.");
        var text = value.GetString() ?? "";
        if (detail.TryGetProperty(field + "_truncated", out var truncated) && truncated.ValueKind != JsonValueKind.False
            || countField is not null && detail.TryGetProperty(countField, out var count)
                && (count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out var chars) || chars != text.Length))
            throw new InvalidDataException($"Incomplete or truncated replay evidence: {field}. Export full developer evidence if available.");
        return text;
    }

    private static bool OptionalComplete(JsonElement detail, string field, out string? text)
    {
        text = null;
        if (!detail.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String)
            return false;
        try { text = RequiredText(detail, field, field + "_chars"); return true; }
        catch (InvalidDataException) { return false; }
    }
}

internal sealed record ReplayResult(string text, string html, string mode, string reason,
    bool attempted, bool applied, int paragraph_count);
internal sealed record ReplayOutput(int schema_version, string scope, string current_app_version,
    string? current_build, JsonElement source, ReplayResult result, bool? recorded_matches,
    bool? expected_matches, string? mapper_exception_type, List<string> warnings);
