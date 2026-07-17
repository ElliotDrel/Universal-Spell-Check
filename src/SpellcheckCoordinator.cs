using System.Diagnostics;
using System.Text;

namespace UniversalSpellCheck;

internal sealed class SpellcheckCoordinator : IDisposable
{
    private readonly DiagnosticsLogger _logger;
    private readonly OpenAiSpellcheckService _spellcheckService;
    private readonly TextPostProcessor _postProcessor;
    private readonly TargetFormattingPipeline _formattingPipeline;
    private readonly Action<string, string> _notify;
    private readonly Action<SpellcheckPhase> _setPhase;
    private readonly Action _showSettings;
    private readonly Func<IntPtr>? _clipboardOwnerHandle;
    private readonly SemaphoreSlim _spellcheckGate = new(1, 1);

    public SpellcheckCoordinator(
        DiagnosticsLogger logger,
        OpenAiSpellcheckService spellcheckService,
        TextPostProcessor postProcessor,
        TargetFormattingPipeline formattingPipeline,
        Action<string, string> notify,
        Action<SpellcheckPhase> setPhase,
        Action showSettings,
        Func<IntPtr>? clipboardOwnerHandle = null)
    {
        _logger = logger;
        _spellcheckService = spellcheckService;
        _postProcessor = postProcessor;
        _formattingPipeline = formattingPipeline;
        _notify = notify;
        _setPhase = setPhase;
        _showSettings = showSettings;
        // Owns the clipboard when re-tagging the captured text as transient.
        // Null in headless/bench mode, which never touches the real clipboard.
        _clipboardOwnerHandle = clipboardOwnerHandle;
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

        // Hide the overlay the moment the run is over — before the clipboard
        // restore below, which can block for seconds while the OS renders the
        // original clipboard formats.
        _setPhase(SpellcheckPhase.Done);

        // Clipboard restore must run here, on the STA UI thread — WinForms
        // Clipboard.* throws ThreadStateException on MTA pool threads, which
        // killed FinalizeAsync before it could log the run (finalize_failed
        // error_type=ThreadStateException) and left the user's clipboard
        // unrestored. Only failed runs take this branch, so the hot path for
        // successful runs is unaffected.
        if (!record.CorrectedTextOnClipboard)
        {
            record.OriginalClipboardRestored = ClipboardLoop.RestoreClipboard(record.OriginalClipboard);
        }

        // Fire-and-forget: every non-paste-critical step (logging serialization,
        // replacements refresh, overlay hide) runs after the hot path returns so
        // the next hotkey can fire without waiting.
        _ = Task.Run(() => FinalizeAsync(record));
    }

    public async Task<HeadlessResult?> RunHeadlessAsync(string inputText)
    {
        if (!await _spellcheckGate.WaitAsync(0)) return null;

        RunRecord record;
        try
        {
            record = await ExecuteHeadlessAsync(inputText);
        }
        finally
        {
            _spellcheckGate.Release();
        }

        _setPhase(SpellcheckPhase.Done);

        var requestMs = TicksToMs(record.T_RequestSendStart, record.T_ResponseEnd);
        var ppMs = TicksToMs(record.T_PostProcessStart, record.T_PostProcessEnd);
        var promptGuardMs = record.PromptLeak.Triggered ? ppMs : 0;

        _ = Task.Run(() => FinalizeAsync(record));

        return new HeadlessResult(
            Success: record.Status == RunStatus.Success,
            OutputText: record.OutputText,
            ErrorMessage: record.ErrorMessage,
            ErrorCode: record.ErrorCode,
            TotalMs: TicksToMs(record.T_HotkeyReceived, record.T_HotPathReturned),
            RequestMs: requestMs,
            ReplacementsMs: Math.Max(0, ppMs - promptGuardMs),
            PromptGuardMs: promptGuardMs,
            InputTokens: record.TokenUsage.Input,
            OutputTokens: record.TokenUsage.Output,
            CachedTokens: record.TokenUsage.Cached
        );
    }

