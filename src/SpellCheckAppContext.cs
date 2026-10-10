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
    private readonly TargetFormattingPipeline _formattingPipeline;
    private readonly OverlayHost _overlayHost = new();
    private readonly UpdateService _updateService;
    private readonly Action<string, string> _notify;
    private readonly Func<Task> _runCorrection;
    private readonly Action _exit;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private DashboardWindow? _dashboardWindow;
    private Forms.ToolStripMenuItem _versionItem = null!;
    private Forms.ToolStripMenuItem _checkForUpdatesItem = null!;
    private bool _restarting;
    private bool _startupChecking;
    private Task _activeCorrection = Task.CompletedTask;

    public SpellCheckAppContext(bool restartRequested = false)
    {
        _logger = new DiagnosticsLogger(() => AppPaths.LogPath);
        Dispatcher.CurrentDispatcher.UnhandledException += OnDispatcherUnhandledException;
        _settingsStore = new SettingsStore(_logger);
        _cachedSettings = new CachedSettings(_settingsStore);
        _spellcheckService = new OpenAiSpellcheckService(_cachedSettings, _logger);
        _postProcessor = new TextPostProcessor(_logger);
        _formattingPipeline = new TargetFormattingPipeline();
        // Pre-warm the HTTPS connection (DNS+TCP+TLS+H2) off-thread so the
        // first hotkey press doesn't pay handshake cost. Re-warms every 4 min.
        _spellcheckService.StartConnectionWarmer();
        _coordinator = new SpellcheckCoordinator(
            _logger,
            _spellcheckService,
            _postProcessor,
            _formattingPipeline,
            ShowTip,
            SetPhase,
            ShowSettings,
            // Owner window for the clipboard-history exclusion. Lazy because the
            // hotkey window is created just below, after the coordinator.
            () => _hotkeyWindow.Handle,
            () => _cachedSettings.DeveloperLogging);

        // Must exist before BuildMenu(): the version line dereferences
        // _updateService.State. Constructing it here only needs _logger; the
        // event wiring + initial check stay below, after _versionItem exists.
        _updateService = new UpdateService(_logger);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = BuildTrayIcon(),
            Text = TruncateTooltip(BuildChannel.TrayTooltip),
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };

        _notify = (title, message) => _notifyIcon.ShowBalloonTip(2500, title, message, Forms.ToolTipIcon.Info);
        _runCorrection = _coordinator.RunAsync;
        _exit = ExitThread;

        _hotkeyWindow = new HotkeyWindow();
        _hotkeyWindow.HotkeyPressed += OnHotkeyPressed;
        _hotkeyWindow.RestartRequested += OnCommandRestartRequested;
        _hotkeyWindow.Register(BuildChannel.HotkeyModifiers, BuildChannel.HotkeyVk);

        _updateService.RestartRequested += OnRestartRequested;
        _updateService.StateChanged += OnUpdateStateChanged;
        _updateService.CheckCompleted += OnUpdateCheckCompleted;
        OnUpdateStateChanged(_updateService, _updateService.State);
        _startupChecking = _updateService.CanUpdate;
        if (restartRequested) ShowRestartNotice();
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
        _versionItem = new Forms.ToolStripMenuItem(BuildVersionLine()) { Enabled = false };
        _checkForUpdatesItem = new Forms.ToolStripMenuItem(
            "Check for Updates",
            null,
            (_, _) => _ = _updateService.CheckAsync(UpdateTrigger.ManualTray));
        menu.Items.Add(_versionItem);
        menu.Items.Add(_checkForUpdatesItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Restart", null, (_, _) => _updateService.RequestRestart())
        { Enabled = _updateService.CanUpdate });
        menu.Items.Add("Open Dashboard", null, (_, _) => ShowSettings());
        menu.Items.Add("Open Logs Folder", null, (_, _) => OpenLogsFolder());
        menu.Items.Add("Quit", null, (_, _) => ExitThread());

        // Refresh the relative timestamp each time the menu opens so it does
        // not look stale after the app has been idle.
        menu.Opening += (_, _) => _versionItem.Text = BuildVersionLine();
        return menu;
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

    private string BuildVersionLine()
    {
        if (_updateService.State is UpdateState.Checking)
            return $"v{BuildChannel.AppVersion} · Checking…";
        if (_updateService.State is UpdateState.Downloading downloading)
            return $"v{BuildChannel.AppVersion} · Downloading {downloading.Version}…";
        if (_updateService.State is UpdateState.UpdateReady ready)
            return $"v{BuildChannel.AppVersion} · {ready.Version} ready";
        return BuildChannel.IsDev
            ? $"v{BuildChannel.AppVersion}"
            : $"v{BuildChannel.AppVersion} · Checked {FormatLastChecked(_updateService.LastCheckedAt)}";
    }

    private void ShowRestartNotice()
    {
        ShowTip("Restarting", "Restarting and checking for updates. Please wait before using the app.");
        _logger.Log("restart_notification");
    }

    private void OnCommandRestartRequested(object? sender, EventArgs e)
    {
        _logger.Log("restart_command_received");
        _updateService.RequestRestart();
    }

    private void OnRestartRequested(bool applyUpdate)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(() => OnRestartRequested(applyUpdate)));
            return;
        }
        if (_restarting) return;
        if (!applyUpdate) ShowRestartNotice();
        _ = RestartAsync(applyUpdate);
    }

    private async Task RestartAsync(bool applyUpdate)
    {
        if (_restarting) return;
        _restarting = true;
        try
        {
            // Let the acknowledgement and notification reach Windows, and finish
            // any active paste before releasing the mutex and restarting.
            await Task.Delay(1000);
            try { await _activeCorrection; }
            catch (Exception ex)
            {
                _logger.Log($"restart_after_correction_failure error=\"{Escape(ex.Message)}\"");
            }
            if (_updateService.PrepareRestart(applyUpdate))
            {
                _exit();
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"restart_failed error=\"{Escape(ex.Message)}\"");
        }
        _restarting = false;
        ShowTip("Restart failed", "The app could not restart. The current version is still running.");
    }

    private void OnUpdateCheckCompleted(object? sender, CheckCompletedEventArgs e)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(() => OnUpdateCheckCompleted(sender, e)));
            return;
        }

        if (e.Trigger == UpdateTrigger.Launch)
        {
            _startupChecking = false;
            return;
        }

        if (!_restarting && e.Trigger is (UpdateTrigger.ManualTray or UpdateTrigger.ManualDashboard)
            && e.Result is UpdateState.UpToDate && e.Result == _updateService.State)
            ShowTip("Up to date", $"You're on the latest version (v{BuildChannel.AppVersion}).");
    }

    private void OnUpdateStateChanged(object? sender, UpdateState state)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(() => OnUpdateStateChanged(sender, state)));
            return;
        }

        switch (state)
        {
            case UpdateState.UpdateReady:
                _checkForUpdatesItem.Enabled = true;
                break;
            case UpdateState.Checking:
                _checkForUpdatesItem.Enabled = false;
                break;
            case UpdateState.Downloading:
                _checkForUpdatesItem.Enabled = false;
                break;
            case UpdateState.Failed:
            case UpdateState.UpToDate:
            case UpdateState.Idle:
            default:
                _checkForUpdatesItem.Enabled = true;
                break;
        }

        _versionItem.Text = BuildVersionLine();
        if (!_restarting && state is UpdateState.Failed)
            ShowTip("Update failed", "Could not check or install the update. The current version is still available; try restarting later.");
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
        if (_restarting || _startupChecking || !_activeCorrection.IsCompleted)
        {
            _logger.Log($"guard_rejected reason={(_restarting || _startupChecking ? "updating" : "already_running")}");
            return;
        }
        _activeCorrection = _runCorrection();
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
        _notify(title, message);
    }

    private void SetPhase(SpellcheckPhase phase)
    {
        try
        {
            _notifyIcon.Text = TruncateTooltip(phase == SpellcheckPhase.Done
                ? BuildChannel.TrayTooltip
                : $"{BuildChannel.TrayTooltip} — checking");
        }
        catch
        {
            // tooltip is cosmetic
        }

        // OverlayHost owns its own STA background thread, so SetPhase just
        // enqueues onto that thread's message loop and returns immediately —
        // never blocks the spellcheck hot path.
        _overlayHost.SetPhase(phase);
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
        _hotkeyWindow.RestartRequested -= OnCommandRestartRequested;
        _hotkeyWindow.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _dashboardWindow?.Close();
        _overlayHost.Dispose();
        _coordinator.Dispose();
        _spellcheckService.Dispose();
        _updateService.RestartRequested -= OnRestartRequested;
        _updateService.StateChanged -= OnUpdateStateChanged;
        _updateService.CheckCompleted -= OnUpdateCheckCompleted;
        _updateService.Dispose();
        base.ExitThreadCore();
    }
}
