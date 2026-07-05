using System.Windows;
using UniversalSpellCheck.UI.Pages;

namespace UniversalSpellCheck.UI;

internal partial class MainWindow : Window
{
    private readonly ActivityPage _activityPage;
    private readonly SettingsPage _settingsPage;
    private readonly UpdateService? _updateService;

    public MainWindow(SettingsStore settingsStore, DiagnosticsLogger logger, UpdateService? updateService = null)
    {
        InitializeComponent();
        _activityPage = new ActivityPage(logger);
        _settingsPage = new SettingsPage(settingsStore, logger, updateService);
        _updateService = updateService;
        ContentFrame.Navigate(_activityPage);

        VersionLabel.Text =
            BuildChannel.IsDev
                ? $"v{BuildChannel.AppVersion} · Dev"
                : $"v{BuildChannel.AppVersion}";

        if (_updateService is not null)
        {
            _updateService.StateChanged += OnUpdateStateChanged;
            ApplyUpdateState(_updateService.State);
            Closed += (_, _) =>
            {
                _updateService.StateChanged -= OnUpdateStateChanged;
                _settingsPage.DisposeUpdateEvents();
            };
        }
    }

    internal ActivityPage ActivityPage => _activityPage;

    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.RadioButton rb || ContentFrame is null) return;

        if (rb == HomeNav)
            ContentFrame.Navigate(_activityPage);
        else if (rb == SettingsNav)
            ContentFrame.Navigate(_settingsPage);
    }

    private void OnUpdateStateChanged(object? sender, UpdateState state)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => ApplyUpdateState(state)));
            return;
        }
        ApplyUpdateState(state);
    }

    private void ApplyUpdateState(UpdateState state)
    {
        VersionCheckButton.IsEnabled = state is not (UpdateState.Checking or UpdateState.Downloading) && !BuildChannel.IsDev;
        if (state is UpdateState.UpdateReady ready)
        {
            UpdateBannerText.Text = $"Update available — v{ready.Version}";
            UpdateBanner.Visibility = Visibility.Visible;
        }
        else
        {
            UpdateBanner.Visibility = Visibility.Collapsed;
        }
    }

    private void OnUpdateNowClicked(object sender, RoutedEventArgs e)
    {
        if (_updateService is null) return;
        _ = _updateService.ApplyUpdatesAndRestartAsync();
    }

    private void OnCheckForUpdatesClicked(object sender, RoutedEventArgs e)
    {
        if (_updateService is null) return;
        _ = _updateService.CheckAsync(UpdateTrigger.ManualDashboard);
    }
}