    private async Task<RunRecord> ExecuteHeadlessAsync(string inputText)
    {
        var record = new RunRecord
        {
            T_HotkeyReceived = Stopwatch.GetTimestamp(),
            Model = _spellcheckService.ModelName
        };

        try
        {
            _setPhase(SpellcheckPhase.Copying);
            record.InputText = inputText;

            record.Protection = ProtectedText.Protect(inputText);
            var spell = await _spellcheckService.SpellcheckAsync(
                record.Protection.Text,
                record,
                onRequestSending: () => _setPhase(SpellcheckPhase.Sending),
                onRequestBodySent: () => _setPhase(SpellcheckPhase.Waiting));
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
                record.T_HotPathReturned = Stopwatch.GetTimestamp();
                return record;
            }

            record.T_PostProcessStart = Stopwatch.GetTimestamp();
            var pp = _postProcessor.Process(spell.OutputText!, record.Protection);
            record.T_PostProcessEnd = Stopwatch.GetTimestamp();
            if (pp.PromptLeak.Triggered)
            {
                record.T_PromptGuardStart = record.T_PostProcessStart;
                record.T_PromptGuardEnd = record.T_PostProcessEnd;
            }
            record.OutputText = pp.Text;
            record.ReplacementsApplied = pp.ReplacementsApplied;
            record.PromptLeak = pp.PromptLeak;
            if (!pp.ProtectionRestored)
            {
                record.Status = RunStatus.RunFailed;
                record.ErrorCode = SpellcheckErrorCodes.ProtectedTextRestoreFailed;
                record.ErrorMessage = $"AI output changed protected placeholder {pp.InvalidPlaceholder}.";
                record.T_HotPathReturned = Stopwatch.GetTimestamp();
                return record;
            }
            record.Status = RunStatus.Success;
            record.T_HotPathReturned = Stopwatch.GetTimestamp();
            return record;
        }
        catch (Exception ex)
        {
            record.Status = RunStatus.RunFailed;
            record.ErrorMessage = ex.Message;
            record.T_HotPathReturned = Stopwatch.GetTimestamp();
            return record;
        }
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
            record.ActiveWindowAtStart = ActiveWindowInfo.Capture();
            // Capture target identity before any clipboard work can yield, then
            // back up the clipboard before Ctrl+C so failed runs can restore it.
            record.OriginalClipboard = ClipboardLoop.TryGetClipboardDataObject();
            record.Events.Add("run_started");
            _setPhase(SpellcheckPhase.Copying);

            // Capture
            record.T_CaptureStart = Stopwatch.GetTimestamp();
            var capture = await ClipboardLoop.CaptureSelectionAsync();
            record.T_CaptureEnd = Stopwatch.GetTimestamp();
            record.CopyAttempts = capture.Attempts;

            if (!capture.Success)
            {
                record.Status = RunStatus.CaptureFailed;
                record.ErrorMessage = capture.FailureReason;
                record.Events.Add(capture.Detail is null
                    ? "capture_failed"
                    : $"capture_failed {capture.Detail}");
                _notify(
                    CaptureFailureTitle(capture.FailureReason),
                    capture.FailureReason ?? "The selected app did not copy text.");
                record.T_HotPathReturned = Stopwatch.GetTimestamp();
                return record;
            }

            record.InputText = capture.Text;
            record.Events.Add("capture_succeeded");

            // Re-tag the just-captured (incorrect) text as transient so it stays
            // out of Windows clipboard history / cloud clipboard. The corrected
            // text written at paste time is left untagged so it IS kept in
            // history. Best-effort; must happen here (right after capture, on the
            // STA thread) to beat the OS history snapshot. Never fails the run.
            if (_clipboardOwnerHandle is { } ownerHandle)
            {
                record.CapturedTextHistoryExcluded = ClipboardLoop.ExcludeTextFromHistory(
                    capture.Text!, ownerHandle(), out var historyExcludeDetail);
                record.HistoryExcludeDetail = historyExcludeDetail;
                record.Events.Add(
                    (record.CapturedTextHistoryExcluded ? "capture_history_excluded " : "capture_history_exclude_failed ")
                    + historyExcludeDetail);
            }

