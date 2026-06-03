using System.Diagnostics;
using System.Runtime.InteropServices;

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
                0,
                $"mods=[{KeyboardState.DescribeModifierState()}]");
        }

        string? lastFailureReason = null;
        // Failure forensics, one entry per attempt. Built only on the failure
        // path — the success path pays for two GetAsyncKeyState snapshots and
        // nothing else.
        List<string>? failureDiag = null;
        for (var attempt = 1; attempt <= MaxCopyAttempts; attempt++)
        {
            var sequenceBeforeCopy = GetClipboardSequenceNumber();
            var modsAtSend = KeyboardState.DescribeModifierState();

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
                        attempt,
                        $"attempt={attempt} seq_before={sequenceBeforeCopy} seq_now={GetClipboardSequenceNumber()} " +
                        $"copied_len={text?.Length ?? 0} mods_at_send=[{modsAtSend}] fg=[{DescribeForegroundWindow()}]");
                }

                return CaptureResult.Ok(text, Environment.TickCount64 - startedAt, attempt);
            }

            lastFailureReason = "Clipboard did not change after Ctrl+C.";
            failureDiag ??= new List<string>(MaxCopyAttempts);
            failureDiag.Add(
                $"attempt={attempt} seq_before={sequenceBeforeCopy} seq_at_timeout={GetClipboardSequenceNumber()} " +
                $"mods_at_send=[{modsAtSend}] mods_at_timeout=[{KeyboardState.DescribeModifierState()}] " +
                $"fg_at_timeout=[{DescribeForegroundWindow()}]");
            await Task.Delay(60);
        }

        return CaptureResult.Fail(
            lastFailureReason ?? "Clipboard capture failed.",
            Environment.TickCount64 - startedAt,
            MaxCopyAttempts,
            failureDiag is null ? null : string.Join("; ", failureDiag));
    }

    // Identifies where the injected Ctrl+C actually went and whether Windows
    // could even deliver it: UIPI silently drops SendInput into elevated
    // processes, so elevated=1 (or access_denied) on a capture failure is the
    // smoking gun for "clipboard did not change after Ctrl+C".
    private static string DescribeForegroundWindow()
    {
        try
        {
            var handle = GetForegroundWindow();
            if (handle == IntPtr.Zero) return "none";
            GetWindowThreadProcessId(handle, out var processId);
            if (processId == 0) return "unknown";

            string name;
            try
            {
                name = Process.GetProcessById((int)processId).ProcessName;
            }
            catch
            {
                name = $"pid_{processId}";
            }

            return $"exe={name} elevated={DescribeElevation((int)processId)}";
        }
        catch
        {
            return "probe_failed";
        }
    }

    private static string DescribeElevation(int processId)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == IntPtr.Zero)
        {
            // Access denied here usually means an elevated or protected process.
            return "access_denied";
        }

        try
        {
            if (!OpenProcessToken(process, TOKEN_QUERY, out var token))
            {
                return "token_denied";
            }

            try
            {
                return GetTokenInformation(token, TokenElevationClass, out var elevated, sizeof(uint), out _)
                    ? (elevated != 0 ? "1" : "0")
                    : "unknown";
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
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

    // Re-asserts the just-captured (pre-correction) selection onto the
    // clipboard tagged with CanIncludeInClipboardHistory=0 and
    // CanUploadToCloudClipboard=0, so the transient incorrect text is excluded
    // from Windows clipboard history (Win+V) and the cloud clipboard. The
    // corrected text written later via Clipboard.SetText carries no such tag,
    // so it IS retained in history — that is the priority and is guaranteed
    // because the corrected write is the final clipboard state.
    //
    // This needs the calling app to own the clipboard: OpenClipboard with a
    // real owner window, EmptyClipboard (taking ownership — an HWND of NULL
    // here makes SetClipboardData fail), then place the text plus the two
    // policy DWORDs in one session. The text is re-placed so the clipboard
    // stays coherent during the request; only the history tag is new.
    //
    // Best-effort by nature: the source Ctrl+C already produced one untagged
    // clipboard update, so this races the OS history snapshot. It wins in
    // practice (this mirrors the proven legacy AHK behavior).
    //
    // Returns true iff the history-exclusion tag (CanIncludeInClipboardHistory)
    // was set — that is the bit that keeps the incorrect text out of Win+V.
    // `detail` is ALWAYS populated with a per-step status (format ids, owner
    // hwnd, and per-format win32 codes) so a test run shows exactly what
    // happened, success or failure, without needing a debugger.
    public static bool ExcludeTextFromHistory(string text, IntPtr ownerWindow, out string detail)
    {
        var inc = CfCanIncludeInClipboardHistory;
        var up = CfCanUploadToCloudClipboard;
        var ids = $"cf_include={inc} cf_upload={up} owner=0x{ownerWindow.ToInt64():X}";

        if (inc == 0 && up == 0)
        {
            detail = $"formats_unavailable {ids}";
            return false;
        }

        if (!OpenClipboard(ownerWindow))
        {
            detail = $"open_clipboard_failed win32={Marshal.GetLastWin32Error()} {ids}";
            return false;
        }

        try
        {
            if (!EmptyClipboard())
            {
                detail = $"empty_clipboard_failed win32={Marshal.GetLastWin32Error()} {ids}";
                return false;
            }

            var textOk = SetClipboardUnicodeText(text, out var textErr);

            var incOk = true;
            var incErr = 0;
            if (inc != 0) incOk = SetClipboardDword(inc, 0, out incErr);

            var upOk = true;
            var upErr = 0;
            if (up != 0) upOk = SetClipboardDword(up, 0, out upErr);

            detail =
                $"text={Step(true, textOk, textErr)} " +
                $"include={Step(inc != 0, incOk, incErr)} " +
                $"upload={Step(up != 0, upOk, upErr)} {ids}";

            // The history-exclusion tag is the thing that matters for priority #2.
            return inc != 0 && incOk;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static string Step(bool attempted, bool ok, int win32) =>
        !attempted ? "unavailable" : ok ? "ok" : $"fail(win32={win32})";

    private static bool SetClipboardUnicodeText(string text, out int win32)
    {
        win32 = 0;
        var byteCount = (text.Length + 1) * 2; // UTF-16 code units + null terminator
        var hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)byteCount);
        if (hMem == IntPtr.Zero)
        {
            win32 = Marshal.GetLastWin32Error();
            return false;
        }

        var ptr = GlobalLock(hMem);
        if (ptr == IntPtr.Zero)
        {
            win32 = Marshal.GetLastWin32Error();
            GlobalFree(hMem);
            return false;
        }

        try
        {
            Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
            Marshal.WriteInt16(ptr, text.Length * 2, 0); // null terminator
        }
        finally
        {
            GlobalUnlock(hMem);
        }

        if (SetClipboardData(CF_UNICODETEXT, hMem) == IntPtr.Zero)
        {
            win32 = Marshal.GetLastWin32Error();
            GlobalFree(hMem); // ownership not transferred to the system on failure
            return false;
        }

        return true;
    }

    private static bool SetClipboardDword(uint format, uint value, out int win32)
    {
        win32 = 0;
        var hMem = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, (UIntPtr)4);
        if (hMem == IntPtr.Zero)
        {
            win32 = Marshal.GetLastWin32Error();
            return false;
        }

        var ptr = GlobalLock(hMem);
        if (ptr == IntPtr.Zero)
        {
            win32 = Marshal.GetLastWin32Error();
            GlobalFree(hMem);
            return false;
        }

        Marshal.WriteInt32(ptr, 0, (int)value);
        GlobalUnlock(hMem);

        if (SetClipboardData(format, hMem) == IntPtr.Zero)
        {
            win32 = Marshal.GetLastWin32Error();
            GlobalFree(hMem);
            return false;
        }

        return true;
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

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevationClass = 20; // TOKEN_INFORMATION_CLASS.TokenElevation

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        out uint tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint GMEM_ZEROINIT = 0x0040;
    private const uint CF_UNICODETEXT = 13;

    // Registered once at type init. RegisterClipboardFormat returns the same id
    // for the same name across all processes; 0 means the format is unavailable.
    private static readonly uint CfCanIncludeInClipboardHistory =
        RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private static readonly uint CfCanUploadToCloudClipboard =
        RegisterClipboardFormat("CanUploadToCloudClipboard");

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);
}

internal sealed class CaptureResult
{
    public bool Success { get; init; }
    public string? Text { get; init; }
    public string? FailureReason { get; init; }
    // Per-attempt forensics (sequence numbers, modifier state, foreground
    // process + elevation). Populated only on failure.
    public string? Detail { get; init; }
    public long DurationMs { get; init; }
    public int Attempts { get; init; }

    public static CaptureResult Ok(string text, long durationMs, int attempts) => new()
    {
        Success = true,
        Text = text,
        DurationMs = durationMs,
        Attempts = attempts
    };

    public static CaptureResult Fail(string reason, long durationMs, int attempts, string? detail = null) => new()
    {
        Success = false,
        FailureReason = reason,
        Detail = detail,
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
