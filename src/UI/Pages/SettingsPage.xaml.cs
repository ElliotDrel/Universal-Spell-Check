using System.Windows.Controls;
using System.Diagnostics;

namespace UniversalSpellCheck.UI.Pages;

internal partial class SettingsPage : Page
{
    private readonly SettingsStore _settingsStore;
    private readonly DiagnosticsLogger _logger;
    private readonly UpdateService? _updateService;
    private bool _suppressStartupToggle;
    private bool _suppressModelSelection = true;
    private bool _suppressApiKeySelection;

    public SettingsPage(SettingsStore settingsStore, DiagnosticsLogger logger, UpdateService? updateService = null)
    {
        _settingsStore = settingsStore;
        _logger = logger;
        _updateService = updateService;
        InitializeComponent();
        RefreshUpdateDetails();
        if (_updateService is not null)
        {
            _updateService.CheckCompleted += OnUpdateCheckCompleted;
            _updateService.StateChanged += OnUpdateStateChanged;
        }
        var model = OpenAiSpellcheckService.NormalizeModel(_settingsStore.Load().Model);
        ModelComboBox.SelectedItem = ModelComboBox.Items.OfType<ComboBoxItem>()
            .First(item => (string)item.Tag == model);
        _suppressModelSelection = false;
        _suppressStartupToggle = true;
        StartupCheckBox.IsChecked = StartupRegistration.IsRegistered();
        if (BuildChannel.IsDev)
        {
            StartupCheckBox.IsEnabled = false;
            StartupCheckBox.ToolTip = "Dev builds are launched manually via dotnet run; auto-start is disabled.";
        }
        _suppressStartupToggle = false;
        RefreshApiKeys();
    }

    private void OnCheckForUpdatesClicked(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_updateService is null) return;
        _ = _updateService.CheckAsync(UpdateTrigger.ManualDashboard);
    }

    private void OnUpdateCheckCompleted(object? sender, CheckCompletedEventArgs e)
    {
        Dispatcher.BeginInvoke(RefreshUpdateDetails);
    }

    private void OnUpdateStateChanged(object? sender, UpdateState state)
    {
        Dispatcher.BeginInvoke(() =>
            CheckForUpdatesButton.IsEnabled = state is not (UpdateState.Checking or UpdateState.Downloading) &&
                                              !BuildChannel.IsDev);
    }

    private void RefreshUpdateDetails()
    {
        CurrentVersionText.Text = $"v{BuildChannel.AppVersion}";
        LastUpdatedText.Text = FormatTimestamp(_updateService?.LastUpdatedAt);
        LastCheckedText.Text = FormatTimestamp(_updateService?.LastCheckedAt);
        CheckForUpdatesButton.IsEnabled = !BuildChannel.IsDev && _updateService is not null;
    }

    private static string FormatTimestamp(DateTimeOffset? value) =>
        value?.LocalDateTime.ToString("MMM d, yyyy 'at' h:mm tt") ?? "Never";

    internal void DisposeUpdateEvents()
    {
        if (_updateService is not null)
        {
            _updateService.CheckCompleted -= OnUpdateCheckCompleted;
            _updateService.StateChanged -= OnUpdateStateChanged;
        }
    }

    private void OnModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressModelSelection || ModelComboBox.SelectedItem is not ComboBoxItem item) return;

        var settings = _settingsStore.Load();
        settings.Model = (string)item.Tag;
        _settingsStore.Save(settings);
        _logger.Log($"model_changed model={settings.Model} dashboard=true");
    }

    private void OnAddApiKeyClicked(object sender, System.Windows.RoutedEventArgs e)
    {
        var name = ApiKeyNameBox.Text.Trim();
        var apiKey = ApiKeyBox.Password.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ApiKeyStatus.Text = "Enter a name for the API key.";
            return;
        }
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            ApiKeyStatus.Text = "Enter an API key before saving.";
            return;
        }

        try
        {
            _settingsStore.AddApiKey(name, apiKey);
            ApiKeyNameBox.Clear();
            ApiKeyBox.Clear();
            RefreshApiKeys();
            ApiKeyStatus.Text = "Key added and selected.";
            _logger.Log("apikey_added dashboard=true");
        }
        catch (Exception ex)
        {
            ApiKeyStatus.Text = ex is ArgumentException or InvalidOperationException
                ? ex.Message
                : "Save failed.";
            _logger.Log($"apikey_save_failed dashboard=true error=\"{Escape(ex.Message)}\"");
        }
    }

    private void OnApiKeySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressApiKeySelection || ApiKeyComboBox.SelectedItem is not ApiKeyInfo selected)
            return;

        try
        {
            _settingsStore.SelectApiKey(selected.Id);
            ApiKeyStatus.Text = $"Using {selected.Name}.";
            _logger.Log("apikey_selected dashboard=true");
        }
        catch (Exception ex)
        {
            ApiKeyStatus.Text = "Selection failed.";
            _logger.Log($"apikey_select_failed dashboard=true error=\"{Escape(ex.Message)}\"");
            RefreshApiKeys();
        }
    }

    private void OnRemoveApiKeyClicked(object sender, System.Windows.RoutedEventArgs e)
    {
        if (ApiKeyComboBox.SelectedItem is not ApiKeyInfo selected)
        {
            ApiKeyStatus.Text = "Select a key to remove.";
            return;
        }

        var answer = System.Windows.MessageBox.Show(
            $"Remove the key named '{selected.Name}'?",
            "Remove API key",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (answer != System.Windows.MessageBoxResult.Yes)
            return;

        try
        {
            _settingsStore.RemoveApiKey(selected.Id);
            RefreshApiKeys();
            ApiKeyStatus.Text = "Key removed.";
            _logger.Log("apikey_removed dashboard=true");
        }
        catch (Exception ex)
        {
            ApiKeyStatus.Text = "Remove failed.";
            _logger.Log($"apikey_remove_failed dashboard=true error=\"{Escape(ex.Message)}\"");
        }
    }

    private void RefreshApiKeys()
    {
        var keys = _settingsStore.LoadApiKeyInfos();
        _suppressApiKeySelection = true;
        ApiKeyComboBox.ItemsSource = keys;
        ApiKeyComboBox.SelectedItem = keys.FirstOrDefault(key => key.IsActive) ?? keys.FirstOrDefault();
        _suppressApiKeySelection = false;
        ApiKeyComboBox.IsEnabled = keys.Count > 0;
    }

    private void OnOpenLogsClicked(object sender, System.Windows.RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.LogDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = AppPaths.LogDirectory,
            UseShellExecute = true
        });
    }

    private void OnEditReplacementsClicked(object sender, System.Windows.RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = AppPaths.ReplacementsPath,
            UseShellExecute = true
        });
    }

    private void OnStartupCheckBoxToggled(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_suppressStartupToggle) return;

        var enable = StartupCheckBox.IsChecked == true;
        try
        {
            if (enable)
            {
                StartupRegistration.Register();
                _logger.Log("startup_registered dashboard=true");
            }
            else
            {
                StartupRegistration.Unregister();
                _logger.Log("startup_unregistered dashboard=true");
            }
        }
        catch (Exception ex)
        {
            _logger.Log(
                $"startup_toggle_failed enable={enable} error_type={ex.GetType().Name} " +
                $"error=\"{Escape(ex.Message)}\"");

            _suppressStartupToggle = true;
            StartupCheckBox.IsChecked = StartupRegistration.IsRegistered();
            _suppressStartupToggle = false;
        }
    }

    private static string Escape(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