            record.T_AfterCopyFormatStart = Stopwatch.GetTimestamp();
            record.FormattingMatch = _formattingPipeline.Resolve(record.ActiveWindowAtStart.ToTargetContext());
            record.AfterCopyFormatting = record.FormattingMatch is null
                ? FormattingResult.NotApplied(capture.Text!)
                : _formattingPipeline.ApplyAfterCopy(record.FormattingMatch, capture.Text!);
            record.T_AfterCopyFormatEnd = Stopwatch.GetTimestamp();
            if (record.AfterCopyFormatting.FailureCode is not null)
            {
                record.Events.Add($"target_format_after_copy_failed reason={record.AfterCopyFormatting.FailureCode}");
            }
            var textToSpellcheck = record.AfterCopyFormatting.Text;

            record.Protection = ProtectedText.Protect(textToSpellcheck);

            // Spellcheck request — timings filled inside the service via record.
            var spell = await _spellcheckService.SpellcheckAsync(
                record.Protection.Text,
                record,
                onRequestSending: () => _setPhase(SpellcheckPhase.Sending),
                onRequestBodySent: () => _setPhase(SpellcheckPhase.Waiting));
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
            _setPhase(SpellcheckPhase.Pasting);

            // Post-process (replacements + prompt-leak guard + protected literal restore)
            record.T_PostProcessStart = Stopwatch.GetTimestamp();
            var pp = _postProcessor.Process(spell.OutputText!, record.Protection);
            record.T_PostProcessEnd = Stopwatch.GetTimestamp();
            if (pp.PromptLeak.Triggered)
            {
                record.T_PromptGuardStart = record.T_PostProcessStart;
                record.T_PromptGuardEnd = record.T_PostProcessEnd;
            }
            record.ReplacementsApplied = pp.ReplacementsApplied;
            record.PromptLeak = pp.PromptLeak;
            if (!pp.ProtectionRestored)
            {
                record.Status = RunStatus.RunFailed;
                record.ErrorCode = SpellcheckErrorCodes.ProtectedTextRestoreFailed;
                record.ErrorMessage = $"AI output changed protected placeholder {pp.InvalidPlaceholder}.";
                record.Events.Add("protected_text_restore_failed");
                _notify("Spell check failed", "Protected text could not be restored safely.");
                record.T_HotPathReturned = Stopwatch.GetTimestamp();
                return record;
            }
            record.Events.Add("postprocess_applied");

            var beforePasteWindow = ActiveWindowInfo.Capture();
            record.ActiveWindowAtPaste = beforePasteWindow;
            var beforePasteContext = beforePasteWindow.ToTargetContext();
            record.T_BeforePasteFormatStart = Stopwatch.GetTimestamp();
            record.BeforePasteFormatting = record.FormattingMatch is null
                ? FormattingResult.NotApplied(pp.Text)
                : _formattingPipeline.ApplyBeforePaste(record.FormattingMatch, pp.Text, beforePasteContext);
            record.T_BeforePasteFormatEnd = Stopwatch.GetTimestamp();

            if (!_formattingPipeline.ValidateDestination(
                    record.FormattingMatch,
                    record.ActiveWindowAtStart.ToTargetContext(),
                    beforePasteContext)
                || record.BeforePasteFormatting.AbortPaste)
            {
                var literalRestoreFailed = string.Equals(
                    record.BeforePasteFormatting.FailureCode,
                    "literal_restore_failed",
                    StringComparison.Ordinal);
                record.Status = literalRestoreFailed ? RunStatus.RunFailed : RunStatus.PasteFailed;
                record.ErrorCode = literalRestoreFailed
                    ? SpellcheckErrorCodes.ProtectedTextRestoreFailed
                    : record.BeforePasteFormatting.FailureCode;
                record.ErrorMessage = literalRestoreFailed
                    ? "Target formatting changed a protected placeholder."
                    : "Target app changed before paste.";
                record.PasteFailurePhase = literalRestoreFailed
                    ? "before_paste_literal_restore"
                    : "target_changed_before_format";
                record.Events.Add(literalRestoreFailed
                    ? "target_format_literal_restore_failed"
                    : "paste_failed");
                _notify(
                    literalRestoreFailed ? "Spell check failed" : "Paste failed",
                    literalRestoreFailed
                        ? "Protected text could not be restored safely."
                        : $"{record.ActiveWindowAtStart.ProcessName} lost focus before the corrected text could be pasted.");
                record.T_HotPathReturned = Stopwatch.GetTimestamp();
                return record;
            }

