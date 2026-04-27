using System.Windows.Controls;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace UniversalSpellCheck.UI.Pages;

internal partial class ActivityPage : Page
{
    public ActivityPage()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshActivity();
    }

    private void RefreshActivity()
    {
        var entries = NativeActivityLogReader.ReadToday().ToList();
        DiffStack.Children.Clear();

        EmptyState.Visibility = entries.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (entries.Count == 0)
        {
            DiffStack.Children.Add(EmptyState);
        }

        foreach (var entry in entries.Take(40))
        {
            DiffStack.Children.Add(CreateDiffRow(entry));
        }

        var successes = entries.Count(e => e.Status == "success");
        var corrections = entries.Count(e => e.TextChanged);
        StatChecks.Text = successes.ToString();
        StatCorrections.Text = corrections.ToString();
        StatAccuracy.Text = successes == 0 ? "-" : $"{Math.Round((double)corrections / successes * 100)}%";
        StatStreak.Text = successes == 0 ? "0" : "1";
    }

    private FrameworkElement CreateDiffRow(ActivityEntry entry)
    {
        var grid = new Grid
        {
            Margin = new Thickness(0, 0, 0, 16)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var time = new TextBlock
        {
            Style = (Style)FindResource("MonoSmall"),
            Text = entry.Timestamp.ToLocalTime().ToString("h:mm tt"),
            Margin = new Thickness(0, 0, 0, 4)
        };
        Grid.SetRow(time, 0);
        Grid.SetColumn(time, 0);
        grid.Children.Add(time);

        var model = new TextBlock
        {
            Style = (Style)FindResource("MonoSmall"),
            Text = entry.Model,
            Margin = new Thickness(0, 0, 0, 4)
        };
        Grid.SetRow(model, 0);
        Grid.SetColumn(model, 1);
        grid.Children.Add(model);

        var before = CreateDiffLine("DiffMinusLine", "DiffMinusText", "- " + entry.InputText);
        Grid.SetRow(before, 1);
        Grid.SetColumnSpan(before, 2);
        grid.Children.Add(before);

        var after = CreateDiffLine("DiffPlusLine", "DiffPlusText", "+ " + entry.OutputText);
        Grid.SetRow(after, 2);
        Grid.SetColumnSpan(after, 2);
        grid.Children.Add(after);

        return grid;
    }

    private Border CreateDiffLine(string borderStyle, string textBrush, string text)
    {
        return new Border
        {
            Style = (Style)FindResource(borderStyle),
            Child = new TextBlock
            {
                FontFamily = (System.Windows.Media.FontFamily)FindResource("FontMono"),
                FontSize = 12,
                Foreground = (System.Windows.Media.Brush)FindResource(textBrush),
                Text = text,
                TextWrapping = TextWrapping.Wrap
            }
        };
    }
}

internal sealed record ActivityEntry(
    DateTimeOffset Timestamp,
    string Status,
    string Model,
    string InputText,
    string OutputText,
    bool TextChanged);

internal static class NativeActivityLogReader
{
    public static IEnumerable<ActivityEntry> ReadToday()
    {
        if (!File.Exists(AppPaths.LogPath))
        {
            yield break;
        }

        foreach (var line in File.ReadLines(AppPaths.LogPath).Reverse())
        {
            var markerIndex = line.IndexOf(" spellcheck_detail ", StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                continue;
            }

            if (!DateTimeOffset.TryParse(line[..markerIndex], out var timestamp))
            {
                continue;
            }

            var jsonStart = markerIndex + " spellcheck_detail ".Length;
            if (jsonStart >= line.Length)
            {
                continue;
            }

            ActivityEntry? entry = null;
            try
            {
                using var document = JsonDocument.Parse(line[jsonStart..]);
                var root = document.RootElement;
                var status = GetString(root, "status");
                var input = GetString(root, "input_text");
                var output = GetString(root, "output_text");
                if (status != "success" || string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output))
                {
                    continue;
                }

                entry = new ActivityEntry(
                    timestamp,
                    status,
                    GetString(root, "model", "unknown"),
                    input,
                    output,
                    GetBool(root, "text_changed"));
            }
            catch
            {
                continue;
            }

            yield return entry;
        }
    }

    private static string GetString(JsonElement root, string name, string fallback = "")
    {
        return root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? fallback
            : fallback;
    }

    private static bool GetBool(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.True;
    }
}
