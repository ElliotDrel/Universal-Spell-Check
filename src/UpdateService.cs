using System.IO;
using Velopack;
using Velopack.Sources;
using Velopack.Locators;

namespace UniversalSpellCheck;

internal enum UpdateTrigger
{
    Launch,
    Periodic,
    ManualTray,
    ManualDashboard,
}

internal sealed record CheckCompletedEventArgs(UpdateTrigger Trigger, UpdateState Result);

internal abstract record UpdateState
{
    public sealed record Idle : UpdateState;
    public sealed record Checking : UpdateState;
    public sealed record Downloading(string Version) : UpdateState;
    public sealed record UpdateReady(string Version) : UpdateState;
    public sealed record UpToDate : UpdateState;
    public sealed record Failed(string Reason) : UpdateState;
}

/// <summary>
/// Single, unified update flow. Every UI affordance — launch check, periodic
/// timer, tray check, and dashboard checks — funnels into
/// <see cref="CheckAsync(UpdateTrigger)"/>. There are intentionally no
/// parallel implementations.
/// </summary>
internal sealed class UpdateService : IDisposable
{
    private const string GitHubReleasesUrl = "https://github.com/ElliotDrel/Universal-Spell-Check";
    private static readonly TimeSpan PeriodicInterval = TimeSpan.FromHours(4);

    private readonly DiagnosticsLogger _logger;
    private readonly string _stateDirectory;
    private readonly UpdateManager? _manager;
    private readonly System.Threading.Timer? _timer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private volatile bool _disposed;

    private UpdateInfo? _pendingUpdates;
    private UpdateState _state = new UpdateState.Idle();

    public event Action<bool>? RestartRequested;
    public event EventHandler<UpdateState>? StateChanged;
    public event EventHandler<CheckCompletedEventArgs>? CheckCompleted;

    public DateTimeOffset? LastCheckedAt { get; private set; }
    public DateTimeOffset? LastUpdatedAt { get; private set; }

    private string LastCheckedPath => Path.Combine(_stateDirectory, "last-update-check.txt");
    private string InstalledVersionPath => Path.Combine(_stateDirectory, "installed-version.txt");

    public UpdateService(DiagnosticsLogger logger, UpdateManager? manager = null, string? stateDirectory = null)
    {
        _logger = logger;
        _stateDirectory = stateDirectory ?? AppPaths.AppDataDirectory;

        if (BuildChannel.IsDev)
        {
            _logger.Log("update_service_init channel=dev mode=disabled");
            _state = new UpdateState.UpToDate();
            return;
        }

        LastCheckedAt = LoadLastCheckedAt();
        LastUpdatedAt = LoadOrRecordInstalledVersion();

        try
        {
            var source = new GithubSource(GitHubReleasesUrl, accessToken: null, prerelease: false);
            _manager = manager ?? new UpdateManager(source);
            _logger.Log($"update_service_init channel=prod source=\"{GitHubReleasesUrl}\"");
        }
        catch (Exception ex)
        {
            _logger.Log(
                $"update_service_init_failed error_type={ex.GetType().Name} " +
                $"error=\"{Escape(ex.Message)}\"");
            _state = new UpdateState.Failed(ex.Message);
            return;
        }

        _timer = new System.Threading.Timer(OnTimerTick, null, PeriodicInterval, PeriodicInterval);
    }

    public UpdateState State => _state;
    public bool CanUpdate => !BuildChannel.IsDev && _manager?.IsInstalled == true;

    public void RequestRestart() => RestartRequested?.Invoke(false);

