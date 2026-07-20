namespace UniversalSpellCheck;

internal enum RunStatus
{
    Success,
    CaptureFailed,
    RequestFailed,
    PasteFailed,
    RunFailed
}

internal sealed class RunRecord
{
    // Status
    public RunStatus Status { get; set; } = RunStatus.Success;
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public List<string> Events { get; } = new();

    // Context
    public ActiveWindowInfo ActiveWindowAtStart { get; set; } = new();
    public ActiveWindowInfo? ActiveWindowAtPaste { get; set; }
    public string Model { get; set; } = OpenAiSpellcheckService.DefaultModel;
    public string PasteMethod { get; } = "ctrl_v";
    public string? PasteFailurePhase { get; set; }
    public string? PasteErrorType { get; set; }
    public bool CorrectedTextOnClipboard { get; set; }
    public bool OriginalClipboardRestored { get; set; }
    // The captured (pre-correction) text was re-asserted with the
    // CanIncludeInClipboardHistory=0 tag so it is kept out of Windows
    // clipboard history / cloud clipboard. Best-effort; see ClipboardLoop.
    public bool CapturedTextHistoryExcluded { get; set; }
    public string? HistoryExcludeDetail { get; set; }

    // Text (refs only — strings are not copied on the hot path)
    public string? InputText { get; set; }
    // The same selection as InputText, in its CF_HTML flavor. "" when the source
    // offered no HTML. This is the input to the rich-text pipeline; today it is
    // captured and logged but not yet consumed.
    // See .planning/rich-text-clipboard-pipeline.md.
    public string CapturedHtml { get; set; } = "";
    // RTF flavor of the same selection, and every format name the source
    // offered. The format list is what makes an empty CapturedHtml readable:
    // "offered nothing" and "offered something we missed" are otherwise
    // indistinguishable after the fact.
    public string CapturedRtf { get; set; } = "";
    public string ClipboardFormats { get; set; } = "";
    public string? OutputText { get; set; }
    public string? RawAiOutput { get; set; }
    public byte[]? RawResponseBytes { get; set; }
    public byte[]? RequestPayloadBytes { get; set; }
    public bool TextChanged { get; set; }

    // Counts
    public int CopyAttempts { get; set; }
    public int RequestAttempts { get; set; }
    public int? StatusCode { get; set; }

    // Timings — raw Stopwatch.GetTimestamp() ticks. Conversion to ms in finalize.
    public long T_HotkeyReceived { get; set; }
    public long T_CaptureStart { get; set; }
    public long T_CaptureEnd { get; set; }
    public long T_RequestSendStart { get; set; }
    public long T_RequestSendEnd { get; set; }
    public long T_ResponseFirstByte { get; set; }
    public long T_ResponseEnd { get; set; }
    public long RequestSendTicks { get; set; }
    public long RequestWaitTicks { get; set; }
    public long ResponseDownloadTicks { get; set; }
    public long T_PostProcessStart { get; set; }
    public long T_PostProcessEnd { get; set; }
    public long T_AfterCopyFormatStart { get; set; }
    public long T_AfterCopyFormatEnd { get; set; }
    public long T_BeforePasteFormatStart { get; set; }
    public long T_BeforePasteFormatEnd { get; set; }
    public long T_PromptGuardStart { get; set; }
    public long T_PromptGuardEnd { get; set; }
    public long T_PasteTargetCheck { get; set; }
    public long T_PasteIssued { get; set; }
    public long T_PasteAck { get; set; }
    public long T_HotPathReturned { get; set; }

    // Tokens
    public TokenUsage TokenUsage { get; set; } = new();

    // Target formatting
    public FormattingMatch? FormattingMatch { get; set; }
    public FormattingResult AfterCopyFormatting { get; set; } = FormattingResult.NotApplied("");
    public FormattingResult BeforePasteFormatting { get; set; } = FormattingResult.NotApplied("");
    public ProtectionResult Protection { get; set; } = ProtectionResult.Empty("");

    // Post-process
    public List<string> ReplacementsApplied { get; set; } = new();
    public PromptLeakResult PromptLeak { get; set; } = PromptLeakResult.NotTriggered("");

    // Cleanup
    public IDataObject? OriginalClipboard { get; set; }
}