            if (record.BeforePasteFormatting.FailureCode is not null)
            {
                record.Events.Add($"target_format_before_paste_failed reason={record.BeforePasteFormatting.FailureCode}");
            }

            var finalText = record.BeforePasteFormatting.Text;
            record.OutputText = finalText;

            // Paste: set clipboard, let it settle, validate once more, then send Ctrl+V.
            record.T_PasteIssued = Stopwatch.GetTimestamp();
            if (await ClipboardLoop.TrySetReplacementTextAsync(finalText))
            {
                record.CorrectedTextOnClipboard = true;
            }
            else
            {
                record.Status = RunStatus.PasteFailed;
                record.ErrorMessage = "Requested Clipboard operation did not succeed.";
                record.PasteErrorType = "ExternalException";
                record.PasteFailurePhase = "set_corrected_clipboard";
                record.Events.Add("paste_failed");
                _notify("Paste failed", "The corrected text could not be copied to the clipboard.");
                record.T_PasteAck = Stopwatch.GetTimestamp();
                record.T_HotPathReturned = Stopwatch.GetTimestamp();
                return record;
            }

            await Task.Delay(50);

            record.T_PasteTargetCheck = Stopwatch.GetTimestamp();
            var pasteTarget = ActiveWindowInfo.Capture();
            record.ActiveWindowAtPaste = pasteTarget;

            if (!_formattingPipeline.ValidateDestination(
                    record.FormattingMatch,
                    record.ActiveWindowAtStart.ToTargetContext(),
                    pasteTarget.ToTargetContext()))
            {
                record.Status = RunStatus.PasteFailed;
                record.ErrorMessage = "Target app changed before paste.";
                record.PasteFailurePhase = "target_changed";
                record.Events.Add("paste_failed");
                record.CorrectedTextOnClipboard = false;
                _notify(
                    "Paste failed",
                    $"{record.ActiveWindowAtStart.ProcessName} lost focus before the corrected text could be pasted.");
                record.T_PasteAck = Stopwatch.GetTimestamp();
                record.T_HotPathReturned = Stopwatch.GetTimestamp();
                return record;
            }

            try
            {
                SendKeys.SendWait("^v");
            }
            catch (Exception ex)
            {
                record.Status = RunStatus.PasteFailed;
                record.ErrorMessage = ex.Message;
                record.PasteErrorType = ex.GetType().Name;
                record.PasteFailurePhase = "send_ctrl_v";
                record.Events.Add("paste_failed");
                _notify("Paste failed", "The corrected text is on the clipboard, but could not be pasted automatically.");
                record.T_PasteAck = Stopwatch.GetTimestamp();
                record.T_HotPathReturned = Stopwatch.GetTimestamp();
                return record;
            }
            record.T_PasteAck = Stopwatch.GetTimestamp();

