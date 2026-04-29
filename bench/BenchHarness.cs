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
    private readonly BenchTargetForm? _form;
    private readonly SpellcheckCoordinator _coordinator;
    private readonly DiagnosticsLogger _logger;
    private readonly int _runs;
    private readonly int _warmup;
    private readonly bool _e2eMode;

    // RunRecord captured by the coordinator's logger sink. The coordinator only
    // exposes timings via its FinalizeAsync log; the cleanest seam is to wrap
    // the logger so we can intercept the spellcheck_detail JSON per trial.
    private volatile TrialTimings? _lastTrialTimings;
    private volatile bool _pasteExpected;
    private long _t1Ticks;

    public BenchHarness(
        BenchTargetForm? form,
        SpellcheckCoordinator coordinator,
        DiagnosticsLogger logger,
        int runs,
        int warmup,
        bool e2eMode)
    {
        _form = form;
        _coordinator = coordinator;
        _logger = logger;
        _runs = runs;
        _warmup = warmup;
        _e2eMode = e2eMode;
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
                await Task.Delay(300);
            }

            for (var i = 0; i < _runs; i++)
            {
                _logger.Log($"bench measured input={name} trial={i + 1}/{_runs}");
                var trial = await RunOneTrialAsync(name, text, trialIndex: i + 1);
                trials.Add(trial);
                await Task.Delay(300);  // let stale clipboard/paste events settle between trials
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
        if (_e2eMode) return await E2eTrialAsync(name, text, trialIndex);
        return await HeadlessTrialAsync(name, text, trialIndex);
    }

    private async Task<TrialResult> E2eTrialAsync(string name, string text, int trialIndex)
    {
        _lastTrialTimings = null;
        _pasteExpected = false;
        _t1Ticks = 0;
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var form = _form ?? throw new InvalidOperationException("E2E mode requires a bench target form.");

        // Subscribe ONCE per trial to the textbox change event.
        void OnChanged(object? _, EventArgs __)
        {
            if (!_pasteExpected) return;   // ignore LoadAndSelect writes before we fire hotkey
            Interlocked.Exchange(ref _t1Ticks, Stopwatch.GetTimestamp());
            ready.TrySetResult(true);
        }

        form.TargetTextChanged += OnChanged;
        try
        {
            // Load text + focus. Pump messages so focus actually lands.
            form.Invoke(() => form.LoadAndSelect(text));
            Application.DoEvents();
            await Task.Delay(50);  // give Windows a tick to settle focus

            _pasteExpected = true;
            var t0 = Stopwatch.GetTimestamp();
            HotkeyInjector.FireCtrlAltB();

            // Wait up to 60s for the textbox to change (cap = HttpClient timeout + slack).
            var winner = await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(60)));

            if (winner != ready.Task)
            {
                return new TrialResult
                {
                    InputName = name,
                    TrialIndex = trialIndex,
                    Success = false,
                    TotalMs = TicksToMs(t0, Stopwatch.GetTimestamp()),
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
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (_lastTrialTimings is null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
            if (_lastTrialTimings is null)
            {
                _logger.Log($"bench warn: FinalizeAsync timings not received within 5s for input={name} trial={trialIndex}");
            }

            var t = _lastTrialTimings;
            return new TrialResult
            {
                InputName = name,
                TrialIndex = trialIndex,
                Success = t?.Status == "success",
                TotalMs = TicksToMs(t0, _t1Ticks),
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
            form.TargetTextChanged -= OnChanged;
        }
    }

    private async Task<TrialResult> HeadlessTrialAsync(string name, string text, int trialIndex)
    {
        _lastTrialTimings = null;
        await _coordinator.RunHeadlessAsync(text);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (_lastTrialTimings is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        if (_lastTrialTimings is null)
        {
            return new TrialResult
            {
                InputName = name,
                TrialIndex = trialIndex,
                Success = false,
                TotalMs = 0,
                CoordinatorTotalMs = 0,
                CaptureMs = 0,
                RequestMs = 0,
                PostProcessMs = 0,
                PasteMs = 0,
                InputTokens = 0,
                OutputTokens = 0,
                CachedTokens = 0,
                Error = "Timed out waiting for headless timings.",
            };
        }

        var t = _lastTrialTimings;
        return new TrialResult
        {
            InputName = name,
            TrialIndex = trialIndex,
            Success = t.Status == "success",
            TotalMs = t.TotalMs,
            CoordinatorTotalMs = t.TotalMs,
            CaptureMs = 0,
            RequestMs = t.RequestMs,
            PostProcessMs = t.ReplacementsMs + t.PromptGuardMs,
            PasteMs = 0,
            InputTokens = t.InputTokens,
            OutputTokens = t.OutputTokens,
            CachedTokens = t.CachedTokens,
            Error = t.ErrorMessage,
        };
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
