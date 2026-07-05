using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfButton = System.Windows.Controls.Button;
using WpfColor = System.Windows.Media.Color;
using WpfContextMenu = System.Windows.Controls.ContextMenu;
using WpfMenuItem = System.Windows.Controls.MenuItem;
using WpfFontFamily = System.Windows.Media.FontFamily;

namespace UniversalSpellCheck.UI.Pages;

internal partial class ActivityPage : Page
{
    /// <summary>Hard cap for the timestamp and model metadata without excess gutter.</summary>
    private const double MetadataColumnMaxWidth = 96;
    private const int FeedPageSize = 30;
    private const double LoadMoreScrollThreshold = 120;

    private ActivityLogCursor? _feedCursor;
    private bool _hasMoreEntries = true;
    private bool _isLoadingMore;
    private bool _viewportFillScheduled;
    private int _loadGeneration;
    private readonly HashSet<DateTime> _renderedDays = new();
    private readonly DiagnosticsLogger _logger;
    private readonly TaskCompletionSource _initialLoadCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task InitialLoadCompleted => _initialLoadCompleted.Task;
    internal int InitialPageEntryCount { get; private set; }
    internal int LoadedEntryCount { get; private set; }

    public ActivityPage(DiagnosticsLogger logger)
    {
        _logger = logger;
        InitializeComponent();
        FeedScrollViewer.ScrollChanged += OnFeedScrollChanged;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await RefreshActivityAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshActivityAsync();

    private async Task RefreshActivityAsync()
    {
        var generation = ++_loadGeneration;
        _feedCursor = null;
        _hasMoreEntries = true;
        _isLoadingMore = false;
        _viewportFillScheduled = false;
        _renderedDays.Clear();
        LoadedEntryCount = 0;

        FeedItems.Children.Clear();

        HideLoadingIndicator();

        var statsTask = Task.Run(NativeActivityLogReader.ReadAllTimeStats);
        await LoadNextPageAsync(isInitial: true, generation);

        try
        {
            var (checks, corrections, dayStreak) = await statsTask;
            if (generation != _loadGeneration)
                return;

            StatChecks.Text = checks.ToString("N0");
            StatCorrections.Text = corrections.ToString("N0");
            StatAccuracy.Text = checks == 0 ? "—" : $"{Math.Round((double)corrections / checks * 100)}%";
            StatStreak.Text = dayStreak.ToString("N0");
        }
        catch (Exception ex)
        {
            LogLoadFailure("stats", ex);
        }
    }

    private async void OnFeedScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_isLoadingMore || !_hasMoreEntries)
            return;

        if (e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - LoadMoreScrollThreshold)
            return;

