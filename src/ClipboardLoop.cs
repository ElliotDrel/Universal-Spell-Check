namespace UniversalSpellCheck;

internal static class ClipboardLoop
{
    private static readonly TimeSpan CopyTimeout = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan HotkeyReleaseTimeout = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan ClipboardRetryDelay = TimeSpan.FromMilliseconds(40);
    private const int ClipboardRetryAttempts = 8;
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
            var sequenceBeforeCopy = GetClipboardSequenceNumber();

            SendKeys.SendWait("^c");

            var deadline = DateTime.UtcNow + CopyTimeout;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(PollInterval);

                if (GetClipboardSequenceNumber() == sequenceBeforeCopy)
                {
                    continue;
                }

                if (!TryContainsText(out var containsText))
                {
                    continue;
                }

                if (!containsText)
                {
                    continue;
                }

                if (!TryGetText(out var text))
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
        if (!await TrySetTextAsync(replacementText))
        {
            return ReplaceResult.Fail(
                "Requested Clipboard operation did not succeed.",
                Environment.TickCount64 - startedAt);
        }

        await Task.Delay(50);
        SendKeys.SendWait("^v");
        return ReplaceResult.Ok(Environment.TickCount64 - startedAt);
    }

    public static IDataObject? TryGetClipboardDataObject()
    {
        return TryClipboardOperation(Clipboard.GetDataObject, out IDataObject? data)
            ? data
            : null;
    }

    public static bool RestoreClipboard(IDataObject? originalClipboard)
    {
        if (originalClipboard is null)
        {
            return false;
        }

        return TryClipboardOperation(() =>
        {
            Clipboard.SetDataObject(originalClipboard, true);
            return true;
        }, out _);
    }

    public static Task<bool> TrySetReplacementTextAsync(string replacementText)
    {
        return TrySetTextAsync(replacementText);
    }

    private static async Task<bool> TrySetTextAsync(string text)
    {
        for (var attempt = 1; attempt <= ClipboardRetryAttempts; attempt++)
        {
            try
            {
                Clipboard.SetText(text, TextDataFormat.UnicodeText);
                return true;
            }
            catch (System.Runtime.InteropServices.ExternalException) when (attempt < ClipboardRetryAttempts)
            {
                await Task.Delay(ClipboardRetryDelay);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                return false;
            }
        }

        return false;
    }

    private static bool TryContainsText(out bool containsText)
    {
        return TryClipboardOperation(
            () => Clipboard.ContainsText(TextDataFormat.UnicodeText),
            out containsText);
    }

    private static bool TryGetText(out string text)
    {
        if (TryClipboardOperation(
            () => Clipboard.GetText(TextDataFormat.UnicodeText),
            out string? result))
        {
            text = result ?? "";
            return true;
        }

        text = "";
        return false;
    }

    private static bool TryClipboardOperation<T>(Func<T> operation, out T? result)
    {
        for (var attempt = 1; attempt <= ClipboardRetryAttempts; attempt++)
        {
            try
            {
                result = operation();
                return true;
            }
            catch (System.Runtime.InteropServices.ExternalException) when (attempt < ClipboardRetryAttempts)
            {
                Thread.Sleep(ClipboardRetryDelay);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                break;
            }
        }

        result = default;
        return false;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
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
