using System.Diagnostics;
using System.Windows.Threading;
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
        // Force the overlay handle to be created on this (UI) thread so
        // InvokeRequired in SetBusy is meaningful and BeginInvoke works.
        _ = _loadingOverlay.Handle;
        Dispatcher.CurrentDispatcher.UnhandledException += OnDispatcherUnhandledException;
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

        // Auto-open the dashboard on startup so the user can see UI errors
        // immediately instead of having to go discover them via the tray menu.
        // Posted to the UI thread so it runs after the message loop is up.
        System.Windows.Forms.Application.Idle += AutoOpenDashboardOnce;
    }

    private void AutoOpenDashboardOnce(object? sender, EventArgs e)
    {
        System.Windows.Forms.Application.Idle -= AutoOpenDashboardOnce;
        _logger.Log("dashboard_auto_open_attempt");
        ShowSettings();
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

        // Marshal to the WinForms UI thread — SetBusy is invoked from the
        // coordinator's async pipeline. ProgressBar marquee animation and
        // window show/hide must run on the form's owning thread, otherwise
        // the overlay can fail to paint.
        try
        {
            if (_loadingOverlay.IsHandleCreated && _loadingOverlay.InvokeRequired)
            {
                _loadingOverlay.BeginInvoke(new Action(() => ApplyBusy(isBusy)));
            }
            else
            {
                ApplyBusy(isBusy);
            }
        }
        catch (Exception ex)
        {
            _logger.Log(
                $"loading_overlay_failed is_busy={isBusy} error_type={ex.GetType().Name} " +
                $"error=\"{Escape(ex.Message)}\"");
        }
    }

    private void ApplyBusy(bool isBusy)
    {
        if (isBusy)
        {
            _logger.Log("loading_overlay_show");
            _loadingOverlay.ShowNearTaskbar();
        }
        else
        {
            _logger.Log("loading_overlay_hide");
            _loadingOverlay.Hide();
        }
    }

    private void ShowSettings()
    {
        try
        {
            if (_dashboardWindow is not null)
            {
                _logger.Log("dashboard_open step=reuse_existing");
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

            _logger.Log("dashboard_open step=construct");
            _dashboardWindow = new DashboardWindow(_settingsStore, _logger);
            _dashboardWindow.Closed += (_, _) => _dashboardWindow = null;
            _logger.Log("dashboard_open step=show");
            _dashboardWindow.Show();
            _logger.Log("dashboard_open step=activate");
            _dashboardWindow.Activate();
            _logger.Log("dashboard_open step=done");
        }
        catch (Exception ex)
        {
            _logger.Log(
                $"dashboard_open_failed error_type={ex.GetType().Name} " +
                $"error=\"{Escape(ex.Message)}\" " +
                $"stack=\"{Escape(ex.ToString())}\"");
            ShowTip("Dashboard failed", "The dashboard could not be opened. Details were written to the native log.");
            _dashboardWindow = null;
        }
    }

    private static string Escape(string? value)
    {
        return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger.Log(
            $"ui_dispatcher_unhandled error_type={e.Exception.GetType().Name} " +
            $"error=\"{Escape(e.Exception.Message)}\" " +
            $"stack=\"{Escape(e.Exception.ToString())}\"");
        _dashboardWindow?.Close();
        _dashboardWindow = null;
        ShowTip("Dashboard failed", "The dashboard hit a UI error. Details were written to the native log.");
        e.Handled = true;
    }

    protected override void ExitThreadCore()
    {
        _logger.Log("stopping");
        Dispatcher.CurrentDispatcher.UnhandledException -= OnDispatcherUnhandledException;
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
