using System.Windows.Controls;
using System.Diagnostics;

namespace UniversalSpellCheck.UI.Pages;

internal partial class SettingsPage : Page
{
    private readonly SettingsStore _settingsStore;
    private readonly DiagnosticsLogger _logger;

    public SettingsPage(SettingsStore settingsStore, DiagnosticsLogger logger)
    {
        _settingsStore = settingsStore;
        _logger = logger;
        InitializeComponent();
        StartupCheckBox.IsChecked = File.Exists(GetStartupShortcutPath());
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

    private static string GetStartupShortcutPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            "Universal Spell Check Native.lnk");
    }

    private static string Escape(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
