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
    private readonly CachedSettings _cachedSettings;
    private readonly OpenAiSpellcheckService _spellcheckService;
    private readonly TextPostProcessor _postProcessor;
    private readonly OverlayHost _overlayHost = new();
    private readonly UpdateService _updateService;
    private DashboardWindow? _dashboardWindow;
    private Forms.ToolStripMenuItem _versionItem = null!;
    private Forms.ToolStripMenuItem _lastCheckedItem = null!;
    private Forms.ToolStripMenuItem _checkForUpdatesItem = null!;
    private Forms.ToolStripMenuItem _updateNowItem = null!;

    public SpellCheckAppContext()
    {
        _logger = new DiagnosticsLogger(AppPaths.LogPath);
        Dispatcher.CurrentDispatcher.UnhandledException += OnDispatcherUnhandledException;
        _settingsStore = new SettingsStore(_logger);
        _cachedSettings = new CachedSettings(_settingsStore);
        _spellcheckService = new OpenAiSpellcheckService(_cachedSettings, _logger);
        _postProcessor = new TextPostProcessor(_logger);
        // Pre-warm the HTTPS connection (DNS+TCP+TLS+H2) off-thread so the
        // first hotkey press doesn't pay handshake cost. Re-warms every 4 min.
        _spellcheckService.StartConnectionWarmer();
        _coordinator = new SpellcheckCoordinator(
            _logger,
            _spellcheckService,
            _postProcessor,
            ShowTip,
            SetBusy,
            ShowSettings);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = BuildTrayIcon(),
            Text = TruncateTooltip(BuildChannel.TrayTooltip),
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };

        _hotkeyWindow = new HotkeyWindow();
        _hotkeyWindow.HotkeyPressed += OnHotkeyPressed;
        _hotkeyWindow.Register(BuildChannel.HotkeyModifiers, BuildChannel.HotkeyVk);

        _updateService = new UpdateService(_logger);
        _updateService.StateChanged += OnUpdateStateChanged;
        _updateService.CheckCompleted += OnUpdateCheckCompleted;
        OnUpdateStateChanged(_updateService, _updateService.State);
        _ = _updateService.CheckAsync(UpdateTrigger.Launch);

        StartupRegistration.EnsureFirstRunRegistered(_logger);

        _logger.Log(
            $"started channel={BuildChannel.ChannelName} version={BuildChannel.AppVersion} " +
            $"hotkey_vk=0x{BuildChannel.HotkeyVk:X2}");

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
        _versionItem = new Forms.ToolStripMenuItem($"v{BuildChannel.AppVersion}") { Enabled = false };
        _lastCheckedItem = new Forms.ToolStripMenuItem("Last checked: never")
        {
            Enabled = false,
            Visible = !BuildChannel.IsDev,
        };
        _checkForUpdatesItem = new Forms.ToolStripMenuItem(
            "Check for Updates",
            null,
            (_, _) => _ = _updateService.CheckAsync(UpdateTrigger.ManualTray));
        _updateNowItem = new Forms.ToolStripMenuItem(
            "Update Now",
            null,
            (_, _) => _ = _updateService.ApplyUpdatesAndRestartAsync()) { Visible = false };

        menu.Items.Add(_versionItem);
        menu.Items.Add(_lastCheckedItem);
        menu.Items.Add(_checkForUpdatesItem);
        menu.Items.Add(_updateNowItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Open Dashboard", null, (_, _) => ShowSettings());
        menu.Items.Add("Open Logs Folder", null, (_, _) => OpenLogsFolder());
        menu.Items.Add("Quit", null, (_, _) => ExitThread());

        // Refresh the relative timestamp each time the menu opens so it does
        // not look stale after the app has been idle.
        menu.Opening += (_, _) => RefreshLastCheckedLabel();
        return menu;
    }

    private void RefreshLastCheckedLabel()
    {
        if (_updateService.State is UpdateState.Checking)
        {
            _lastCheckedItem.Text = "Checking for updates…";
            return;
        }

        _lastCheckedItem.Text = "Last checked: " + FormatLastChecked(_updateService.LastCheckedAt);
    }

    private static string FormatLastChecked(DateTimeOffset? when)
    {
        if (when is null) return "never";
        var delta = DateTimeOffset.Now - when.Value;
        if (delta.TotalSeconds < 60) return "just now";
        if (delta.TotalMinutes < 60)
        {
            var m = (int)delta.TotalMinutes;
            return $"{m} minute{(m == 1 ? "" : "s")} ago";
        }
        if (delta.TotalHours < 24)
        {
            var h = (int)delta.TotalHours;
            return $"{h} hour{(h == 1 ? "" : "s")} ago";
        }
        if (delta.TotalDays < 7)
        {
            var d = (int)delta.TotalDays;
            return $"{d} day{(d == 1 ? "" : "s")} ago";
        }
        return when.Value.LocalDateTime.ToString("yyyy-MM-dd");
    }

    private void OnUpdateCheckCompleted(object? sender, CheckCompletedEventArgs e)
    {
        // Marshal to UI thread for ShowBalloonTip.
        if (_notifyIcon.ContextMenuStrip is { } menu && menu.InvokeRequired)
        {
            menu.BeginInvoke(new Action(() => OnUpdateCheckCompleted(sender, e)));
            return;
        }

        if (e.Trigger != UpdateTrigger.ManualTray) return;

        switch (e.Result)
        {
            case UpdateState.UpToDate:
                ShowTip("Up to date", $"You're on the latest version (v{BuildChannel.AppVersion}).");
                break;
            case UpdateState.UpdateReady ready:
                ShowTip("Update available", $"v{ready.Version} is ready. Click 'Update Now' in the tray menu to install.");
                break;
            case UpdateState.Downloading dl:
                ShowTip("Downloading update", $"Fetching v{dl.Version}…");
                break;
            case UpdateState.Failed failed:
                ShowTip("Update check failed", failed.Reason);
                break;
        }
    }

    private void OnUpdateStateChanged(object? sender, UpdateState state)
    {
        // UpdateService may raise from a background thread; marshal to UI thread.
        if (_notifyIcon.ContextMenuStrip is { } menu && menu.InvokeRequired)
        {
            menu.BeginInvoke(new Action(() => OnUpdateStateChanged(sender, state)));
            return;
        }

        switch (state)
        {
            case UpdateState.UpdateReady ready:
                _versionItem.Text = $"v{BuildChannel.AppVersion} — Update available ({ready.Version})";
                _updateNowItem.Visible = true;
                _checkForUpdatesItem.Enabled = true;
                break;
            case UpdateState.Checking:
                _versionItem.Text = $"v{BuildChannel.AppVersion} — Checking…";
                _updateNowItem.Visible = false;
                _checkForUpdatesItem.Enabled = false;
                break;
            case UpdateState.Downloading dl:
                _versionItem.Text = $"v{BuildChannel.AppVersion} — Downloading {dl.Version}…";
                _updateNowItem.Visible = false;
                _checkForUpdatesItem.Enabled = false;
                break;
            case UpdateState.Failed:
            case UpdateState.UpToDate:
            case UpdateState.Idle:
            default:
                _versionItem.Text = $"v{BuildChannel.AppVersion}";
                _updateNowItem.Visible = false;
                _checkForUpdatesItem.Enabled = true;
                break;
        }

        RefreshLastCheckedLabel();
    }

    private static System.Drawing.Icon BuildTrayIcon()
    {
        var baseIcon = System.Drawing.SystemIcons.Application;
        if (!BuildChannel.IsDev)
        {
            return baseIcon;
        }

        // Tint the Dev icon orange so it is visually distinct from Prod when
        // both run side-by-side in the tray.
        try
        {
            using var bmp = baseIcon.ToBitmap();
            using var tinted = new System.Drawing.Bitmap(bmp.Width, bmp.Height);
            using (var g = System.Drawing.Graphics.FromImage(tinted))
            {
                g.DrawImage(bmp, 0, 0);
                using var overlay = new System.Drawing.SolidBrush(
                    System.Drawing.Color.FromArgb(120, 255, 140, 0));
                g.FillRectangle(overlay, 0, 0, bmp.Width, bmp.Height);
            }
            var hIcon = tinted.GetHicon();
            return System.Drawing.Icon.FromHandle(hIcon);
        }
        catch
        {
            return baseIcon;
        }
    }

    private static string TruncateTooltip(string text)
    {
        // NotifyIcon.Text has a 63-char limit before .NET throws.
        return text.Length > 63 ? text[..63] : text;
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
        try { _notifyIcon.Text = TruncateTooltip(isBusy
            ? $"{BuildChannel.TrayTooltip} — checking"
            : BuildChannel.TrayTooltip); } catch { /* tooltip is cosmetic */ }

        // OverlayHost owns its own STA background thread, so Show/Hide just
        // enqueue onto that thread's message loop and return immediately —
        // never blocks the spellcheck hot path.
        if (isBusy)
        {
            _overlayHost.Show();
        }
        else
        {
            _overlayHost.Hide();
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
            _dashboardWindow = new DashboardWindow(_settingsStore, _logger, _updateService);
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

    internal UpdateService UpdateService => _updateService;

    protected override void ExitThreadCore()
    {
        _logger.Log("stopping");
        Dispatcher.CurrentDispatcher.UnhandledException -= OnDispatcherUnhandledException;
        _hotkeyWindow.HotkeyPressed -= OnHotkeyPressed;
        _hotkeyWindow.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _dashboardWindow?.Close();
        _overlayHost.Dispose();
        _coordinator.Dispose();
        _spellcheckService.Dispose();
        _updateService.StateChanged -= OnUpdateStateChanged;
        _updateService.CheckCompleted -= OnUpdateCheckCompleted;
        _updateService.Dispose();
        base.ExitThreadCore();
    }
}
