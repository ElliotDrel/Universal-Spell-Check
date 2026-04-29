using System.Text.Json;
using System.Text.RegularExpressions;

namespace UniversalSpellCheck;

internal sealed class TextPostProcessor
{
    private static readonly Regex UrlRegex = new(@"https?://\S+", RegexOptions.Compiled);
    private static readonly string LeakedPromptLine =
        "instructions: " + OpenAiSpellcheckService.PromptInstruction;

    private readonly DiagnosticsLogger _logger;
    private readonly object _reloadLock = new();
    private volatile IReadOnlyList<ReplacementPair> _pairs = Array.Empty<ReplacementPair>();
    private DateTime _lastModifiedUtc = DateTime.MinValue;
    private long _lastFileSize = -1;

    public TextPostProcessor(DiagnosticsLogger logger)
    {
        _logger = logger;
        RefreshIfChanged();
    }

    // Hot-path: no file I/O, just snapshot + apply.
    public PostProcessResult Process(string outputText)
    {
        var pairs = _pairs;
        var replacements = ApplyReplacements(outputText, pairs);
        var promptGuard = StripPromptLeak(replacements.Text);

        return new PostProcessResult
        {
            Text = promptGuard.Text,
            ReplacementsApplied = replacements.Applied,
            UrlsProtected = replacements.UrlsProtected,
            PromptLeak = promptGuard
        };
    }

    // Off-hot-path refresh: called from FinalizeAsync after the paste lands.
    public bool RefreshIfChanged()
    {
        lock (_reloadLock)
        {
            try
            {
                if (!File.Exists(AppPaths.ReplacementsPath))
                {
                    if (_pairs.Count > 0 || _lastFileSize != -1)
                    {
                        _pairs = Array.Empty<ReplacementPair>();
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
                if (rawJson.Length > 0 && rawJson[0] == '﻿')
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

                // Atomic swap so a concurrent Process() either sees the old or new list.
                _pairs = nextPairs;
                _lastModifiedUtc = info.LastWriteTimeUtc;
                _lastFileSize = info.Length;

                _logger.Log($"replacements_reloaded count={nextPairs.Count} path=\"{Escape(AppPaths.ReplacementsPath)}\"");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Log($"replacements_reload_failed error=\"{Escape(ex.Message)}\"");
                return false;
            }
        }
    }

    private static ReplacementResult ApplyReplacements(string text, IReadOnlyList<ReplacementPair> pairs)
    {
        var urls = new List<string>();
        text = UrlRegex.Replace(text, match =>
        {
            urls.Add(match.Value);
            return $"__URL_{urls.Count}__";
        });

        var applied = new List<string>();
        foreach (var pair in pairs)
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

    private static PromptLeakResult StripPromptLeak(string text)
    {
        var beforeLength = text.Length;
        if (!text.Contains(LeakedPromptLine, StringComparison.Ordinal))
        {
            return PromptLeakResult.NotTriggered(text);
        }

        var occurrences = CountOccurrences(text, LeakedPromptLine);
        var textAfter = text.Replace(LeakedPromptLine, "", StringComparison.Ordinal).TrimStart();
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
