using System.Text.Json;
using System.Text.RegularExpressions;

namespace UniversalSpellCheck;

internal sealed class TextPostProcessor
{
    private static readonly Regex UrlRegex = new(@"https?://\S+", RegexOptions.Compiled);

    private readonly DiagnosticsLogger _logger;
    private readonly List<ReplacementPair> _pairs = [];
    private DateTime _lastModifiedUtc = DateTime.MinValue;
    private long _lastFileSize = -1;

    public TextPostProcessor(DiagnosticsLogger logger)
    {
        _logger = logger;
        ReloadIfChanged();
    }

    public PostProcessResult Process(string outputText, string promptInstruction)
    {
        var replacementsReloaded = ReloadIfChanged();
        var replacements = ApplyReplacements(outputText);
        var promptGuard = StripPromptLeak(replacements.Text, promptInstruction);

        return new PostProcessResult
        {
            Text = promptGuard.Text,
            ReplacementsReloaded = replacementsReloaded,
            ReplacementsApplied = replacements.Applied,
            UrlsProtected = replacements.UrlsProtected,
            PromptLeak = promptGuard
        };
    }

    private bool ReloadIfChanged()
    {
        try
        {
            if (!File.Exists(AppPaths.ReplacementsPath))
            {
                if (_pairs.Count > 0 || _lastFileSize != -1)
                {
                    _pairs.Clear();
                    _lastModifiedUtc = DateTime.MinValue;
                    _lastFileSize = -1;
                    _logger.Log("replacements_missing cache_cleared=true");
                }

                return false;
            }

            var info = new FileInfo(AppPaths.ReplacementsPath);
            if (info.LastWriteTimeUtc == _lastModifiedUtc && info.Length == _lastFileSize)
            {
                return false;
            }

            var rawJson = File.ReadAllText(AppPaths.ReplacementsPath);
            if (rawJson.Length > 0 && rawJson[0] == '\uFEFF')
            {
                rawJson = rawJson[1..];
            }

            var parsed = JsonSerializer.Deserialize<Dictionary<string, string[]>>(rawJson)
                ?? new Dictionary<string, string[]>();

            var nextPairs = new List<ReplacementPair>();
            foreach (var (canonical, variants) in parsed)
            {
                foreach (var variant in variants)
                {
                    if (!string.IsNullOrEmpty(variant)
                        && !string.Equals(variant, canonical, StringComparison.Ordinal))
                    {
                        nextPairs.Add(new ReplacementPair(variant, canonical));
                    }
                }
            }

            nextPairs.Sort((a, b) => b.Variant.Length.CompareTo(a.Variant.Length));

            _pairs.Clear();
            _pairs.AddRange(nextPairs);
            _lastModifiedUtc = info.LastWriteTimeUtc;
            _lastFileSize = info.Length;

            _logger.Log($"replacements_reloaded count={_pairs.Count} path=\"{Escape(AppPaths.ReplacementsPath)}\"");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Log($"replacements_reload_failed error=\"{Escape(ex.Message)}\"");
            return false;
        }
    }

    private ReplacementResult ApplyReplacements(string text)
    {
        var urls = new List<string>();
        text = UrlRegex.Replace(text, match =>
        {
            urls.Add(match.Value);
            return $"__URL_{urls.Count}__";
        });

        var applied = new List<string>();
        foreach (var pair in _pairs)
        {
            if (!text.Contains(pair.Variant, StringComparison.Ordinal))
            {
                continue;
            }

            var before = text;
            text = text.Replace(pair.Variant, pair.Canonical, StringComparison.Ordinal);
            if (!string.Equals(before, text, StringComparison.Ordinal))
            {
                applied.Add($"{pair.Variant} -> {pair.Canonical}");
            }
        }

        for (var i = urls.Count; i >= 1; i--)
        {
            text = text.Replace($"__URL_{i}__", urls[i - 1], StringComparison.Ordinal);
        }

        return new ReplacementResult(text, applied, urls.Count);
    }

    private static PromptLeakResult StripPromptLeak(string text, string promptInstruction)
    {
        var beforeLength = text.Length;
        if (string.IsNullOrEmpty(promptInstruction))
        {
            return PromptLeakResult.NotTriggered(text);
        }

        var leakedPromptLine = $"instructions: {promptInstruction}";
        if (!text.Contains(leakedPromptLine, StringComparison.Ordinal))
        {
            return PromptLeakResult.NotTriggered(text);
        }

        var occurrences = CountOccurrences(text, leakedPromptLine);
        var textAfter = text.Replace(leakedPromptLine, "", StringComparison.Ordinal).TrimStart();
        var textInputRemoved = false;

        const string textInputLabel = "text input:";
        if (textAfter.StartsWith(textInputLabel, StringComparison.Ordinal))
        {
            textAfter = textAfter[textInputLabel.Length..].TrimStart();
            textInputRemoved = true;
        }

        return new PromptLeakResult
        {
            Triggered = true,
            Occurrences = occurrences,
            TextInputRemoved = textInputRemoved,
            RemovedChars = beforeLength - textAfter.Length,
            BeforeLength = beforeLength,
            AfterLength = textAfter.Length,
            Text = textAfter
        };
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string Escape(string? value)
    {
        return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private sealed record ReplacementPair(string Variant, string Canonical);
    private sealed record ReplacementResult(string Text, List<string> Applied, int UrlsProtected);
}

internal sealed class PostProcessResult
{
    public string Text { get; init; } = "";
    public bool ReplacementsReloaded { get; init; }
    public List<string> ReplacementsApplied { get; init; } = [];
    public int UrlsProtected { get; init; }
    public PromptLeakResult PromptLeak { get; init; } = PromptLeakResult.NotTriggered("");
}

internal sealed class PromptLeakResult
{
    public bool Triggered { get; init; }
    public int Occurrences { get; init; }
    public bool TextInputRemoved { get; init; }
    public int RemovedChars { get; init; }
    public int BeforeLength { get; init; }
    public int AfterLength { get; init; }
    public string Text { get; init; } = "";

    public static PromptLeakResult NotTriggered(string text) => new()
    {
        Triggered = false,
        BeforeLength = text.Length,
        AfterLength = text.Length,
        Text = text
    };
}
