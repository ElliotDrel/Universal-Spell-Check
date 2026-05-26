using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using WpfBrush = System.Windows.Media.Brush;
using WpfFontFamily = System.Windows.Media.FontFamily;

namespace UniversalSpellCheck.UI.Pages;

internal partial class ActivityPage : Page
{
    private int _weekOffset = 0;

    public ActivityPage()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshActivity();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshActivity();

    private void RefreshActivity()
    {
        _weekOffset = 0;

        while (DiffStack.Children.Count > 1)
            DiffStack.Children.RemoveAt(1);

        AppendWeek();

        var (checks, corrections) = NativeActivityLogReader.ReadAllTimeStats();
        StatChecks.Text = checks.ToString("N0");
        StatCorrections.Text = corrections.ToString("N0");
        StatAccuracy.Text = checks == 0 ? "—" : $"{Math.Round((double)corrections / checks * 100)}%";
        StatStreak.Text = checks == 0 ? "0" : "1";
    }

    private void AppendWeek()
    {
        var today = DateTime.Now.Date;
        var weekFrom = today.AddDays(-(7 * _weekOffset + 6));
        var weekTo = today.AddDays(-7 * _weekOffset);

        var entries = NativeActivityLogReader.ReadDateRange(weekFrom, weekTo).ToList();

        if (_weekOffset == 0)
            EmptyState.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var entry in entries)
            DiffStack.Children.Add(CreateDiffRow(entry));

        // Cumulative range label (updates to cover all loaded weeks)
        var cumulativeFrom = today.AddDays(-(7 * _weekOffset + 6));
        FeedRangeLabel.Text = $"{cumulativeFrom:MMM d} – {today:MMM d}";