        await LoadNextPageAsync(isInitial: false, _loadGeneration);
    }

    private async Task LoadNextPageAsync(bool isInitial, int generation)
    {
        if (_isLoadingMore || !_hasMoreEntries)
            return;

        _isLoadingMore = true;
        if (!isInitial)
            ShowLoadingIndicator();

        try
        {
            var cursor = _feedCursor;
            var (entries, nextCursor, hasMore) = await Task.Run(
                () => NativeActivityLogReader.ReadEntries(FeedPageSize, cursor));
            if (generation != _loadGeneration)
                return;

            _feedCursor = nextCursor;
            _hasMoreEntries = hasMore;

            if (isInitial)
            {
                InitialPageEntryCount = entries.Count;
                EmptyState.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            if (entries.Count > 0)
            {
                if (isInitial)
                    PrependEntries(entries);
                else
                    AppendEntries(entries);
                LoadedEntryCount += entries.Count;
            }

            if (isInitial)
                _initialLoadCompleted.TrySetResult();
        }
        catch (Exception ex)
        {
            LogLoadFailure(isInitial ? "initial_page" : "next_page", ex);
            if (isInitial)
                _initialLoadCompleted.TrySetException(ex);
        }
        finally
        {
            if (generation == _loadGeneration)
            {
                _isLoadingMore = false;
                HideLoadingIndicator();
                ScheduleViewportFill(generation);
            }
        }
    }

    private void ScheduleViewportFill(int generation)
    {
        if (_viewportFillScheduled || !_hasMoreEntries || _isLoadingMore)
            return;

        _viewportFillScheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(async () =>
        {
            _viewportFillScheduled = false;
            if (generation != _loadGeneration || !_hasMoreEntries || _isLoadingMore)
                return;

            // Extent/viewport values are valid only after WPF has completed a layout pass.
            if (FeedScrollViewer.ViewportHeight <= 0 ||
                FeedScrollViewer.ExtentHeight > FeedScrollViewer.ViewportHeight + LoadMoreScrollThreshold)
                return;

            await LoadNextPageAsync(isInitial: false, generation);
        }));
    }

    private void LogLoadFailure(string stage, Exception ex)
    {
        _logger.Log(
            $"activity_load_failed stage={stage} error_type={ex.GetType().Name} " +
            $"error=\"{Escape(ex.Message)}\" stack=\"{Escape(ex.ToString())}\"");
    }

    private static string Escape(string? value)
    {
        return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
    }

    private void PrependEntries(IReadOnlyList<ActivityEntry> entries)
    {
        var today = DateTime.Now.Date;
        var insertAt = 0;
        var isFirstInFeed = true;

        foreach (var dayGroup in entries
                     .GroupBy(e => e.Timestamp.ToLocalTime().Date)
                     .OrderByDescending(g => g.Key))
        {
            if (_renderedDays.Add(dayGroup.Key))
            {
                FeedItems.Children.Insert(
                    insertAt++,
                    CreateDayHeader(dayGroup.Key, today, isFirstInFeed: isFirstInFeed));
                isFirstInFeed = false;
            }

            foreach (var entry in dayGroup.OrderByDescending(e => e.Timestamp))
                FeedItems.Children.Insert(insertAt++, CreateDiffRow(entry));
        }
    }

    private void AppendEntries(IReadOnlyList<ActivityEntry> entries)
    {
        var today = DateTime.Now.Date;

        foreach (var dayGroup in entries
                     .GroupBy(e => e.Timestamp.ToLocalTime().Date)
                     .OrderBy(g => g.Key))
        {
            if (_renderedDays.Add(dayGroup.Key))
                FeedItems.Children.Add(CreateDayHeader(dayGroup.Key, today, isFirstInFeed: false));

            foreach (var entry in dayGroup.OrderBy(e => e.Timestamp))
                FeedItems.Children.Add(CreateDiffRow(entry));
        }
    }

    private void ShowLoadingIndicator()
    {
        LoadingIndicator.Visibility = Visibility.Visible;
        if (FindResource("FeedLoadingSpinnerStoryboard") is Storyboard storyboard)
            storyboard.Begin(LoadingIndicator, true);
    }

    private void HideLoadingIndicator()
    {
        if (FindResource("FeedLoadingSpinnerStoryboard") is Storyboard storyboard)
            storyboard.Stop(LoadingIndicator);

        LoadingIndicator.Visibility = Visibility.Collapsed;
    }

    private FrameworkElement CreateDayHeader(DateTime day, DateTime today, bool isFirstInFeed)
    {
        var header = new StackPanel
        {
            Margin = new Thickness(0, isFirstInFeed ? 4 : 28, 0, 4)
        };

        header.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Caption"),
            Text = FormatDayLabel(day, today).ToUpperInvariant(),
            Margin = new Thickness(0, 0, 0, 10)
        });

        header.Children.Add(new Border
        {
            Height = 1,
            Background = (WpfBrush)FindResource("Border")
        });

        return header;
    }

    private static string FormatDayLabel(DateTime day, DateTime today)
    {
        if (day == today)
            return "Today";

        if (day == today.AddDays(-1))
            return "Yesterday";

        return day.Year == today.Year
            ? day.ToString("MMMM d", CultureInfo.CurrentCulture)
            : day.ToString("MMMM d, yyyy", CultureInfo.CurrentCulture);
    }

    private FrameworkElement CreateDiffRow(ActivityEntry entry)
    {
        var rowWrap = new StackPanel();

        var rowChrome = new Border
        {
            Background = WpfBrushes.Transparent,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(-8, 2, -8, 2),
            CornerRadius = new CornerRadius(6)
        };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto,
            MaxWidth = MetadataColumnMaxWidth
        });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rowChrome.Child = row;
        rowWrap.Children.Add(rowChrome);

        var metadata = new StackPanel
        {
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        metadata.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("MonoSmall"),
            Text = entry.Timestamp.ToLocalTime().ToString("h:mm tt"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        });
        metadata.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Caption"),
            Text = entry.Model,
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = entry.Model
        });
        Grid.SetColumn(metadata, 0);
        row.Children.Add(metadata);

        var inlineBlock = CreateInlineDiffBlock(entry);

        var diffBody = new StackPanel();
        diffBody.Children.Add(inlineBlock);

        var diffHost = new Border
        {
            Child = diffBody,
            Margin = new Thickness(0, 0, 12, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Click to copy corrected text"
        };
        Grid.SetColumn(diffHost, 1);
        row.Children.Add(diffHost);

        var actions = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top,
            Opacity = 0,
            Margin = new Thickness(0, -4, 0, 0)
        };
        Grid.SetColumn(actions, 2);
        row.Children.Add(actions);

        var hoverBrush = (WpfBrush)FindResource("HoverGhost");
        rowChrome.MouseEnter += (_, _) =>
        {
            rowChrome.Background = hoverBrush;
            actions.Opacity = 1;
        };
        rowChrome.MouseLeave += (_, _) =>
        {
            rowChrome.Background = WpfBrushes.Transparent;
            actions.Opacity = 0;
        };

        DispatcherTimer? copyResetTimer = null;
        var copyButton = CreateIconButton(FeedActionIcons.Copy(), "Copy corrected text");

        void CopyCorrectedText()
        {
            if (!TryCopyOutputText(entry.OutputText))
                return;

            copyButton.Content = FeedActionIcons.Check();
            copyButton.ToolTip = "Copied";

            copyResetTimer?.Stop();
            copyResetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            copyResetTimer.Tick += (_, _) =>
            {
                copyResetTimer?.Stop();
                copyResetTimer = null;
                copyButton.Content = FeedActionIcons.Copy();
                copyButton.ToolTip = "Copy corrected text";
            };
            copyResetTimer.Start();
        }

        copyButton.Click += (_, e) =>
        {
            CopyCorrectedText();
            e.Handled = true;
        };
        actions.Children.Add(copyButton);

        if (entry.TextChanged)
            actions.Children.Add(CreateDiffViewMenuButton(inlineBlock, diffBody, entry));

        diffHost.MouseLeftButtonUp += (_, e) =>
        {
            CopyCorrectedText();
            e.Handled = true;
        };

        rowWrap.Children.Add(new Border
        {
            Height = 1,
            Background = (WpfBrush)FindResource("Border"),
            Opacity = 0.85
        });

        return rowWrap;
    }

    private FrameworkElement CreateInlineDiffBlock(ActivityEntry entry)
    {
        var block = new StackPanel();

        if (!entry.TextChanged)
        {
            block.Children.Add(CreatePlainTextLine(entry.OutputText));
            return block;
        }

        var lineDiffs = InlineTextDiff.AlignLines(
            SplitLines(entry.InputText),
            SplitLines(entry.OutputText));

        foreach (var line in lineDiffs)
        {
            block.Children.Add(line.Kind switch
            {
                LineDiffKind.Unchanged => CreatePlainTextLine(line.NewLine),
                LineDiffKind.Modified => CreateInlineCharDiffLine(line.OldLine, line.NewLine),
                LineDiffKind.Removed => CreateRemovedOnlyLine(line.OldLine),
                LineDiffKind.Added => CreateAddedOnlyLine(line.NewLine),
                _ => CreatePlainTextLine(line.NewLine)
            });
        }

        return block;
    }

    private FrameworkElement CreateSplitDiffBlock(ActivityEntry entry)
    {
        var block = new StackPanel();

        block.Children.Add(CreateSplitColumnHeader());

        var lineDiffs = InlineTextDiff.AlignLines(
            SplitLines(entry.InputText),
            SplitLines(entry.OutputText));

        var lineNumber = 1;
        foreach (var line in lineDiffs)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var leftSide = line.Kind switch
            {
                LineDiffKind.Unchanged => CreateSplitSidePlain(line.OldLine, lineNumber, isOriginal: true),
                LineDiffKind.Modified => CreateSplitSideInline(line.OldLine, line.NewLine, lineNumber, isOriginal: true),
                LineDiffKind.Removed => CreateSplitSideFullLine(line.OldLine, lineNumber, isOriginal: true),
                LineDiffKind.Added => CreateSplitSideEmpty(lineNumber, isOriginal: true),
                _ => CreateSplitSidePlain(line.OldLine, lineNumber, isOriginal: true)
            };

            var rightSide = line.Kind switch
            {
                LineDiffKind.Unchanged => CreateSplitSidePlain(line.NewLine, lineNumber, isOriginal: false),
                LineDiffKind.Modified => CreateSplitSideInline(line.OldLine, line.NewLine, lineNumber, isOriginal: false),
                LineDiffKind.Removed => CreateSplitSideEmpty(lineNumber, isOriginal: false),
                LineDiffKind.Added => CreateSplitSideFullLine(line.NewLine, lineNumber, isOriginal: false),
                _ => CreateSplitSidePlain(line.NewLine, lineNumber, isOriginal: false)
            };

            Grid.SetColumn(leftSide, 0);
            Grid.SetColumn(rightSide, 1);
            row.Children.Add(leftSide);
            row.Children.Add(rightSide);
            block.Children.Add(row);
            lineNumber++;
        }

        return block;
    }

    private Grid CreateSplitColumnHeader()
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        row.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Caption"),
            Text = "ORIGINAL",
            Margin = new Thickness(0, 0, 8, 0)
        });

        var corrected = new TextBlock
        {
            Style = (Style)FindResource("Caption"),
            Text = "CORRECTED",
            Margin = new Thickness(8, 0, 0, 0)
        };
        Grid.SetColumn(corrected, 1);
        row.Children.Add(corrected);

        return row;
    }

    private Border CreatePlainTextLine(string text)
    {
        return WrapDiffText(CreateDiffTextBlock(text));
    }

    private Border CreateInlineCharDiffLine(string oldLine, string newLine)
    {
        var textBlock = CreateDiffTextBlock("");
        AppendInlineCharDiff(textBlock.Inlines, oldLine, newLine);
        return WrapDiffText(textBlock);
    }

    private Border CreateRemovedOnlyLine(string text)
    {
        var textBlock = CreateDiffTextBlock("");
        AppendDiffRuns(textBlock.Inlines, text, TextDiffKind.Delete, showNewlineGlyphs: true);
        return WrapDiffText(textBlock, highlightLine: true, removed: true);
    }

    private Border CreateAddedOnlyLine(string text)
    {
        var textBlock = CreateDiffTextBlock("");
        AppendDiffRuns(textBlock.Inlines, text, TextDiffKind.Insert, showNewlineGlyphs: true);
        return WrapDiffText(textBlock, highlightLine: true, removed: false);
    }

    private TextBlock CreateDiffTextBlock(string text)
    {
        return new TextBlock
        {
            FontFamily = (WpfFontFamily)FindResource("FontMono"),
            FontSize = 12,
            Foreground = (WpfBrush)FindResource("TextPrimary"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 16,
            Text = text
        };
    }

    private Border WrapDiffText(TextBlock textBlock, bool highlightLine = false, bool removed = false)
    {
        var line = new Border
        {
            Padding = new Thickness(8, 2, 8, 2),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 0, 0),
            Child = textBlock
        };

        if (highlightLine)
        {
            line.Background = removed
                ? (WpfBrush)FindResource("DiffMinusBg")
                : (WpfBrush)FindResource("DiffPlusBg");
        }

        return line;
    }

    private void AppendInlineCharDiff(InlineCollection inlines, string oldLine, string newLine)
    {
        foreach (var segment in InlineTextDiff.ComputeChars(oldLine, newLine))
            AppendDiffRuns(inlines, segment.Text, segment.Kind, showNewlineGlyphs: segment.Kind != TextDiffKind.Equal);
    }

    private void AppendDiffRuns(
        InlineCollection inlines,
        string text,
        TextDiffKind kind,
        bool showNewlineGlyphs)
    {
        if (text.Length == 0)
            return;

        if (!showNewlineGlyphs || !text.Contains('\n'))
        {
            inlines.Add(CreateDiffRun(text, kind));
            return;
        }

        var parts = text.Split('\n');
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0)
                inlines.Add(CreateDiffRun(parts[i], kind));

            if (i >= parts.Length - 1)
                continue;

            if (kind != TextDiffKind.Equal)
            {
                inlines.Add(new Run("↵")
                {
                    Foreground = kind == TextDiffKind.Delete
                        ? (WpfBrush)FindResource("DiffMinusText")
                        : (WpfBrush)FindResource("DiffPlusText"),
                    Background = kind == TextDiffKind.Delete
                        ? (WpfBrush)FindResource("DiffMinusBg")
                        : (WpfBrush)FindResource("DiffPlusBg")
                });
            }

            inlines.Add(new Run("\n"));
        }
    }

    private Run CreateDiffRun(string text, TextDiffKind kind)
    {
        var run = new Run(text);
        switch (kind)
        {
            case TextDiffKind.Delete:
                run.Background = (WpfBrush)FindResource("DiffMinusBg");
                run.Foreground = (WpfBrush)FindResource("DiffMinusText");
                run.TextDecorations = TextDecorations.Strikethrough;
                break;
            case TextDiffKind.Insert:
                run.Background = (WpfBrush)FindResource("DiffPlusBg");
                run.Foreground = (WpfBrush)FindResource("DiffPlusText");
                break;
        }

        return run;
    }

    private Border CreateSplitSidePlain(string text, int lineNumber, bool isOriginal)
    {
        return CreateSplitSideShell(
            lineNumber,
            isOriginal,
            highlightLine: false,
            removedSide: isOriginal,
            CreateDiffTextBlock(text));
    }

    private Border CreateSplitSideInline(string oldLine, string newLine, int lineNumber, bool isOriginal)
    {
        var textBlock = CreateDiffTextBlock("");
        AppendSplitSideCharDiff(textBlock.Inlines, oldLine, newLine, isOriginal);
        return CreateSplitSideShell(lineNumber, isOriginal, highlightLine: true, removedSide: isOriginal, textBlock);
    }

    private Border CreateSplitSideFullLine(string text, int lineNumber, bool isOriginal)
    {
        var textBlock = CreateDiffTextBlock("");
        AppendSplitSideRuns(textBlock.Inlines, text, isOriginal ? TextDiffKind.Delete : TextDiffKind.Insert);
        return CreateSplitSideShell(lineNumber, isOriginal, highlightLine: true, removedSide: isOriginal, textBlock);
    }

    private Border CreateSplitSideEmpty(int lineNumber, bool isOriginal)
    {
        return CreateSplitSideShell(
            lineNumber,
            isOriginal,
            highlightLine: false,
            removedSide: isOriginal,
            CreateDiffTextBlock(""));
    }

    private Border CreateSplitSideShell(
        int lineNumber,
        bool isOriginal,
        bool highlightLine,
        bool removedSide,
        TextBlock textBlock)
    {
        var side = new Border
        {
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(isOriginal ? 0 : 8, 0, isOriginal ? 8 : 0, 0),
            CornerRadius = new CornerRadius(4),
            Background = highlightLine
                ? (removedSide
                    ? (WpfBrush)FindResource("DiffMinusBg")
                    : (WpfBrush)FindResource("DiffPlusBg"))
                : (WpfBrush)FindResource("Canvas")
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        grid.Children.Add(new TextBlock
        {
            FontFamily = (WpfFontFamily)FindResource("FontMono"),
            FontSize = 12,
            Foreground = (WpfBrush)FindResource("TextMuted"),
            Text = lineNumber.ToString(),
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 10, 0)
        });

        Grid.SetColumn(textBlock, 1);
        grid.Children.Add(textBlock);

        side.Child = grid;
        return side;
    }

    private void AppendSplitSideCharDiff(InlineCollection inlines, string oldLine, string newLine, bool isOriginal)
    {
        foreach (var segment in InlineTextDiff.ComputeChars(oldLine, newLine))
        {
            if (isOriginal && segment.Kind == TextDiffKind.Insert)
                continue;
            if (!isOriginal && segment.Kind == TextDiffKind.Delete)
                continue;

            inlines.Add(segment.Kind switch
            {
                TextDiffKind.Equal => new Run(segment.Text)
                {
                    Foreground = (WpfBrush)FindResource("TextPrimary")
                },
                TextDiffKind.Delete => CreateSplitHighlightRun(segment.Text, removed: true),
                TextDiffKind.Insert => CreateSplitHighlightRun(segment.Text, removed: false),
                _ => new Run(segment.Text)
                {
                    Foreground = (WpfBrush)FindResource("TextPrimary")
                }
            });
        }
    }

    private void AppendSplitSideRuns(InlineCollection inlines, string text, TextDiffKind kind)
    {
        if (text.Length == 0)
            return;

        inlines.Add(CreateSplitHighlightRun(text, removed: kind == TextDiffKind.Delete));
    }

    private Run CreateSplitHighlightRun(string text, bool removed)
    {
        var accentColor = (WpfColor)FindResource(removed ? "DiffMinusTextColor" : "DiffPlusTextColor");
        return new Run(text)
        {
            Background = new SolidColorBrush(WpfColor.FromArgb(0x66, accentColor.R, accentColor.G, accentColor.B)),
            Foreground = (WpfBrush)FindResource(removed ? "DiffMinusText" : "DiffPlusText"),
            FontWeight = FontWeights.SemiBold
        };
    }

    private WpfButton CreateIconButton(UIElement icon, string toolTip, Action? onClick = null)
    {
        var button = new WpfButton
        {
            Style = (Style)FindResource("IconButton"),
            Content = icon,
            ToolTip = toolTip
        };
        if (onClick is not null)
            button.Click += (_, _) => onClick();
        return button;
    }

    private WpfButton CreateDiffViewMenuButton(
        FrameworkElement inlineBlock,
        System.Windows.Controls.Panel diffBody,
        ActivityEntry entry)
    {
        var inlineItem = new WpfMenuItem
        {
            Header = "Inline diff",
            IsCheckable = true,
            IsChecked = true
        };
        var sideBySideItem = new WpfMenuItem
        {
            Header = "Side by side",
            IsCheckable = true
        };

        FrameworkElement? splitBlock = null;

        void SetDiffView(bool sideBySide)
        {
            if (sideBySide && splitBlock is null)
            {
                splitBlock = CreateSplitDiffBlock(entry);
                diffBody.Children.Add(splitBlock);
            }

            inlineBlock.Visibility = sideBySide ? Visibility.Collapsed : Visibility.Visible;
            if (splitBlock is not null)
                splitBlock.Visibility = sideBySide ? Visibility.Visible : Visibility.Collapsed;
            inlineItem.IsChecked = !sideBySide;
            sideBySideItem.IsChecked = sideBySide;
        }

        inlineItem.Click += (_, _) => SetDiffView(sideBySide: false);
        sideBySideItem.Click += (_, _) => SetDiffView(sideBySide: true);

        var menu = new WpfContextMenu
        {
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = false
        };
        menu.Items.Add(inlineItem);
        menu.Items.Add(sideBySideItem);

        var button = CreateIconButton(FeedActionIcons.MoreVertical(), "Diff view options");
        button.ContextMenu = menu;
        button.Click += (_, e) =>
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
            e.Handled = true;
        };
        return button;
    }

    private static bool TryCopyOutputText(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            return true;
        }
        catch
        {
            // Clipboard can be busy; the action is best-effort.
            return false;
        }
    }

    private static List<string> SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text))
            return new List<string> { "" };

        return text.Replace("\r\n", "\n").Split('\n').ToList();
    }
}

