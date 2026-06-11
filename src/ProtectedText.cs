using System.Text.RegularExpressions;

namespace UniversalSpellCheck;

internal static partial class ProtectedText
{
    private const string PlaceholderStem = "__USC_LITERAL_";

    [GeneratedRegex(
        """
        (?<url>https?://[^\s"'<>]+)
        |
        (?<api_key>
            \b(?:sk|pk|rk)-(?:proj-)?[A-Za-z0-9_-]{16,}\b
            |
            \bgithub_pat_[A-Za-z0-9_]{20,}\b
            |
            \bgh[pousr]_[A-Za-z0-9]{20,}\b
            |
            \bxox[baprs]-[A-Za-z0-9-]{10,}\b
            |
            \bAKIA[A-Z0-9]{16}\b
            |
            \bAIza[A-Za-z0-9_-]{20,}\b
            |
            \beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b
            |
            (?i:\b(?:api[_-]?key|access[_-]?token|auth[_-]?token|client[_-]?secret|session[_-]?id)\b)
            \s*[:=]\s*
            ["']?[A-Za-z0-9_./+=-]{12,}["']?
        )
        |
        (?<uuid>\b[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[1-8][0-9A-Fa-f]{3}-[89ABab][0-9A-Fa-f]{3}-[0-9A-Fa-f]{12}\b)
        |
        (?<file_path>
            ["']
            (?:
                [A-Za-z]:\\
                |
                \\\\[^\\\s]+\\
                |
                (?:~|\.\.?)[/\\]
                |
                /
                |
                [A-Za-z0-9_.-]+[/\\]
            )
            [^"'<>|:*?]+
            ["']
            |
            (?<![\w:/\\])
            (?:
                [A-Za-z]:\\
                |
                \\\\[^\\\s]+\\
                |
                (?:~|\.\.?)[/\\]
                |
                /
                |
                [A-Za-z0-9_.-]+[/\\]
            )
            (?:[^\s"'<>|:*?]+[/\\])*[^\s"'<>|:*?]*[A-Za-z0-9_.-]
        )
        |
        (?<opaque_id>\b(?=[A-Za-z0-9_-]{20,}\b)(?=[A-Za-z0-9_-]*[A-Za-z])(?=[A-Za-z0-9_-]*\d)[A-Za-z0-9_-]+\b)
        """,
        RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex LiteralRegex();

    public static ProtectionResult Protect(string text)
    {
        var entries = new List<ProtectedLiteral>();
        var namespaceId = 0;
        string placeholderPrefix;

        do
        {
            placeholderPrefix = $"{PlaceholderStem}{namespaceId}_";
            namespaceId++;
        }
        while (text.Contains(placeholderPrefix, StringComparison.Ordinal));

        var protectedText = LiteralRegex().Replace(text, match =>
        {
            var kind = match.Groups["url"].Success ? ProtectedLiteralKind.Url
                : match.Groups["api_key"].Success ? ProtectedLiteralKind.ApiKey
                : match.Groups["uuid"].Success ? ProtectedLiteralKind.Uuid
                : match.Groups["file_path"].Success ? ProtectedLiteralKind.FilePath
                : ProtectedLiteralKind.OpaqueId;

            var placeholder = $"{placeholderPrefix}{entries.Count + 1}__";
            entries.Add(new ProtectedLiteral(placeholder, match.Value, kind));
            return placeholder;
        });

        return new ProtectionResult(protectedText, entries);
    }

    public static RestoreResult Restore(string text, ProtectionResult protection)
    {
        foreach (var entry in protection.Entries)
        {
            var first = text.IndexOf(entry.Placeholder, StringComparison.Ordinal);
            if (first < 0
                || text.IndexOf(entry.Placeholder, first + entry.Placeholder.Length, StringComparison.Ordinal) >= 0)
            {
                return new RestoreResult(text, false, entry.Placeholder);
            }

            text = text.Replace(entry.Placeholder, entry.Value, StringComparison.Ordinal);
        }

        return new RestoreResult(text, true, null);
    }
}

internal enum ProtectedLiteralKind
{
    Url,
    ApiKey,
    Uuid,
    FilePath,
    OpaqueId
}

internal sealed record ProtectedLiteral(
    string Placeholder,
    string Value,
    ProtectedLiteralKind Kind);

internal sealed record ProtectionResult(
    string Text,
    IReadOnlyList<ProtectedLiteral> Entries)
{
    public static ProtectionResult Empty(string text) => new(text, Array.Empty<ProtectedLiteral>());

    public int Count(ProtectedLiteralKind kind) => Entries.Count(entry => entry.Kind == kind);
}

internal sealed record RestoreResult(
    string Text,
    bool Success,
    string? InvalidPlaceholder);
