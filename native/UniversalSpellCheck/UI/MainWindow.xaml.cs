using System.Windows;
using System.Windows.Controls;
using UniversalSpellCheck.UI.Pages;

namespace UniversalSpellCheck.UI;

internal partial class MainWindow : Window
{
    private readonly ActivityPage _activityPage;
    private readonly SettingsPage _settingsPage;

    public MainWindow(SettingsStore settingsStore, DiagnosticsLogger logger)
    {
        InitializeComponent();
        _activityPage = new ActivityPage();
        _settingsPage = new SettingsPage(settingsStore, logger);
        ContentFrame.Navigate(_activityPage);
    }

    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.RadioButton rb || ContentFrame is null) return;

        if (rb == HomeNav)
            ContentFrame.Navigate(_activityPage);
        else if (rb == SettingsNav)
            ContentFrame.Navigate(_settingsPage);
    }
}