internal sealed record ActivityEntry(
    DateTimeOffset Timestamp,
    string Status,
    string Model,
    string InputText,
    string OutputText,
    bool TextChanged);

internal sealed class ActivityLogCursor
{
    public required string[] LogPaths { get; init; }
    public int FileIndex { get; init; }
    public int LineIndex { get; init; }
}

internal static class NativeActivityLogReader
{
    public static (IReadOnlyList<ActivityEntry> Entries, ActivityLogCursor? NextCursor, bool HasMore) ReadEntries(
        int count,
        ActivityLogCursor? cursor)
    {
        if (count <= 0)
            return ([], cursor, false);

        var paths = cursor?.LogPaths ?? GetSortedLogPaths();
        if (paths.Length == 0)
            return ([], null, false);

        var fileIndex = cursor?.FileIndex ?? 0;
        var lineIndex = cursor?.LineIndex ?? 0;
        var entries = new List<ActivityEntry>(count);
        string[] fileLines = [];
        var currentPath = (string?)null;

        while (entries.Count < count && fileIndex < paths.Length)
        {
            var path = paths[fileIndex];
            if (!string.Equals(currentPath, path, StringComparison.OrdinalIgnoreCase))
            {
                fileLines = File.Exists(path) ? File.ReadAllLines(path) : [];
                currentPath = path;
            }

            while (entries.Count < count && lineIndex < fileLines.Length)
            {
                var line = fileLines[fileLines.Length - 1 - lineIndex];
                lineIndex++;

                if (TryParseLine(line, out var entry))
                    entries.Add(entry);
            }

            if (lineIndex >= fileLines.Length)
            {
                fileIndex++;
                lineIndex = 0;
            }
        }

        var hasMore = fileIndex < paths.Length;
        ActivityLogCursor? nextCursor = hasMore
            ? new ActivityLogCursor
            {
                LogPaths = paths,
                FileIndex = fileIndex,
                LineIndex = lineIndex
            }
            : null;

        return (entries, nextCursor, hasMore);
    }