    public async Task CheckAsync(UpdateTrigger trigger)
    {
        if (_disposed) return;
        var shutdownToken = _shutdown.Token;
        if (BuildChannel.IsDev || _manager is null)
        {
            _logger.Log($"update_check_skipped trigger={trigger} reason=dev_or_uninstalled");
            return;
        }

        if (!_manager.IsInstalled)
        {
            _logger.Log($"update_check_skipped trigger={trigger} reason=not_installed");
            return;
        }

        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
        {
            _logger.Log($"update_check_skipped trigger={trigger} reason=in_progress");
            return;
        }

        try
        {
            SetState(new UpdateState.Checking());
            _logger.Log($"update_check_start trigger={trigger}");
            var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            shutdownToken.ThrowIfCancellationRequested();

            if (info is null || info.TargetFullRelease is null)
            {
                _logger.Log($"update_check_done trigger={trigger} result=up_to_date");
                _pendingUpdates = null;
                SetState(new UpdateState.UpToDate());
                return;
            }

            var latestVersion = info.TargetFullRelease.Version.ToString();

            _pendingUpdates = null;

            SetState(new UpdateState.Downloading(latestVersion));
            _logger.Log($"update_download_start version={latestVersion}");
            await _manager.DownloadUpdatesAsync(info, cancelToken: shutdownToken).ConfigureAwait(false);
            shutdownToken.ThrowIfCancellationRequested();
            _pendingUpdates = info;
            _logger.Log($"update_download_done version={latestVersion}");

            SetState(new UpdateState.UpdateReady(latestVersion));

        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
            _logger.Log($"update_check_cancelled trigger={trigger} reason=shutdown");
        }
        catch (Exception ex)
        {
            _logger.Log(
                $"update_check_failed trigger={trigger} error_type={ex.GetType().Name} " +
                $"error=\"{Escape(ex.Message)}\"");
            SetState(new UpdateState.Failed(ex.Message));
        }
        finally
        {
            if (!_disposed)
            {
                LastCheckedAt = DateTimeOffset.Now;
                SaveLastCheckedAt(LastCheckedAt.Value);
            }
            try
            {
                if (!_disposed && trigger == UpdateTrigger.Launch && _state is UpdateState.UpdateReady)
                    RestartRequested?.Invoke(true);
                if (!_disposed) CheckCompleted?.Invoke(this, new CheckCompletedEventArgs(trigger, _state));
            }
            catch (Exception ex)
            {
                _logger.Log(
                    $"update_check_completed_subscriber_failed error_type={ex.GetType().Name} " +
                    $"error=\"{Escape(ex.Message)}\"");
            }
            _gate.Release();
        }
    }

    private DateTimeOffset? LoadLastCheckedAt()
    {
        try
        {
            if (!File.Exists(LastCheckedPath)) return null;
            var raw = File.ReadAllText(LastCheckedPath).Trim();
            return DateTimeOffset.TryParse(raw, out var parsed) ? parsed : (DateTimeOffset?)null;
        }
        catch (Exception ex)
        {
            _logger.Log(
                $"update_last_checked_load_failed error_type={ex.GetType().Name} " +
                $"error=\"{Escape(ex.Message)}\"");
            return null;
        }
    }

    private void SaveLastCheckedAt(DateTimeOffset value)
    {
        try
        {
            Directory.CreateDirectory(_stateDirectory);
            File.WriteAllText(LastCheckedPath, value.ToString("O"));
        }
        catch (Exception ex)
        {
            _logger.Log(
                $"update_last_checked_save_failed error_type={ex.GetType().Name} " +
                $"error=\"{Escape(ex.Message)}\"");
        }
    }

    private DateTimeOffset? LoadOrRecordInstalledVersion()
    {
        try
        {
            Directory.CreateDirectory(_stateDirectory);
            if (File.Exists(InstalledVersionPath))
            {
                var lines = File.ReadAllLines(InstalledVersionPath);
                if (lines.Length >= 2 && lines[0] == BuildChannel.AppVersion &&
                    DateTimeOffset.TryParse(lines[1], out var installedAt))
                {
                    return installedAt;
                }
            }

            var now = DateTimeOffset.Now;
            File.WriteAllLines(InstalledVersionPath, [BuildChannel.AppVersion, now.ToString("O")]);
            return now;
        }
        catch (Exception ex)
        {
            _logger.Log(
                $"update_installed_version_save_failed error_type={ex.GetType().Name} " +
                $"error=\"{Escape(ex.Message)}\"");
            return null;
        }
    }

    public bool PrepareRestart(bool applyUpdate)
    {
        try
        {
            if (!CanUpdate)
                throw new InvalidOperationException("Restart updates require an installed production app.");

            if (applyUpdate)
            {
                if (_pendingUpdates is null)
                    throw new InvalidOperationException("No verified update is ready to install.");
                _logger.Log($"update_apply_now version={_pendingUpdates.TargetFullRelease.Version}");
                _manager!.WaitExitThenApplyUpdates(_pendingUpdates, silent: true, restart: true, restartArgs: []);
            }
            else
            {
                _logger.Log("restart_requested");
                UpdateExe.Start(VelopackLocator.Current, (uint)Environment.ProcessId, []);
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.Log($"restart_prepare_failed error=\"{Escape(ex.Message)}\"");
            SetState(new UpdateState.Failed(ex.Message));
            return false;
        }
    }

    private void OnTimerTick(object? _) => _ = CheckAsync(UpdateTrigger.Periodic);

    private void SetState(UpdateState next)
    {
        if (_disposed) return;
        _state = next;
        try
        {
            StateChanged?.Invoke(this, next);
        }
        catch (Exception ex)
        {
            _logger.Log(
                $"update_state_subscriber_failed error_type={ex.GetType().Name} " +
                $"error=\"{Escape(ex.Message)}\"");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
        _shutdown.Cancel();
        // Async checks can still unwind; never dispose their gate or token
        // source underneath them. Neither uses unmanaged wait handles here.
    }

    private static string Escape(string? value) =>
        (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
}
