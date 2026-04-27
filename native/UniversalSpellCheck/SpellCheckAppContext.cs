using System.Diagnostics;
using DashboardWindow = UniversalSpellCheck.UI.MainWindow;
using Forms = System.Windows.Forms;

namespace UniversalSpellCheck;

internal sealed class SpellCheckAppContext : Forms.ApplicationContext
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly HotkeyWindow _hotkeyWindow;
    private readonly DiagnosticsLogger _logger;
    private readonly SpellcheckCoordinator _coordinator;
    private readonly SettingsStore _settingsStore;
    private readonly OpenAiSpellcheckService _spellcheckService;
    private readonly TextPostProcessor _postProcessor;
    private readonly LoadingOverlayForm _loadingOverlay = new();
    private DashboardWindow? _dashboardWindow;

    public SpellCheckAppContext()
    {
        _logger = new DiagnosticsLogger(AppPaths.LogPath);
        _settingsStore = new SettingsStore(_logger);
        _spellcheckService = new OpenAiSpellcheckService(_settingsStore, _logger);
        _postProcessor = new TextPostProcessor(_logger);
        _coordinator = new SpellcheckCoordinator(
            _logger,
            _spellcheckService,
            _postProcessor,
            ShowTip,
            SetBusy,
            ShowSettings);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Universal Spell Check Native Spike",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };

        _hotkeyWindow = new HotkeyWindow();
        _hotkeyWindow.HotkeyPressed += OnHotkeyPressed;
        _hotkeyWindow.Register();

        _logger.Log("started hotkey=Ctrl+Alt+U model=gpt-4.1 phase=5");
    }

    private Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Dashboard", null, (_, _) => ShowSettings());
        menu.Items.Add("Open Logs Folder", null, (_, _) => OpenLogsFolder());
        menu.Items.Add("Quit", null, (_, _) => ExitThread());
        return menu;
    }

    private void OnHotkeyPressed(object? sender, EventArgs e)
    {
        _logger.Log("hotkey_pressed");
        _ = _coordinator.RunAsync();
    }

    private void OpenLogsFolder()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = AppPaths.LogDirectory,
            UseShellExecute = true
        });
    }

    private void ShowTip(string title, string message)
    {
        _notifyIcon.ShowBalloonTip(2500, title, message, Forms.ToolTipIcon.Info);
    }

    private void SetBusy(bool isBusy)
    {
        _notifyIcon.Text = isBusy
            ? "Universal Spell Check - checking"
            : "Universal Spell Check Native Spike";

        if (isBusy)
        {
            _loadingOverlay.ShowNearTaskbar();
        }
        else
        {
            _loadingOverlay.Hide();
        }
    }

    private void ShowSettings()
    {
        if (_dashboardWindow is not null)
        {
            if (!_dashboardWindow.IsVisible)
            {
                _dashboardWindow.Show();
            }

            if (_dashboardWindow.WindowState == System.Windows.WindowState.Minimized)
            {
                _dashboardWindow.WindowState = System.Windows.WindowState.Normal;
            }

            _dashboardWindow.Activate();
            return;
        }

        _dashboardWindow = new DashboardWindow(_settingsStore, _logger);
        _dashboardWindow.Closed += (_, _) => _dashboardWindow = null;
        _dashboardWindow.Show();
        _dashboardWindow.Activate();
    }

    protected override void ExitThreadCore()
    {
        _logger.Log("stopping");
        _hotkeyWindow.HotkeyPressed -= OnHotkeyPressed;
        _hotkeyWindow.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _dashboardWindow?.Close();
        _loadingOverlay.Dispose();
        _coordinator.Dispose();
        _spellcheckService.Dispose();
        base.ExitThreadCore();
    }
}
