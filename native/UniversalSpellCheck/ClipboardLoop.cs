namespace UniversalSpellCheck;

internal static class ClipboardLoop
{
    private static readonly TimeSpan CopyTimeout = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan HotkeyReleaseTimeout = TimeSpan.FromMilliseconds(1200);
    private const int MaxCopyAttempts = 2;

    public static async Task<CaptureResult> CaptureSelectionAsync()
    {
        var startedAt = Environment.TickCount64;

        if (!await KeyboardState.WaitForHotkeyReleaseAsync(HotkeyReleaseTimeout))
        {
            return CaptureResult.Fail(
                "Hotkey keys were not released before copy.",
                Environment.TickCount64 - startedAt,
                0);
        }

        string? lastFailureReason = null;
        for (var attempt = 1; attempt <= MaxCopyAttempts; attempt++)
        {
            var sentinel = $"__USC_COPY_SENTINEL_{Guid.NewGuid():N}__";
            Clipboard.SetText(sentinel, TextDataFormat.UnicodeText);

            SendKeys.SendWait("^c");

            var deadline = DateTime.UtcNow + CopyTimeout;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(PollInterval);

                if (!Clipboard.ContainsText(TextDataFormat.UnicodeText))
                {
                    continue;
                }

                var text = Clipboard.GetText(TextDataFormat.UnicodeText);
                if (text == sentinel)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    return CaptureResult.Fail(
                        "Copied selection was empty.",
                        Environment.TickCount64 - startedAt,
                        attempt);
                }

                return CaptureResult.Ok(text, Environment.TickCount64 - startedAt, attempt);
            }

            lastFailureReason = "Clipboard did not change after Ctrl+C.";
            await Task.Delay(60);
        }

        return CaptureResult.Fail(
            lastFailureReason ?? "Clipboard capture failed.",
            Environment.TickCount64 - startedAt,
            MaxCopyAttempts);
    }

    public static async Task<ReplaceResult> ReplaceSelectionAsync(string replacementText)
    {
        var startedAt = Environment.TickCount64;
        Clipboard.SetText(replacementText, TextDataFormat.UnicodeText);
        await Task.Delay(50);
        SendKeys.SendWait("^v");
        return ReplaceResult.Ok(Environment.TickCount64 - startedAt);
    }

    public static void RestoreClipboard(IDataObject? originalClipboard)
    {
        if (originalClipboard is null)
        {
            return;
        }

        try
        {
            Clipboard.SetDataObject(originalClipboard, true);
        }
        catch
        {
            // Best-effort only. This path is for non-destructive failure behavior.
        }
    }
}

internal sealed class CaptureResult
{
    public bool Success { get; init; }
    public string? Text { get; init; }
    public string? FailureReason { get; init; }
    public long DurationMs { get; init; }
    public int Attempts { get; init; }

    public static CaptureResult Ok(string text, long durationMs, int attempts) => new()
    {
        Success = true,
        Text = text,
        DurationMs = durationMs,
        Attempts = attempts
    };

    public static CaptureResult Fail(string reason, long durationMs, int attempts) => new()
    {
        Success = false,
        FailureReason = reason,
        DurationMs = durationMs,
        Attempts = attempts
    };
}

internal sealed class ReplaceResult
{
    public bool Success { get; init; }
    public string? FailureReason { get; init; }
    public long DurationMs { get; init; }

    public static ReplaceResult Ok(long durationMs) => new()
    {
        Success = true,
        DurationMs = durationMs
    };

    public static ReplaceResult Fail(string reason, long durationMs) => new()
    {
        Success = false,
        FailureReason = reason,
        DurationMs = durationMs
    };
}