            record.TextChanged = !string.Equals(capture.Text, finalText, StringComparison.Ordinal);
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
            var clipboardMs = TicksToMs(r.T_CaptureStart, r.T_CaptureEnd);
            var afterCopyFormatMs = TicksToMs(r.T_AfterCopyFormatStart, r.T_AfterCopyFormatEnd);
            var beforePasteFormatMs = TicksToMs(r.T_BeforePasteFormatStart, r.T_BeforePasteFormatEnd);
            var terminalApplied = r.FormattingMatch?.Rule.Id == TerminalFormattingRule.RuleId
                && r.AfterCopyFormatting.Applied;
            var normMs = terminalApplied ? afterCopyFormatMs : 0;
            var terminalCounters = r.AfterCopyFormatting.Counters;
            var doubleBreakCount = GetCounter(terminalCounters, TerminalFormattingRule.DoubleBreakCounter);
            var listItemCount = GetCounter(terminalCounters, TerminalFormattingRule.ListItemCounter);
            var softWrapCount = GetCounter(terminalCounters, TerminalFormattingRule.SoftWrapCounter);
            var requestMs = TicksToMs(r.T_RequestSendStart, r.T_ResponseEnd);
            var requestSendMs = TicksToMs(r.RequestSendTicks);
            var requestWaitMs = TicksToMs(r.RequestWaitTicks);
            var responseDownloadMs = TicksToMs(r.ResponseDownloadTicks);
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
                $"request_send_ms={requestSendMs} " +
                $"request_wait_ms={requestWaitMs} " +
                $"response_download_ms={responseDownloadMs} " +
                $"postprocess_ms={ppMs} " +
                $"after_copy_format_ms={afterCopyFormatMs} " +
                $"before_paste_format_ms={beforePasteFormatMs} " +
                $"paste_ms={pasteMs} " +
                $"copy_attempts={r.CopyAttempts} " +
                $"request_attempts={r.RequestAttempts} " +
                $"replacements_count={r.ReplacementsApplied.Count} " +
                $"protected_values={r.Protection.Entries.Count} " +
                $"urls_protected={r.Protection.Count(ProtectedLiteralKind.Url)} " +
                $"prompt_leak_triggered={r.PromptLeak.Triggered.ToString().ToLowerInvariant()} " +
                $"prompt_leak_removed_chars={r.PromptLeak.RemovedChars} " +
                $"active_process=\"{Escape(r.ActiveWindowAtStart.ProcessName)}\" " +
                (terminalApplied
                    ? $"terminal_normalized=true terminal_norm_ms={normMs} terminal_norm_chars_removed={r.AfterCopyFormatting.CharsRemoved} " +
                      $"terminal_norm_double_break={doubleBreakCount} terminal_norm_list_item={listItemCount} terminal_norm_soft_wrap={softWrapCount} "
                    : "") +
                $"target_formatting_rule={r.FormattingMatch?.Rule.Id ?? "none"} " +
                $"corrected_text_on_clipboard={r.CorrectedTextOnClipboard.ToString().ToLowerInvariant()} " +
                $"original_clipboard_restored={r.OriginalClipboardRestored.ToString().ToLowerInvariant()} " +
                $"captured_text_history_excluded={r.CapturedTextHistoryExcluded.ToString().ToLowerInvariant()} " +
                (r.HistoryExcludeDetail is null ? "" : $"history_exclude_detail=\"{Escape(r.HistoryExcludeDetail)}\" ") +
                (r.PasteFailurePhase is null ? "" : $"paste_failure_phase={r.PasteFailurePhase} ") +
                (r.PasteErrorType is null ? "" : $"paste_error_type={r.PasteErrorType} ") +
                (r.ErrorMessage is null ? "" : $"error=\"{Escape(r.ErrorMessage)}\" ") +
                (r.ErrorCode is null ? "" : $"error_code={r.ErrorCode}"));

            _logger.LogData("spellcheck_detail", new
            {
                status = statusName,
                error = r.ErrorMessage ?? "",
                error_code = r.ErrorCode,
                status_code = r.StatusCode,
                model = r.Model,
                active_app = r.FormattingMatch?.Rule.MatchType == TargetFormattingMatchType.Site
                    ? ""
                    : r.ActiveWindowAtStart.WindowTitle,
                active_exe = r.ActiveWindowAtStart.ProcessName,
                paste_target_app = r.FormattingMatch?.Rule.MatchType == TargetFormattingMatchType.Site
                    ? ""
                    : r.ActiveWindowAtPaste?.WindowTitle ?? "",
                paste_target_exe = r.ActiveWindowAtPaste?.ProcessName ?? "",
                paste_method = r.PasteMethod,
                paste_failure_phase = r.PasteFailurePhase,
                paste_error_type = r.PasteErrorType,
                corrected_text_on_clipboard = r.CorrectedTextOnClipboard,
                original_clipboard_restored = r.OriginalClipboardRestored,
                captured_text_history_excluded = r.CapturedTextHistoryExcluded,
                history_exclude_detail = r.HistoryExcludeDetail ?? "",
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
                    norm_ms = normMs,
                    after_copy_format_ms = afterCopyFormatMs,
                    before_paste_format_ms = beforePasteFormatMs,
                    payload_ms = 0,
                    request_ms = requestMs,
                    api_ms = apiMs,
                    request_send_ms = requestSendMs,
                    request_wait_ms = requestWaitMs,
                    response_download_ms = responseDownloadMs,
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
                    protected_values = r.Protection.Entries.Count,
                    urls_protected = r.Protection.Count(ProtectedLiteralKind.Url),
                    api_keys_protected = r.Protection.Count(ProtectedLiteralKind.ApiKey),
                    uuids_protected = r.Protection.Count(ProtectedLiteralKind.Uuid),
                    file_paths_protected = r.Protection.Count(ProtectedLiteralKind.FilePath),
                    opaque_ids_protected = r.Protection.Count(ProtectedLiteralKind.OpaqueId)
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
                terminal_normalization = new
                {
                    applied = terminalApplied,
                    process = terminalApplied ? r.ActiveWindowAtStart.ProcessName : "",
                    norm_ms = normMs,
                    chars_removed = terminalApplied ? r.AfterCopyFormatting.CharsRemoved : 0,
                    normalized_input_text = terminalApplied ? r.AfterCopyFormatting.Text : null,
                    passes = new
                    {
                        double_break_count = doubleBreakCount,
                        list_item_count = listItemCount,
                        soft_wrap_count = softWrapCount
                    }
                },
                target_formatting = new
                {
                    rule_id = r.FormattingMatch?.Rule.Id ?? "",
                    match_type = r.FormattingMatch?.Rule.MatchType switch
                    {
                        TargetFormattingMatchType.App => "app",
                        TargetFormattingMatchType.Site => "site",
                        _ => "none"
                    },
                    browser_context_state = r.FormattingMatch?.Rule.MatchType == TargetFormattingMatchType.Site
                        ? "matched"
                        : "not_applicable",
                    host = r.FormattingMatch?.StartingContext.Browser?.Host ?? "",
                    after_copy = new
                    {
                        applied = r.AfterCopyFormatting.Applied,
                        chars_added = r.AfterCopyFormatting.CharsAdded,
                        chars_removed = r.AfterCopyFormatting.CharsRemoved,
                        operations = r.AfterCopyFormatting.Operations,
                        failure = r.AfterCopyFormatting.FailureCode ?? "",
                        failure_type = r.AfterCopyFormatting.FailureType ?? ""
                    },
                    before_paste = new
                    {
                        applied = r.BeforePasteFormatting.Applied,
                        chars_added = r.BeforePasteFormatting.CharsAdded,
                        chars_removed = r.BeforePasteFormatting.CharsRemoved,
                        operations = r.BeforePasteFormatting.Operations,
                        failure = r.BeforePasteFormatting.FailureCode ?? "",
                        failure_type = r.BeforePasteFormatting.FailureType ?? ""
                    }
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
                // Stack included: a finalize failure means a whole run vanished
                // from the logs, so this line is the only forensic trace left.
                _logger.Log(
                    $"finalize_failed error_type={ex.GetType().Name} " +
                    $"error=\"{Escape(ex.Message)}\" " +
                    $"status={r.Status} active_process=\"{Escape(r.ActiveWindowAtStart.ProcessName)}\" " +
                    $"stack=\"{Escape(ex.ToString())}\"");
            }
            catch { /* swallow — finalize must never affect the next hotkey */ }
        }
    }

    private static long TicksToMs(long start, long end)
    {
        if (start == 0 || end == 0 || end <= start) return 0;
        return (long)((end - start) * 1000.0 / Stopwatch.Frequency);
    }

    private static long TicksToMs(long ticks)
    {
        if (ticks <= 0) return 0;
        return (long)(ticks * 1000.0 / Stopwatch.Frequency);
    }

    private static int GetCounter(IReadOnlyDictionary<string, int>? counters, string key)
    {
        return counters is not null && counters.TryGetValue(key, out var value) ? value : 0;
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

public enum SpellcheckPhase { Copying, Sending, Waiting, Pasting, Done }

public sealed record HeadlessResult(
    bool Success,
    string? OutputText,
    string? ErrorMessage,
    string? ErrorCode,
    long TotalMs,
    long RequestMs,
    long ReplacementsMs,
    long PromptGuardMs,
    int InputTokens,
    int OutputTokens,
    int CachedTokens
);
