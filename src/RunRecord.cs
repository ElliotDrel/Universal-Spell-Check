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

    // Text (refs only — strings are not copied on the hot path)
    public string? InputText { get; set; }
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
    public long T_PostProcessStart { get; set; }
    public long T_PostProcessEnd { get; set; }
    public long T_PromptGuardStart { get; set; }
    public long T_PromptGuardEnd { get; set; }
    public long T_PasteTargetCheck { get; set; }
    public long T_PasteIssued { get; set; }
    public long T_PasteAck { get; set; }
    public long T_HotPathReturned { get; set; }

    // Tokens
    public TokenUsage TokenUsage { get; set; } = new();

    // Post-process
    public List<string> ReplacementsApplied { get; set; } = new();
    public int UrlsProtected { get; set; }
    public PromptLeakResult PromptLeak { get; set; } = PromptLeakResult.NotTriggered("");

    // Cleanup
    public IDataObject? OriginalClipboard { get; set; }
}
