using System.Diagnostics;

namespace UniversalSpellCheck;

internal sealed class SpellcheckCoordinator : IDisposable
{
    private readonly DiagnosticsLogger _logger;
    private readonly OpenAiSpellcheckService _spellcheckService;
    private readonly TextPostProcessor _postProcessor;
    private readonly Action<string, string> _notify;
    private readonly Action<bool> _setBusy;
    private readonly Action _showSettings;
    private readonly SemaphoreSlim _spellcheckGate = new(1, 1);

    public SpellcheckCoordinator(
        DiagnosticsLogger logger,
        OpenAiSpellcheckService spellcheckService,
        TextPostProcessor postProcessor,
        Action<string, string> notify,
        Action<bool> setBusy,
        Action showSettings)
    {
        _logger = logger;
        _spellcheckService = spellcheckService;
        _postProcessor = postProcessor;
        _notify = notify;
        _setBusy = setBusy;
        _showSettings = showSettings;
    }

    public async Task RunAsync()
    {
        if (!await _spellcheckGate.WaitAsync(0))
        {
            _logger.Log("guard_rejected reason=already_running");
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        IDataObject? originalClipboard = null;
        var activeWindow = ActiveWindowInfo.Capture();

        try
        {
            _setBusy(true);
            _logger.Log(
                "run_started " +
                $"active_process=\"{Escape(activeWindow.ProcessName)}\" " +
                $"window_title=\"{Escape(activeWindow.WindowTitle)}\"");

            originalClipboard = Clipboard.GetDataObject();

            var capture = await ClipboardLoop.CaptureSelectionAsync();
            if (!capture.Success)
            {
                ClipboardLoop.RestoreClipboard(originalClipboard);
                _notify("No selected text", capture.FailureReason ?? "Select text and press Ctrl+Alt+Y.");
                _logger.Log(
                    $"capture_failed duration_ms={stopwatch.ElapsedMilliseconds} " +
                    $"capture_duration_ms={capture.DurationMs} " +
                    $"copy_attempts={capture.Attempts} " +
                    $"active_process=\"{Escape(activeWindow.ProcessName)}\" " +
                    $"reason=\"{capture.FailureReason}\"");
                return;
            }

            _logger.Log(
                "capture_succeeded " +
                $"input_len={capture.Text!.Length} " +
                $"capture_duration_ms={capture.DurationMs} " +
                $"copy_attempts={capture.Attempts} " +
                $"active_process=\"{Escape(activeWindow.ProcessName)}\"");

            var spellcheck = await _spellcheckService.SpellcheckAsync(capture.Text!);
            if (!spellcheck.Success)
            {
                ClipboardLoop.RestoreClipboard(originalClipboard);
                if (spellcheck.ErrorCode == SpellcheckErrorCodes.MissingApiKey)
                {
                    _notify("API key needed", "Enter your OpenAI API key in Settings.");
                    _showSettings();
                }
                else
                {
                    _notify("Spell check failed", spellcheck.ErrorMessage ?? "The request failed.");
                }

                _logger.Log(
                    "request_failed " +
                    $"input_len={capture.Text!.Length} " +
                    $"duration_ms={stopwatch.ElapsedMilliseconds} " +
                    $"request_duration_ms={spellcheck.DurationMs} " +
                    $"request_attempts={spellcheck.Attempts} " +
                    $"status_code={spellcheck.StatusCode ?? 0} " +
                    $"active_process=\"{Escape(activeWindow.ProcessName)}\" " +
                    $"error_code={spellcheck.ErrorCode} " +
                    $"error=\"{Escape(spellcheck.ErrorMessage)}\"");
                return;
            }

            var postProcessStarted = Environment.TickCount64;
            var postProcess = _postProcessor.Process(
                spellcheck.OutputText!,
                OpenAiSpellcheckService.PromptInstruction);
            var postProcessDuration = Environment.TickCount64 - postProcessStarted;
            var replacement = postProcess.Text;
            var replace = await ClipboardLoop.ReplaceSelectionAsync(replacement);

            _logger.Log(
                "replace_succeeded " +
                $"input_len={capture.Text!.Length} " +
                $"output_len={replacement.Length} " +
                $"duration_ms={stopwatch.ElapsedMilliseconds} " +
                $"capture_duration_ms={capture.DurationMs} " +
                $"request_duration_ms={spellcheck.DurationMs} " +
                $"postprocess_duration_ms={postProcessDuration} " +
                $"paste_duration_ms={replace.DurationMs} " +
                $"copy_attempts={capture.Attempts} " +
                $"request_attempts={spellcheck.Attempts} " +
                $"replacements_count={postProcess.ReplacementsApplied.Count} " +
                $"urls_protected={postProcess.UrlsProtected} " +
                $"prompt_leak_triggered={postProcess.PromptLeak.Triggered.ToString().ToLowerInvariant()} " +
                $"prompt_leak_removed_chars={postProcess.PromptLeak.RemovedChars} " +
                $"active_process=\"{Escape(activeWindow.ProcessName)}\"");
        }
        catch (Exception ex)
        {
            ClipboardLoop.RestoreClipboard(originalClipboard);
            _notify("Spell check failed", ex.Message);
            _logger.Log(
                $"run_failed duration_ms={stopwatch.ElapsedMilliseconds} " +
                $"active_process=\"{Escape(activeWindow.ProcessName)}\" " +
                $"error_type={ex.GetType().Name} error=\"{Escape(ex.Message)}\"");
        }
        finally
        {
            _setBusy(false);
            _spellcheckGate.Release();
        }
    }

    public void Dispose()
    {
        _spellcheckGate.Dispose();
    }

    private static string Escape(string? value)
    {
        return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
