namespace UniversalSpellCheck.UI;

internal enum TextDiffKind
{
    Equal,
    Insert,
    Delete
}

internal readonly record struct TextDiffSegment(string Text, TextDiffKind Kind);

internal enum LineDiffKind
{
    Unchanged,
    Modified,
    Added,
    Removed
}

internal readonly record struct LineDiff(LineDiffKind Kind, string OldLine, string NewLine);

/// <summary>
/// Character- and line-level diff for activity feed inline rendering.
/// </summary>
internal static class InlineTextDiff
{
    // LCS allocates and fills an n*m matrix. Bound it so one unusually large
    // spellcheck record cannot monopolize the dashboard dispatcher.
    private const long MaxDiffMatrixCells = 1_000_000;

    internal static IReadOnlyList<TextDiffSegment> ComputeChars(string oldText, string newText)
    {
        if (oldText == newText)
            return oldText.Length == 0 ? Array.Empty<TextDiffSegment>() : [new TextDiffSegment(oldText, TextDiffKind.Equal)];

        if ((long)oldText.Length * newText.Length > MaxDiffMatrixCells)
        {
            return
            [
                new TextDiffSegment(oldText, TextDiffKind.Delete),
                new TextDiffSegment(newText, TextDiffKind.Insert)
            ];
        }

        var oldChars = oldText.ToCharArray();
        var newChars = newText.ToCharArray();
        return ComputeSequenceDiff(oldChars, newChars, static c => c.ToString());
    }

    internal static IReadOnlyList<LineDiff> AlignLines(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines)
    {
        if ((long)oldLines.Count * newLines.Count > MaxDiffMatrixCells)
        {
            return oldLines.Select(line => new LineDiff(LineDiffKind.Removed, line, ""))
                .Concat(newLines.Select(line => new LineDiff(LineDiffKind.Added, "", line)))
                .ToArray();
        }

        var segments = ComputeSequenceDiff(oldLines.ToArray(), newLines.ToArray(), static s => s);
        var lines = new List<LineDiff>();

        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            switch (segment.Kind)
            {
                case TextDiffKind.Equal:
                    lines.Add(new LineDiff(LineDiffKind.Unchanged, segment.Text, segment.Text));
                    break;
                case TextDiffKind.Delete:
                    if (i + 1 < segments.Count && segments[i + 1].Kind == TextDiffKind.Insert)
                    {
                        lines.Add(new LineDiff(LineDiffKind.Modified, segment.Text, segments[i + 1].Text));
                        i++;
                    }
                    else
                    {
                        lines.Add(new LineDiff(LineDiffKind.Removed, segment.Text, ""));
                    }
                    break;
                case TextDiffKind.Insert:
                    lines.Add(new LineDiff(LineDiffKind.Added, "", segment.Text));
                    break;
            }
        }

        return lines;
    }

    private static List<TextDiffSegment> ComputeSequenceDiff<T>(
        T[] oldItems,
        T[] newItems,
        Func<T, string> toText) where T : notnull
    {
        var n = oldItems.Length;
        var m = newItems.Length;
        var lcs = new int[n + 1, m + 1];

        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                lcs[i, j] = EqualityComparer<T>.Default.Equals(oldItems[i - 1], newItems[j - 1])
                    ? lcs[i - 1, j - 1] + 1
                    : Math.Max(lcs[i - 1, j], lcs[i, j - 1]);
            }
        }

        var raw = new List<TextDiffSegment>();
        var iIdx = n;
        var jIdx = m;

        while (iIdx > 0 || jIdx > 0)
        {
            if (iIdx > 0 && jIdx > 0 && EqualityComparer<T>.Default.Equals(oldItems[iIdx - 1], newItems[jIdx - 1]))
            {
                raw.Add(new TextDiffSegment(toText(oldItems[iIdx - 1]), TextDiffKind.Equal));
                iIdx--;
                jIdx--;
            }
            else if (jIdx > 0 && (iIdx == 0 || lcs[iIdx, jIdx - 1] >= lcs[iIdx - 1, jIdx]))
            {
                raw.Add(new TextDiffSegment(toText(newItems[jIdx - 1]), TextDiffKind.Insert));
                jIdx--;
            }
            else
            {
                raw.Add(new TextDiffSegment(toText(oldItems[iIdx - 1]), TextDiffKind.Delete));
                iIdx--;
            }
        }

        raw.Reverse();
        return MergeAdjacent(raw);
    }

    private static List<TextDiffSegment> MergeAdjacent(IReadOnlyList<TextDiffSegment> segments)
    {
        if (segments.Count == 0)
            return [];

        var merged = new List<TextDiffSegment> { segments[0] };
        for (var i = 1; i < segments.Count; i++)
        {
            var current = segments[i];
            var last = merged[^1];
            if (last.Kind == current.Kind)
                merged[^1] = new TextDiffSegment(last.Text + current.Text, last.Kind);
            else
                merged.Add(current);
        }

        return merged;
    }
}
