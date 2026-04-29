using System.IO;
using Velopack;
using Velopack.Sources;

namespace UniversalSpellCheck;

internal enum UpdateTrigger
{
    Launch,
    Periodic,
    ManualTray,
    ManualDashboardButton,
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
/// timer, tray "Check for Updates", dashboard "Update Now" — funnels into
/// <see cref="CheckAsync(UpdateTrigger)"/>. There are intentionally no
/// parallel implementations.
/// </summary>
internal sealed class UpdateService : IDisposable
{
    private const string GitHubReleasesUrl = "https://github.com/ElliotDrel/Universal-Spell-Check";
    private static readonly TimeSpan PeriodicInterval = TimeSpan.FromHours(4);

    private readonly DiagnosticsLogger _logger;
    private readonly UpdateManager? _manager;
    private readonly System.Threading.Timer? _timer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private UpdateInfo? _pendingUpdates;
    private UpdateState _state = new UpdateState.Idle();

    public event EventHandler<UpdateState>? StateChanged;
    public event EventHandler<CheckCompletedEventArgs>? CheckCompleted;

    public DateTimeOffset? LastCheckedAt { get; private set; }

    private static string LastCheckedPath => Path.Combine(AppPaths.AppDataDirectory, "last-update-check.txt");

    public UpdateService(DiagnosticsLogger logger)
    {
        _logger = logger;

        if (BuildChannel.IsDev)
        {
            _logger.Log("update_service_init channel=dev mode=disabled");
            _state = new UpdateState.UpToDate();
            return;
        }

        LastCheckedAt = LoadLastCheckedAt();

        try
        {
            var source = new GithubSource(GitHubReleasesUrl, accessToken: null, prerelease: false);
            _manager = new UpdateManager(source);
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

    public async Task CheckAsync(UpdateTrigger trigger)
    {
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

            if (info is null || info.TargetFullRelease is null)
            {
                _logger.Log($"update_check_done trigger={trigger} result=up_to_date");
                _pendingUpdates = null;
                SetState(new UpdateState.UpToDate());
                return;
            }

            var latestVersion = info.TargetFullRelease.Version.ToString();

            // Always converge on latest: if there's a stale pending download
            // for a different version, drop our reference and re-download.
            if (_pendingUpdates is not null &&
                _pendingUpdates.TargetFullRelease.Version != info.TargetFullRelease.Version)
            {
                _logger.Log(
                    $"update_pending_evicted old={_pendingUpdates.TargetFullRelease.Version} " +
                    $"new={latestVersion}");
                _pendingUpdates = null;
            }

            SetState(new UpdateState.Downloading(latestVersion));
            _logger.Log($"update_download_start version={latestVersion}");
            await _manager.DownloadUpdatesAsync(info).ConfigureAwait(false);
            _pendingUpdates = info;
            _logger.Log($"update_download_done version={latestVersion}");

            SetState(new UpdateState.UpdateReady(latestVersion));

            if (trigger == UpdateTrigger.ManualDashboardButton)
            {
                _logger.Log($"update_apply_immediate version={latestVersion}");
                _manager.ApplyUpdatesAndRestart(_pendingUpdates);
            }
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
            LastCheckedAt = DateTimeOffset.Now;
            SaveLastCheckedAt(LastCheckedAt.Value);
            try
            {
                CheckCompleted?.Invoke(this, new CheckCompletedEventArgs(trigger, _state));
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
            Directory.CreateDirectory(AppPaths.AppDataDirectory);
            File.WriteAllText(LastCheckedPath, value.ToString("O"));
        }
        catch (Exception ex)
        {
            _logger.Log(
                $"update_last_checked_save_failed error_type={ex.GetType().Name} " +
                $"error=\"{Escape(ex.Message)}\"");
        }
    }

    public Task ApplyUpdatesAndRestartAsync()
    {
        if (BuildChannel.IsDev || _manager is null || _pendingUpdates is null)
        {
            _logger.Log("update_apply_skipped reason=no_pending");
            return Task.CompletedTask;
        }

        try
        {
            _logger.Log($"update_apply_now version={_pendingUpdates.TargetFullRelease.Version}");
            _manager.ApplyUpdatesAndRestart(_pendingUpdates);
        }
        catch (Exception ex)
        {
            _logger.Log(
                $"update_apply_failed error_type={ex.GetType().Name} " +
                $"error=\"{Escape(ex.Message)}\"");
            SetState(new UpdateState.Failed(ex.Message));
        }

        return Task.CompletedTask;
    }

    private void OnTimerTick(object? _) => _ = CheckAsync(UpdateTrigger.Periodic);

    private void SetState(UpdateState next)
    {
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
        _timer?.Dispose();
        _gate.Dispose();
    }

    private static string Escape(string? value) =>
        (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
}
