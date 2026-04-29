using System.Diagnostics;
using System.Text;

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
        var hotkeyTicks = Stopwatch.GetTimestamp();
        if (!await _spellcheckGate.WaitAsync(0))
        {
            _logger.Log("guard_rejected reason=already_running");
            return;
        }

        RunRecord record;
        try
        {
            record = await ExecuteHotPathAsync(hotkeyTicks);
        }
        finally
        {
            _spellcheckGate.Release();
        }

        // Fire-and-forget: every non-paste-critical step (logging serialization,
        // clipboard restore, replacements refresh, overlay hide) runs after the
        // hot path returns so the next hotkey can fire without waiting.
        _ = Task.Run(() => FinalizeAsync(record));
    }

    private async Task<RunRecord> ExecuteHotPathAsync(long hotkeyTicks)
    {
        var record = new RunRecord
        {
            T_HotkeyReceived = hotkeyTicks,
            Model = _spellcheckService.ModelName
        };

        try
        {
            // Clipboard backup must come before Ctrl+C so we can restore later.
            try { record.OriginalClipboard = Clipboard.GetDataObject(); } catch { /* best-effort */ }
            record.ActiveWindowAtStart = ActiveWindowInfo.Capture();
            record.Events.Add("run_started");
            _setBusy(true);

            // Capture
            record.T_CaptureStart = Stopwatch.GetTimestamp();
            var capture = await ClipboardLoop.CaptureSelectionAsync();
            record.T_CaptureEnd = Stopwatch.GetTimestamp();
            record.CopyAttempts = capture.Attempts;

            if (!capture.Success)
            {
                record.Status = RunStatus.CaptureFailed;
                record.ErrorMessage = capture.FailureReason;
                record.Events.Add("capture_failed");
                _notify(
                    CaptureFailureTitle(capture.FailureReason),
                    capture.FailureReason ?? "The selected app did not copy text.");
                record.T_HotPathReturned = Stopwatch.GetTimestamp();
                return record;
            }

            record.InputText = capture.Text;
            record.Events.Add("capture_succeeded");

            // Spellcheck request — timings filled inside the service via record.
            var spell = await _spellcheckService.SpellcheckAsync(capture.Text!, record);
            record.RequestAttempts = spell.Attempts;
            record.StatusCode = spell.StatusCode;
            record.RawResponseBytes = spell.RawResponseBytes;
            record.RequestPayloadBytes = spell.RequestPayloadBytes;
            record.TokenUsage = spell.Tokens;
            record.RawAiOutput = spell.OutputText;

            if (!spell.Success)
            {
                record.Status = RunStatus.RequestFailed;
                record.ErrorCode = spell.ErrorCode;
                record.ErrorMessage = spell.ErrorMessage;
                record.Events.Add("request_failed");
                if (spell.ErrorCode == SpellcheckErrorCodes.MissingApiKey)
                {
                    _notify("API key needed", "Enter your OpenAI API key in Settings.");
                    _showSettings();
                }
                else
                {
                    _notify("Spell check failed", spell.ErrorMessage ?? "The request failed.");
                }
                record.T_HotPathReturned = Stopwatch.GetTimestamp();
                return record;
            }

            record.Events.Add("request_succeeded");

            // Post-process (replacements + prompt-leak guard)
            record.T_PostProcessStart = Stopwatch.GetTimestamp();
            var pp = _postProcessor.Process(spell.OutputText!);
            record.T_PostProcessEnd = Stopwatch.GetTimestamp();
            if (pp.PromptLeak.Triggered)
            {
                record.T_PromptGuardStart = record.T_PostProcessStart;
                record.T_PromptGuardEnd = record.T_PostProcessEnd;
            }
            record.OutputText = pp.Text;
            record.ReplacementsApplied = pp.ReplacementsApplied;
            record.UrlsProtected = pp.UrlsProtected;
            record.PromptLeak = pp.PromptLeak;
            record.Events.Add("postprocess_applied");

            // Paste-target check
            record.T_PasteTargetCheck = Stopwatch.GetTimestamp();
            var pasteTarget = ActiveWindowInfo.Capture();
            record.ActiveWindowAtPaste = pasteTarget;

            if (!record.ActiveWindowAtStart.HasSameProcess(pasteTarget))
            {
                record.Status = RunStatus.PasteFailed;
                record.ErrorMessage = "Target app changed before paste.";
                record.Events.Add("paste_failed");
                _notify(
                    "Paste failed",
                    $"{record.ActiveWindowAtStart.ProcessName} lost focus before the corrected text could be pasted.");
                record.T_HotPathReturned = Stopwatch.GetTimestamp();
                return record;
            }

            // Paste — set clipboard + Ctrl+V only. Restore is moved to finalize.
            record.T_PasteIssued = Stopwatch.GetTimestamp();
            try
            {
                Clipboard.SetText(pp.Text, TextDataFormat.UnicodeText);
                await Task.Delay(50);
                SendKeys.SendWait("^v");
            }
            catch (Exception ex)
            {
                record.Status = RunStatus.PasteFailed;
                record.ErrorMessage = ex.Message;
                record.Events.Add("paste_failed");
                _notify("Paste failed", "The corrected text could not be pasted.");
                record.T_PasteAck = Stopwatch.GetTimestamp();
                record.T_HotPathReturned = Stopwatch.GetTimestamp();
                return record;
            }
            record.T_PasteAck = Stopwatch.GetTimestamp();

            record.TextChanged = !string.Equals(capture.Text, pp.Text, StringComparison.Ordinal);
            record.Status = RunStatus.Success;
            record.Events.Add("replace_succeeded");
            record.T_HotPathReturned = Stopwatch.GetTimestamp();
            return record;
        }
        catch (Exception ex)
        {
            record.Status = RunStatus.RunFailed;
            record.ErrorCode = ex.GetType().Name;
            record.ErrorMessage = ex.Message;
            record.Events.Add("run_failed");
            try { _notify("Spell check failed", ex.Message); } catch { /* best-effort */ }
            record.T_HotPathReturned = Stopwatch.GetTimestamp();
            return record;
        }
    }

    private void FinalizeAsync(RunRecord r)
    {
        try
        {
            _setBusy(false);

            // Single point of clipboard restore — covers every status path.
            ClipboardLoop.RestoreClipboard(r.OriginalClipboard);

            var clipboardMs = TicksToMs(r.T_CaptureStart, r.T_CaptureEnd);
            var requestMs = TicksToMs(r.T_RequestSendStart, r.T_ResponseEnd);
            var apiMs = requestMs;
            var ppMs = TicksToMs(r.T_PostProcessStart, r.T_PostProcessEnd);
            var promptGuardMs = r.PromptLeak.Triggered ? ppMs : 0;
            var replacementsMs = Math.Max(0, ppMs - promptGuardMs);
            var pasteMs = TicksToMs(r.T_PasteIssued, r.T_PasteAck);
            var totalMs = TicksToMs(r.T_HotkeyReceived, r.T_HotPathReturned);

            var rawResponse = r.RawResponseBytes is null
                ? ""
                : Encoding.UTF8.GetString(r.RawResponseBytes);
            var requestPayload = r.RequestPayloadBytes is null
                ? ""
                : Encoding.UTF8.GetString(r.RequestPayloadBytes);

            var statusName = r.Status switch
            {
                RunStatus.Success => "success",
                RunStatus.CaptureFailed => "capture_failed",
                RunStatus.RequestFailed => "request_failed",
                RunStatus.PasteFailed => "paste_failed",
                _ => "run_failed"
            };

            // Human-readable line
            _logger.Log(
                $"run_completed status={statusName} " +
                $"input_len={r.InputText?.Length ?? 0} " +
                $"output_len={r.OutputText?.Length ?? 0} " +
                $"total_ms={totalMs} " +
                $"clipboard_ms={clipboardMs} " +
                $"request_ms={requestMs} " +
                $"postprocess_ms={ppMs} " +
                $"paste_ms={pasteMs} " +
                $"copy_attempts={r.CopyAttempts} " +
                $"request_attempts={r.RequestAttempts} " +
                $"replacements_count={r.ReplacementsApplied.Count} " +
                $"urls_protected={r.UrlsProtected} " +
                $"prompt_leak_triggered={r.PromptLeak.Triggered.ToString().ToLowerInvariant()} " +
                $"prompt_leak_removed_chars={r.PromptLeak.RemovedChars} " +
                $"active_process=\"{Escape(r.ActiveWindowAtStart.ProcessName)}\" " +
                (r.ErrorMessage is null ? "" : $"error=\"{Escape(r.ErrorMessage)}\" ") +
                (r.ErrorCode is null ? "" : $"error_code={r.ErrorCode}"));

            _logger.LogData("spellcheck_detail", new
            {
                status = statusName,
                error = r.ErrorMessage ?? "",
                error_code = r.ErrorCode,
                status_code = r.StatusCode,
                model = r.Model,
                active_app = r.ActiveWindowAtStart.WindowTitle,
                active_exe = r.ActiveWindowAtStart.ProcessName,
                paste_target_app = r.ActiveWindowAtPaste?.WindowTitle ?? "",
                paste_target_exe = r.ActiveWindowAtPaste?.ProcessName ?? "",
                paste_method = r.PasteMethod,
                text_changed = r.TextChanged,
                input_text = r.InputText ?? "",
                input_chars = r.InputText?.Length ?? 0,
                output_text = r.OutputText ?? "",
                output_chars = r.OutputText?.Length ?? 0,
                raw_ai_output = r.RawAiOutput ?? "",
                raw_response = rawResponse,
                request_payload = requestPayload,
                tokens = new
                {
                    input = r.TokenUsage.Input,
                    output = r.TokenUsage.Output,
                    total = r.TokenUsage.Total,
                    cached = r.TokenUsage.Cached,
                    reasoning = r.TokenUsage.Reasoning
                },
                timings = new
                {
                    clipboard_ms = clipboardMs,
                    payload_ms = 0,
                    request_ms = requestMs,
                    api_ms = apiMs,
                    parse_ms = 0,
                    replacements_ms = replacementsMs,
                    prompt_guard_ms = promptGuardMs,
                    paste_ms = pasteMs,
                    total_ms = totalMs
                },
                replacements = new
                {
                    count = r.ReplacementsApplied.Count,
                    applied = r.ReplacementsApplied,
                    urls_protected = r.UrlsProtected
                },
                prompt_leak = new
                {
                    triggered = r.PromptLeak.Triggered,
                    occurrences = r.PromptLeak.Occurrences,
                    text_input_removed = r.PromptLeak.TextInputRemoved,
                    removed_chars = r.PromptLeak.RemovedChars,
                    before_length = r.PromptLeak.BeforeLength,
                    after_length = r.PromptLeak.AfterLength
                },
                events = r.Events.ToArray()
            });

            // Mtime/size-aware refresh — no-op in steady state.
            _postProcessor.RefreshIfChanged();
        }
        catch (Exception ex)
        {
            try
            {
                _logger.Log(
                    $"finalize_failed error_type={ex.GetType().Name} " +
                    $"error=\"{Escape(ex.Message)}\"");
            }
            catch { /* swallow — finalize must never affect the next hotkey */ }
        }
    }

    private static long TicksToMs(long start, long end)
    {
        if (start == 0 || end == 0 || end <= start) return 0;
        return (long)((end - start) * 1000.0 / Stopwatch.Frequency);
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
}
