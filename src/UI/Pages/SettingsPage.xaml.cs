using System.Windows.Controls;
using System.Diagnostics;

namespace UniversalSpellCheck.UI.Pages;

internal partial class SettingsPage : Page
{
    private readonly SettingsStore _settingsStore;
    private readonly DiagnosticsLogger _logger;
    private bool _suppressStartupToggle;

    public SettingsPage(SettingsStore settingsStore, DiagnosticsLogger logger)
    {
        _settingsStore = settingsStore;
        _logger = logger;
        InitializeComponent();
        _suppressStartupToggle = true;
        StartupCheckBox.IsChecked = StartupRegistration.IsRegistered();
        if (BuildChannel.IsDev)
        {
            StartupCheckBox.IsEnabled = false;
            StartupCheckBox.ToolTip = "Dev builds are launched manually via dotnet run; auto-start is disabled.";
        }
        _suppressStartupToggle = false;
        ApiKeyBox.Password = "";
        ApiKeyBox.ToolTip = _settingsStore.HasApiKey()
            ? "API key saved. Enter a new key to replace it."
            : "Enter your OpenAI API key.";
    }

    private void OnSaveApiKeyClicked(object sender, System.Windows.RoutedEventArgs e)
    {
        var apiKey = ApiKeyBox.Password.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            ApiKeyStatus.Text = "Enter an API key before saving.";
            return;
        }

        try
        {
            _settingsStore.SaveApiKey(apiKey);
            ApiKeyBox.Clear();
            ApiKeyBox.ToolTip = "API key saved. Enter a new key to replace it.";
            ApiKeyStatus.Text = "Saved encrypted for this Windows user with DPAPI.";
            _logger.Log("apikey_saved dashboard=true");
        }
        catch (Exception ex)
        {
            ApiKeyStatus.Text = "Save failed.";
            _logger.Log($"apikey_save_failed dashboard=true error=\"{Escape(ex.Message)}\"");
        }
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