        DiffStack.Children.Add(CreateLoadEarlierButton());
    }

    private System.Windows.Controls.Button CreateLoadEarlierButton()
    {
        var btn = new System.Windows.Controls.Button
        {
            Style = (Style)FindResource("GhostButton"),
            Content = "Load earlier",
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        btn.Click += (_, _) =>
        {
            DiffStack.Children.Remove(btn);
            _weekOffset++;
            AppendWeek();
        };
        return btn;
    }

    private FrameworkElement CreateDiffRow(ActivityEntry entry)
    {
        var outer = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header: date/time · model · [Split] toggle (only if text changed)
        var header = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (entry.TextChanged)
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var timestamp = new TextBlock
        {
            Style = (Style)FindResource("MonoSmall"),
            Text = entry.Timestamp.ToLocalTime().ToString("MMM d, h:mm tt"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(timestamp, 0);
        header.Children.Add(timestamp);

        var model = new TextBlock
        {
            Style = (Style)FindResource("MonoSmall"),
            Text = entry.Model,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, entry.TextChanged ? 8 : 0, 0)
        };
        Grid.SetColumn(model, 1);
        header.Children.Add(model);

        // Pre-render both views; toggle via Visibility
        var inlineView = CreateInlineView(entry);
        FrameworkElement? splitView = entry.TextChanged ? CreateSplitView(entry) : null;

        if (entry.TextChanged && splitView != null)
        {
            splitView.Visibility = Visibility.Collapsed;

            var toggleBtn = new System.Windows.Controls.Button
            {
                Style = (Style)FindResource("GhostButton"),
                Content = "Split",
                Padding = new Thickness(8, 2, 8, 2),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(toggleBtn, 2);
            header.Children.Add(toggleBtn);

            var showingSplit = false;
            toggleBtn.Click += (_, _) =>
            {
                showingSplit = !showingSplit;
                inlineView.Visibility = showingSplit ? Visibility.Collapsed : Visibility.Visible;
                splitView.Visibility = showingSplit ? Visibility.Visible : Visibility.Collapsed;
                toggleBtn.Content = showingSplit ? "Inline" : "Split";
            };
        }

        Grid.SetRow(header, 0);
        outer.Children.Add(header);

        var contentStack = new StackPanel();
        contentStack.Children.Add(inlineView);
        if (splitView != null)
            contentStack.Children.Add(splitView);

        Grid.SetRow(contentStack, 1);
        outer.Children.Add(contentStack);

        return outer;
    }

    // ~250 chars ≈ 3 lines of JetBrains Mono 12px in a typical card width
    private const int TruncateAt = 250;

    private FrameworkElement CreateInlineView(ActivityEntry entry)
    {
        var tb = new TextBlock
        {
            FontFamily = (WpfFontFamily)FindResource("FontMono"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };

        if (entry.TextChanged)
        {
            var highlightBg = (WpfBrush)FindResource("DiffPlusBg");
            var highlightFg = (WpfBrush)FindResource("DiffPlusText");
            var normalFg = (WpfBrush)FindResource("TextPrimary");

            int charCount = 0;
            foreach (var (token, isChanged) in WordDiff.Compute(entry.InputText, entry.OutputText))
            {
                if (charCount + token.Length > TruncateAt)
                {
                    tb.Inlines.Add(new Run("…") { Foreground = normalFg });
                    break;
                }
                tb.Inlines.Add(isChanged
                    ? new Run(token) { Background = highlightBg, Foreground = highlightFg }
                    : new Run(token) { Foreground = normalFg });
                charCount += token.Length;
            }
        }
        else
        {
            // No correction made — show text in muted style
            var display = entry.OutputText.Length > TruncateAt
                ? entry.OutputText[..TruncateAt] + "…"
                : entry.OutputText;
            tb.Text = display;
            tb.Foreground = (WpfBrush)FindResource("TextMuted");
        }

        return new Border
        {
            Background = (WpfBrush)FindResource("Surface"),
            BorderBrush = (WpfBrush)FindResource("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 6, 12, 6),
            Child = tb
        };
    }

    private FrameworkElement CreateSplitView(ActivityEntry entry)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
        stack.Children.Add(CreateDiffLine("DiffMinusLine", "DiffMinusText", "- " + entry.InputText));
        stack.Children.Add(CreateDiffLine("DiffPlusLine", "DiffPlusText", "+ " + entry.OutputText));
        return stack;
    }

    private Border CreateDiffLine(string borderStyle, string textBrush, string text)
    {
        var display = text.Length > TruncateAt ? text[..TruncateAt] + "…" : text;
        return new Border
        {
            Style = (Style)FindResource(borderStyle),
            Child = new TextBlock
            {
                FontFamily = (WpfFontFamily)FindResource("FontMono"),
                FontSize = 12,
                Foreground = (WpfBrush)FindResource(textBrush),
                Text = display,
                TextWrapping = TextWrapping.Wrap
            }
        };
    }
}

// ── Word-level LCS diff ────────────────────────────────────────────────────
internal static class WordDiff
{
    // Returns output tokens with a flag for each token that differs from input.
    // Spaces are folded into each word token so Inlines render correctly.
    public static List<(string Token, bool IsChanged)> Compute(string input, string output)
    {
        var inWords = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var outWords = output.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int m = inWords.Length, n = outWords.Length;

        var dp = new int[m + 1, n + 1];
        for (int i = 1; i <= m; i++)
            for (int j = 1; j <= n; j++)
                dp[i, j] = string.Equals(inWords[i - 1], outWords[j - 1], StringComparison.OrdinalIgnoreCase)
                    ? dp[i - 1, j - 1] + 1
                    : Math.Max(dp[i - 1, j], dp[i, j - 1]);

        var changed = new bool[n];
        int oi = m, oj = n;
        while (oi > 0 || oj > 0)
        {
            if (oi > 0 && oj > 0 && string.Equals(inWords[oi - 1], outWords[oj - 1], StringComparison.OrdinalIgnoreCase))
            { oi--; oj--; }
            else if (oj > 0 && (oi == 0 || dp[oi, oj - 1] >= dp[oi - 1, oj]))
            { changed[oj - 1] = true; oj--; }
            else
            { oi--; }
        }

        var result = new List<(string, bool)>(n);
        for (int j = 0; j < n; j++)
        {
            var token = j < n - 1 ? outWords[j] + " " : outWords[j];
            result.Add((token, changed[j]));
        }
        return result;
    }
}

// ── Data model ─────────────────────────────────────────────────────────────
internal sealed record ActivityEntry(
    DateTimeOffset Timestamp,
    string Status,
    string Model,
    string InputText,
    string OutputText,
    bool TextChanged);

// ── Log reader ─────────────────────────────────────────────────────────────
internal static class NativeActivityLogReader
{
    // Read entries for a date range, newest date first within each day.
    public static IEnumerable<ActivityEntry> ReadDateRange(DateTime from, DateTime to)
    {
        for (var date = to.Date; date >= from.Date; date = date.AddDays(-1))
        {
            var path = AppPaths.SpellcheckLogPathFor(date);
            if (!File.Exists(path))
                continue;
            foreach (var entry in ReadFile(path))
                yield return entry;
        }
    }

    // Fast substring scan across all log files — no full JSON parse needed.
    public static (int Checks, int Corrections) ReadAllTimeStats()
    {
        if (!Directory.Exists(AppPaths.LogDirectory))
            return (0, 0);

        int checks = 0, corrections = 0;
        foreach (var path in Directory.GetFiles(AppPaths.LogDirectory, "spellcheck-*.jsonl"))
        {
            foreach (var line in File.ReadLines(path))
            {
                if (line.IndexOf(" spellcheck_detail ", StringComparison.Ordinal) < 0)
                    continue;
                if (!line.Contains("\"status\":\"success\"", StringComparison.Ordinal))
                    continue;
                checks++;
                if (line.Contains("\"text_changed\":true", StringComparison.Ordinal))
                    corrections++;
            }
        }
        return (checks, corrections);
    }

    private static IEnumerable<ActivityEntry> ReadFile(string path)
    {
        foreach (var line in File.ReadLines(path).Reverse())
        {
            var markerIndex = line.IndexOf(" spellcheck_detail ", StringComparison.Ordinal);
            if (markerIndex < 0)
                continue;

            var firstSpace = line.IndexOf(' ');
            if (firstSpace < 0 || !DateTimeOffset.TryParse(line[..firstSpace], out var timestamp))
                continue;

            var jsonStart = markerIndex + " spellcheck_detail ".Length;
            if (jsonStart >= line.Length)
                continue;

            ActivityEntry? entry = null;
            try
            {
                using var doc = JsonDocument.Parse(line[jsonStart..]);
                var root = doc.RootElement;
                var status = GetString(root, "status");
                var input = GetString(root, "input_text");
                var output = GetString(root, "output_text");
                if (status != "success" || string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output))
                    continue;

                entry = new ActivityEntry(
                    timestamp,
                    status,
                    GetString(root, "model", "unknown"),
                    input,
                    output,
                    GetBool(root, "text_changed"));
            }
            catch { continue; }

            yield return entry;
        }
    }

    private static string GetString(JsonElement root, string name, string fallback = "")
        => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? fallback
            : fallback;

    private static bool GetBool(JsonElement root, string name)
        => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;
}
