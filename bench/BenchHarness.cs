using System.Diagnostics;
using System.Windows.Forms;

namespace UniversalSpellCheck.Bench;

internal sealed class TrialResult
{
    public required string InputName { get; init; }
    public required int TrialIndex { get; init; }
    public required bool Success { get; init; }
    public required long TotalMs { get; init; }              // hotkey-press → TextChanged
    public required long CoordinatorTotalMs { get; init; }   // RunRecord total (hotkey → HotPathReturned)
    public required long CaptureMs { get; init; }
    public required long RequestMs { get; init; }
    public required long PostProcessMs { get; init; }
    public required long PasteMs { get; init; }
    public required int InputTokens { get; init; }
    public required int OutputTokens { get; init; }
    public required int CachedTokens { get; init; }
    public string? Error { get; init; }
}

internal sealed class InputResult
{
    public required string Name { get; init; }
    public required int InputChars { get; init; }
    public required List<TrialResult> Trials { get; init; }
}

internal sealed class BenchHarness
{
    private readonly BenchTargetForm _form;
    private readonly SpellcheckCoordinator _coordinator;
    private readonly DiagnosticsLogger _logger;
    private readonly int _runs;
    private readonly int _warmup;

    // RunRecord captured by the coordinator's logger sink. The coordinator only
    // exposes timings via its FinalizeAsync log; the cleanest seam is to wrap
    // the logger so we can intercept the spellcheck_detail JSON per trial.
    private TrialTimings? _lastTrialTimings;

    public BenchHarness(
        BenchTargetForm form,
        SpellcheckCoordinator coordinator,
        DiagnosticsLogger logger,
        int runs,
        int warmup)
    {
        _form = form;
        _coordinator = coordinator;
        _logger = logger;
        _runs = runs;
        _warmup = warmup;
    }

    /// <summary>Set by Program.cs after each Coordinator.RunAsync completes.</summary>
    public void RecordCoordinatorTimings(TrialTimings timings) => _lastTrialTimings = timings;

    public async Task<List<InputResult>> RunAllAsync(IReadOnlyList<(string Name, string Text)> inputs)
    {
        var results = new List<InputResult>();

        foreach (var (name, text) in inputs)
        {
            var trials = new List<TrialResult>();

            for (var w = 0; w < _warmup; w++)
            {
                _logger.Log($"bench warmup input={name} trial={w + 1}/{_warmup}");
                _ = await RunOneTrialAsync(name, text, trialIndex: -(w + 1));
            }

            for (var i = 0; i < _runs; i++)
            {
                _logger.Log($"bench measured input={name} trial={i + 1}/{_runs}");
                var trial = await RunOneTrialAsync(name, text, trialIndex: i + 1);
                trials.Add(trial);
            }

            results.Add(new InputResult
            {
                Name = name,
                InputChars = text.Length,
                Trials = trials,
            });
        }

        return results;
    }

    private async Task<TrialResult> RunOneTrialAsync(string name, string text, int trialIndex)
    {
        _lastTrialTimings = null;
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Subscribe ONCE per trial to the textbox change event.
        void OnChanged(object? _, EventArgs __)
        {
            if (string.Equals(_form.CurrentText, text, StringComparison.Ordinal))
            {
                // The change is from our own LoadAndSelect call; ignore.
                return;
            }
            ready.TrySetResult(true);
        }

        _form.TargetTextChanged += OnChanged;
        try
        {
            // Load text + focus. Pump messages so focus actually lands.
            _form.Invoke(() => _form.LoadAndSelect(text));
            Application.DoEvents();
            await Task.Delay(50);  // give Windows a tick to settle focus

            var t0 = Stopwatch.GetTimestamp();
            HotkeyInjector.FireCtrlAltB();

            // Wait up to 60s for the textbox to change (cap = HttpClient timeout + slack).
            var winner = await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(60)));
            var t1 = Stopwatch.GetTimestamp();

            if (winner != ready.Task)
            {
                return new TrialResult
                {
                    InputName = name,
                    TrialIndex = trialIndex,
                    Success = false,
                    TotalMs = TicksToMs(t0, t1),
                    CoordinatorTotalMs = 0,
                    CaptureMs = 0,
                    RequestMs = 0,
                    PostProcessMs = 0,
                    PasteMs = 0,
                    InputTokens = 0,
                    OutputTokens = 0,
                    CachedTokens = 0,
                    Error = "Timed out waiting for paste TextChanged.",
                };
            }

            // Coordinator finalize runs as Task.Run after RunAsync returns; wait briefly
            // for the timings sink to receive the spellcheck_detail event.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (_lastTrialTimings is null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            var t = _lastTrialTimings;
            return new TrialResult
            {
                InputName = name,
                TrialIndex = trialIndex,
                Success = t?.Status == "success",
                TotalMs = TicksToMs(t0, t1),
                CoordinatorTotalMs = t?.TotalMs ?? 0,
                CaptureMs = t?.ClipboardMs ?? 0,
                RequestMs = t?.RequestMs ?? 0,
                PostProcessMs = (t?.ReplacementsMs ?? 0) + (t?.PromptGuardMs ?? 0),
                PasteMs = t?.PasteMs ?? 0,
                InputTokens = t?.InputTokens ?? 0,
                OutputTokens = t?.OutputTokens ?? 0,
                CachedTokens = t?.CachedTokens ?? 0,
                Error = t?.ErrorMessage,
            };
        }
        finally
        {
            _form.TargetTextChanged -= OnChanged;
        }
    }

    private static long TicksToMs(long start, long end)
    {
        if (start == 0 || end == 0 || end <= start) return 0;
        return (long)((end - start) * 1000.0 / Stopwatch.Frequency);
    }
}

/// <summary>Coordinator phase timings captured per trial via a logger interceptor.</summary>
internal sealed class TrialTimings
{
    public required string Status { get; init; }
    public required long TotalMs { get; init; }
    public required long ClipboardMs { get; init; }
    public required long RequestMs { get; init; }
    public required long ReplacementsMs { get; init; }
    public required long PromptGuardMs { get; init; }
    public required long PasteMs { get; init; }
    public required int InputTokens { get; init; }
    public required int OutputTokens { get; init; }
    public required int CachedTokens { get; init; }
    public string? ErrorMessage { get; init; }
}