    public static (int Checks, int Corrections, int DayStreak) ReadAllTimeStats()
    {
        if (!Directory.Exists(AppPaths.LogDirectory))
            return (0, 0, 0);

        int checks = 0, corrections = 0;
        var activeDays = new HashSet<DateTime>();

        foreach (var path in Directory.GetFiles(AppPaths.LogDirectory, "spellcheck-*.jsonl"))
        {
            if (!TryParseLogDate(path, out var date))
                continue;

            var dayHasSuccess = false;
            foreach (var line in File.ReadLines(path))
            {
                if (line.IndexOf(" spellcheck_detail ", StringComparison.Ordinal) < 0)
                    continue;
                if (!line.Contains("\"status\":\"success\"", StringComparison.Ordinal))
                    continue;

                checks++;
                dayHasSuccess = true;
                if (line.Contains("\"text_changed\":true", StringComparison.Ordinal))
                    corrections++;
            }

            if (dayHasSuccess)
                activeDays.Add(date);
        }

        return (checks, corrections, ComputeDayStreak(activeDays, DateTime.Now.Date));
    }

    private static bool TryParseLogDate(string path, out DateTime date)
    {
        date = default;
        var fileName = Path.GetFileNameWithoutExtension(path);
        const string prefix = "spellcheck-";
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        return DateTime.TryParseExact(
            fileName[prefix.Length..],
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
    }

    private static int ComputeDayStreak(IReadOnlySet<DateTime> activeDays, DateTime today)
    {
        if (activeDays.Count == 0)
            return 0;

        var cursor = today;
        if (!activeDays.Contains(today))
        {
            var yesterday = today.AddDays(-1);
            if (activeDays.Contains(yesterday))
                cursor = yesterday;
            else
                return 0;
        }

        var streak = 0;
        while (activeDays.Contains(cursor))
        {
            streak++;
            cursor = cursor.AddDays(-1);
        }

        return streak;
    }

    private static string[] GetSortedLogPaths()
    {
        if (!Directory.Exists(AppPaths.LogDirectory))
            return [];

        return Directory.GetFiles(AppPaths.LogDirectory, "spellcheck-*.jsonl")
            .Select(path => (Path: path, Date: TryParseLogDate(path, out var date) ? date : DateTime.MinValue))
            .Where(item => item.Date != DateTime.MinValue)
            .OrderByDescending(item => item.Date)
            .Select(item => item.Path)
            .ToArray();
    }

    private static bool TryParseLine(string line, out ActivityEntry entry)
    {
        entry = null!;

        var markerIndex = line.IndexOf(" spellcheck_detail ", StringComparison.Ordinal);
        if (markerIndex < 0)
            return false;

        var firstSpace = line.IndexOf(' ');
        if (firstSpace < 0 || !DateTimeOffset.TryParse(line[..firstSpace], out var timestamp))
            return false;

        var jsonStart = markerIndex + " spellcheck_detail ".Length;
        if (jsonStart >= line.Length)
            return false;

        try
        {
            using var doc = JsonDocument.Parse(line[jsonStart..]);
            var root = doc.RootElement;
            var status = GetString(root, "status");
            var input = GetString(root, "input_text");
            var output = GetString(root, "output_text");
            if (status != "success" || string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output))
                return false;

            entry = new ActivityEntry(
                timestamp,
                status,
                GetString(root, "model", "unknown"),
                input,
                output,
                GetBool(root, "text_changed"));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetString(JsonElement root, string name, string fallback = "")
        => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? fallback
            : fallback;

    private static bool GetBool(JsonElement root, string name)
        => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;
}
