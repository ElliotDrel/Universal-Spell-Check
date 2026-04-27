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
            _logger.Log(
                "run_started " +
                $"active_process=\"{Escape(activeWindow.ProcessName)}\" " +
                $"window_title=\"{Escape(activeWindow.WindowTitle)}\"");

            originalClipboard = Clipboard.GetDataObject();

            var capture = await ClipboardLoop.CaptureSelectionAsync();
            if (!capture.Success)
            {
                ClipboardLoop.RestoreClipboard(originalClipboard);
                _notify(
                    CaptureFailureTitle(capture.FailureReason),
                    capture.FailureReason ?? "The selected app did not copy text.");
                _logger.Log(
                    $"capture_failed duration_ms={stopwatch.ElapsedMilliseconds} " +
                    $"capture_duration_ms={capture.DurationMs} " +
                    $"copy_attempts={capture.Attempts} " +
                    $"active_process=\"{Escape(activeWindow.ProcessName)}\" " +
                    $"reason=\"{capture.FailureReason}\"");
                _logger.LogData("spellcheck_detail", new
                {
                    status = "capture_failed",
                    error = capture.FailureReason,
                    model = OpenAiSpellcheckService.Model,
                    active_app = activeWindow.WindowTitle,
                    active_exe = activeWindow.ProcessName,
                    paste_method = "ctrl_v",
                    text_changed = false,
                    input_text = "",
                    input_chars = 0,
                    output_text = "",
                    output_chars = 0,
                    raw_ai_output = "",
                    raw_response = "",
                    tokens = EmptyTokens(),
                    timings = new
                    {
                        clipboard_ms = capture.DurationMs,
                        payload_ms = 0,
                        request_ms = 0,
                        api_ms = 0,
                        parse_ms = 0,
                        replacements_ms = 0,
                        prompt_guard_ms = 0,
                        paste_ms = 0,
                        total_ms = stopwatch.ElapsedMilliseconds
                    },
                    replacements = EmptyReplacements(),
                    prompt_leak = EmptyPromptLeak(),
                    events = new[] { "capture_failed" }
                });
                return;
            }

            _setBusy(true);
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
                _logger.LogData("spellcheck_detail", new
                {
                    status = "request_failed",
                    error = spellcheck.ErrorMessage,
                    error_code = spellcheck.ErrorCode,
                    status_code = spellcheck.StatusCode,
                    model = OpenAiSpellcheckService.Model,
                    active_app = activeWindow.WindowTitle,
                    active_exe = activeWindow.ProcessName,
                    paste_method = "ctrl_v",
                    text_changed = false,
                    input_text = capture.Text,
                    input_chars = capture.Text!.Length,
                    output_text = "",
                    output_chars = 0,
                    raw_ai_output = spellcheck.RawOutputText ?? "",
                    raw_response = spellcheck.RawResponse ?? "",
                    request_payload = spellcheck.RequestPayload ?? "",
                    tokens = spellcheck.Tokens,
                    timings = new
                    {
                        clipboard_ms = capture.DurationMs,
                        payload_ms = 0,
                        request_ms = spellcheck.DurationMs,
                        api_ms = spellcheck.DurationMs,
                        parse_ms = 0,
                        replacements_ms = 0,
                        prompt_guard_ms = 0,
                        paste_ms = 0,
                        total_ms = stopwatch.ElapsedMilliseconds
                    },
                    replacements = EmptyReplacements(),
                    prompt_leak = EmptyPromptLeak(),
                    events = new[] { "capture_succeeded", "request_failed" }
                });
                return;
            }

            var postProcessStarted = Environment.TickCount64;
            var postProcess = _postProcessor.Process(
                spellcheck.OutputText!,
                OpenAiSpellcheckService.PromptInstruction);
            var postProcessDuration = Environment.TickCount64 - postProcessStarted;
            var replacement = postProcess.Text;
            var promptGuardMs = postProcess.PromptLeak.Triggered ? postProcessDuration : 0;
            var replacementsMs = Math.Max(0, postProcessDuration - promptGuardMs);
            var pasteTarget = ActiveWindowInfo.Capture();
            if (!activeWindow.HasSameProcess(pasteTarget))
            {
                ClipboardLoop.RestoreClipboard(originalClipboard);
                var reason = "Target app changed before paste.";
                _notify("Paste failed", $"{activeWindow.ProcessName} lost focus before the corrected text could be pasted.");
                _logger.Log(
                    "paste_failed " +
                    $"reason=\"{Escape(reason)}\" " +
                    $"input_len={capture.Text!.Length} " +
                    $"output_len={replacement.Length} " +
                    $"duration_ms={stopwatch.ElapsedMilliseconds} " +
                    $"capture_duration_ms={capture.DurationMs} " +
                    $"request_duration_ms={spellcheck.DurationMs} " +
                    $"postprocess_duration_ms={postProcessDuration} " +
                    $"expected_process=\"{Escape(activeWindow.ProcessName)}\" " +
                    $"expected_window=\"{Escape(activeWindow.WindowTitle)}\" " +
                    $"actual_process=\"{Escape(pasteTarget.ProcessName)}\" " +
                    $"actual_window=\"{Escape(pasteTarget.WindowTitle)}\"");
                _logger.LogData("spellcheck_detail", new
                {
                    status = "paste_failed",
                    error = reason,
                    model = OpenAiSpellcheckService.Model,
                    active_app = activeWindow.WindowTitle,
                    active_exe = activeWindow.ProcessName,
                    paste_target_app = pasteTarget.WindowTitle,
                    paste_target_exe = pasteTarget.ProcessName,
                    paste_method = "ctrl_v",
                    text_changed = false,
                    input_text = capture.Text,
                    input_chars = capture.Text!.Length,
                    output_text = replacement,
                    output_chars = replacement.Length,
                    raw_ai_output = spellcheck.RawOutputText ?? "",
                    raw_response = spellcheck.RawResponse ?? "",
                    request_payload = spellcheck.RequestPayload ?? "",
                    tokens = spellcheck.Tokens,
                    timings = new
                    {
                        clipboard_ms = capture.DurationMs,
                        payload_ms = 0,
                        request_ms = spellcheck.DurationMs,
                        api_ms = spellcheck.DurationMs,
                        parse_ms = 0,
                        replacements_ms = replacementsMs,
                        prompt_guard_ms = promptGuardMs,
                        paste_ms = 0,
                        total_ms = stopwatch.ElapsedMilliseconds
                    },
                    replacements = new
                    {
                        count = postProcess.ReplacementsApplied.Count,
                        applied = postProcess.ReplacementsApplied,
                        urls_protected = postProcess.UrlsProtected
                    },
                    prompt_leak = new
                    {
                        triggered = postProcess.PromptLeak.Triggered,
                        occurrences = postProcess.PromptLeak.Occurrences,
                        text_input_removed = postProcess.PromptLeak.TextInputRemoved,
                        removed_chars = postProcess.PromptLeak.RemovedChars,
                        before_length = postProcess.PromptLeak.BeforeLength,
                        after_length = postProcess.PromptLeak.AfterLength
                    },
                    events = new[] { "capture_succeeded", "request_succeeded", "postprocess_applied", "paste_failed" }
                });
                return;
            }

            var replace = await ClipboardLoop.ReplaceSelectionAsync(replacement);
            if (!replace.Success)
            {
                ClipboardLoop.RestoreClipboard(originalClipboard);
                _notify("Paste failed", replace.FailureReason ?? "The corrected text could not be pasted.");
                _logger.Log(
                    "paste_failed " +
                    $"reason=\"{Escape(replace.FailureReason)}\" " +
                    $"input_len={capture.Text!.Length} " +
                    $"output_len={replacement.Length} " +
                    $"duration_ms={stopwatch.ElapsedMilliseconds} " +
                    $"capture_duration_ms={capture.DurationMs} " +
                    $"request_duration_ms={spellcheck.DurationMs} " +
                    $"postprocess_duration_ms={postProcessDuration} " +
                    $"paste_duration_ms={replace.DurationMs} " +
                    $"active_process=\"{Escape(activeWindow.ProcessName)}\"");
                return;
            }

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
            _logger.LogData("spellcheck_detail", new
            {
                status = "success",
                error = "",
                model = OpenAiSpellcheckService.Model,
                active_app = activeWindow.WindowTitle,
                active_exe = activeWindow.ProcessName,
                paste_target_app = pasteTarget.WindowTitle,
                paste_target_exe = pasteTarget.ProcessName,
                paste_method = "ctrl_v",
                text_changed = !string.Equals(capture.Text, replacement, StringComparison.Ordinal),
                input_text = capture.Text,
                input_chars = capture.Text!.Length,
                output_text = replacement,
                output_chars = replacement.Length,
                raw_ai_output = spellcheck.RawOutputText ?? "",
                raw_response = spellcheck.RawResponse ?? "",
                request_payload = spellcheck.RequestPayload ?? "",
                tokens = spellcheck.Tokens,
                timings = new
                {
                    clipboard_ms = capture.DurationMs,
                    payload_ms = 0,
                    request_ms = spellcheck.DurationMs,
                    api_ms = spellcheck.DurationMs,
                    parse_ms = 0,
                    replacements_ms = replacementsMs,
                    prompt_guard_ms = promptGuardMs,
                    paste_ms = replace.DurationMs,
                    total_ms = stopwatch.ElapsedMilliseconds
                },
                replacements = new
                {
                    count = postProcess.ReplacementsApplied.Count,
                    applied = postProcess.ReplacementsApplied,
                    urls_protected = postProcess.UrlsProtected
                },
                prompt_leak = new
                {
                    triggered = postProcess.PromptLeak.Triggered,
                    occurrences = postProcess.PromptLeak.Occurrences,
                    text_input_removed = postProcess.PromptLeak.TextInputRemoved,
                    removed_chars = postProcess.PromptLeak.RemovedChars,
                    before_length = postProcess.PromptLeak.BeforeLength,
                    after_length = postProcess.PromptLeak.AfterLength
                },
                events = new[] { "capture_succeeded", "request_succeeded", "postprocess_applied", "replace_succeeded" }
            });
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

    private static string CaptureFailureTitle(string? failureReason)
    {
        return string.Equals(failureReason, "Copied selection was empty.", StringComparison.Ordinal)
            ? "No selected text"
            : "Copy failed";
    }

    private static object EmptyTokens() => new
    {
        input = 0,
        output = 0,
        total = 0,
        cached = 0,
        reasoning = 0
    };

    private static object EmptyReplacements() => new
    {
        count = 0,
        applied = Array.Empty<string>(),
        urls_protected = 0
    };

    private static object EmptyPromptLeak() => new
    {
        triggered = false,
        occurrences = 0,
        text_input_removed = false,
        removed_chars = 0,
        before_length = 0,
        after_length = 0
    };
}
