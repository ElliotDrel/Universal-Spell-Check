using System.Diagnostics;

namespace UniversalSpellCheck;

internal sealed class SpellCheckAppContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly HotkeyWindow _hotkeyWindow;
    private readonly DiagnosticsLogger _logger;
    private readonly SpellcheckCoordinator _coordinator;
    private readonly SettingsStore _settingsStore;
    private readonly OpenAiSpellcheckService _spellcheckService;
    private readonly TextPostProcessor _postProcessor;
    private readonly LoadingOverlayForm _loadingOverlay = new();
    private SettingsForm? _settingsForm;

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

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Universal Spell Check Native Spike",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };

        _hotkeyWindow = new HotkeyWindow();
        _hotkeyWindow.HotkeyPressed += OnHotkeyPressed;
        _hotkeyWindow.Register();

        _logger.Log("started hotkey=Ctrl+Alt+Y model=gpt-4.1 phase=5");
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Settings", null, (_, _) => ShowSettings());
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
        _notifyIcon.ShowBalloonTip(2500, title, message, ToolTipIcon.Info);
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
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }

        _settingsForm = new SettingsForm(_settingsStore, _logger);
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    protected override void ExitThreadCore()
    {
        _logger.Log("stopping");
        _hotkeyWindow.HotkeyPressed -= OnHotkeyPressed;
        _hotkeyWindow.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _loadingOverlay.Dispose();
        _coordinator.Dispose();
        _spellcheckService.Dispose();
        base.ExitThreadCore();
    }
}
