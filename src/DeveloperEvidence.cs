using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;

namespace UniversalSpellCheck;

// Optional evidence never participates in the correction's success or completion.
internal sealed class DeveloperEvidence
{
    private static readonly object StorageGate = new();
    private static int _activeJobs;

    private static int _imageBusy;
    private static int _accessibilityBusy;
    private const int MaxPayloadBytes = 16 * 1024 * 1024;
    private const long MaxStoreBytes = 256L * 1024 * 1024;
    private readonly string _runId;
    private readonly string _logDirectory;
    private readonly List<Task<object>> _contexts = [];
    private long _pasteIssued;
    private DeveloperEvidence(string runId, string? logDirectory)
    {
        _runId = runId;
        _logDirectory = logDirectory ?? AppPaths.LogDirectory;
    }
    public static DeveloperEvidence? TryCreate(string runId, string? logDirectory = null)
    {
        if (Interlocked.Increment(ref _activeJobs) <= 2) return new(runId, logDirectory);
        Interlocked.Decrement(ref _activeJobs);
        return null;
    }
    public void MarkPasteIssued() => Interlocked.Exchange(ref _pasteIssued, DateTimeOffset.UtcNow.UtcTicks);

    public void CaptureContext(string phase, ActiveWindowInfo target, bool afterPaste = false)
    {
        var requested = DateTimeOffset.UtcNow;
        _contexts.Add(Task.Run(async () =>
        {
            if (afterPaste) await Task.Delay(150).ConfigureAwait(false);
            var image = CaptureImage(phase, target);
            var accessibility = await CaptureAccessibility(target, () => phase == "before-source" && Interlocked.Read(ref _pasteIssued) != 0).ConfigureAwait(false);
            return (object)new { phase, requested_at = requested, target = Identity(target), image, accessibility };
        }));
    }

    private static string Limit(string value, int limit) => value.Length > limit ? value[..limit] : value;

    private static object Identity(ActiveWindowInfo window) => new
    {
        process = window.ProcessName, pid = window.ProcessId,
        hwnd = window.WindowHandle.ToInt64(), title = window.WindowTitle
    };

    private object CaptureImage(string phase, ActiveWindowInfo target)
    {
        if (Interlocked.CompareExchange(ref _imageBusy, 1, 0) != 0)
            return new { status = "busy" };
        try
        {
            var started = DateTimeOffset.UtcNow;
            if (phase == "before-source" && Interlocked.Read(ref _pasteIssued) != 0)
                return new { status = "late_before_capture", captured_at = started };
            if (GetForegroundWindow() != target.WindowHandle)
                return new { status = "foreground_changed", captured_at = started };
            if (!GetWindowRect(target.WindowHandle, out var rect))
                return new { status = "window_unavailable", captured_at = started };
            var bounds = Rectangle.Intersect(new Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top), SystemInformation.VirtualScreen);
            if (bounds.Width <= 0 || bounds.Height <= 0 || (long)bounds.Width * bounds.Height > 16_000_000)
                return new { status = "image_size_limit", captured_at = started };
            using var bitmap = new Bitmap(bounds.Width, bounds.Height);
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            var ended = DateTimeOffset.UtcNow;
            if (GetForegroundWindow() != target.WindowHandle)
                return new { status = "foreground_changed_during_capture", captured_at = ended };
            if (phase == "before-source" && Interlocked.Read(ref _pasteIssued) != 0)
                return new { status = "late_before_capture", captured_at = ended };
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            var reference = WriteBytes(phase + ".png", stream.ToArray());
            return new { status = "ok", captured_at = started, capture_completed_at = ended, bounds, visibility = "visible_screen_pixels_may_include_overlays", artifact = new { path = reference.Path, sha256 = reference.Hash, status = reference.Status, complete = reference.Status == "ok" } };
        }
        catch (Exception ex) { return new { status = "failed", error_type = ex.GetType().Name }; }
        finally { Volatile.Write(ref _imageBusy, 0); }
    }

    private static async Task<object> CaptureAccessibility(ActiveWindowInfo target, Func<bool> late)
    {
        if (Interlocked.CompareExchange(ref _accessibilityBusy, 1, 0) != 0)
            return new { status = "busy_or_previous_provider_stuck" };
        var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                var started = DateTimeOffset.UtcNow;
                if (late())
                { completion.TrySetResult(new { status = "late_before_capture", captured_at = started }); return; }
                if (GetForegroundWindow() != target.WindowHandle)
                { completion.TrySetResult(new { status = "foreground_changed", captured_at = started }); return; }
                var element = AutomationElement.FocusedElement;
                if (element is null || element.Current.ProcessId != target.ProcessId)
                { completion.TrySetResult(new { status = "focused_editor_unavailable", captured_at = started }); return; }
                var current = element.Current;
                if (current.IsPassword)
                { completion.TrySetResult(new { status = "password_control_excluded", captured_at = started }); return; }
                string? text = null;
                string[] selection = [];
                var rangeCount = 0;
                string textStatus = "unsupported";
                if (element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern))
                {
                    var textPattern = (TextPattern)pattern;
                    text = textPattern.DocumentRange.GetText(32769);
                    var ranges = textPattern.GetSelection();
                    rangeCount = ranges.Length;
                    selection = ranges.Take(8).Select(range => range.GetText(8193)).ToArray();
                    textStatus = text.Length > 32768 ? "truncated" : "ok";
                    if (text.Length > 32768) text = text[..32768];
                }
                var bounds = current.BoundingRectangle;
                var controlBounds = bounds.IsEmpty || !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
                    !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) ? null :
                    (object)new { x = bounds.X, y = bounds.Y, width = bounds.Width, height = bounds.Height };
                var name = current.Name;
                var automationId = current.AutomationId;
                var enabled = current.IsEnabled;
                var focused = current.HasKeyboardFocus;
                var controlType = current.ControlType.ProgrammaticName;
                completion.TrySetResult(new
                {
                    status = late() ? "late_before_capture" : GetForegroundWindow() == target.WindowHandle ? "ok" : "foreground_changed_during_capture",
                    captured_at = started, completed_at = DateTimeOffset.UtcNow,
                    control_bounds = controlBounds, is_enabled = enabled, has_keyboard_focus = focused,
                    control_type = controlType, name = Limit(name, 1024), name_truncated = name.Length > 1024,
                    automation_id = Limit(automationId, 1024), automation_id_truncated = automationId.Length > 1024, text, text_status = textStatus,
                    selection, selection_range_count = rangeCount,
                    selection_status = rangeCount > 8 || selection.Any(value => value.Length > 8192) ? "truncated" : text is null ? "unsupported" : selection.All(value => value.Length == 0) ? "caret_or_no_selection" : "available",
                    selection_offsets = "not_captured"
                });
            }
            catch (Exception ex) { completion.TrySetResult(new { status = "failed", error_type = ex.GetType().Name }); }
            finally { Volatile.Write(ref _accessibilityBusy, 0); }
        }) { IsBackground = true, Name = "Formatting evidence UIA" };
        try
        {
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _accessibilityBusy, 0);
            return new { status = "worker_start_failed", error_type = ex.GetType().Name };
        }
        var finished = await Task.WhenAny(completion.Task, Task.Delay(750)).ConfigureAwait(false);
        return finished == completion.Task ? await completion.Task.ConfigureAwait(false) : new { status = "timeout", timeout_ms = 750 };
    }

    public object PendingReference => new { schema_version = 1, manifest_path = $"developer-evidence/{_runId}/manifest.json", status = "pending" };

    public object Save(RunRecord run)
    {
        var manifestPath = $"developer-evidence/{_runId}/manifest.json";
        try
        {
            var readback = run.ReplacementClipboard.ReadbackCapture?.GetAwaiter().GetResult() ?? run.ReplacementClipboard.Readback;
            if (readback is not null)
            {
                var expected = run.RichTextReplacement.Html;
                run.ReplacementClipboard = run.ReplacementClipboard with
                {
                    Readback = readback, VerifiedFormats = readback.Formats ?? "", VerifiedHtmlChars = readback.Html?.Length ?? 0,
                    HtmlVerification = readback.Status != "ok" ? readback.Status : readback.HtmlStatus == "failed" ? "read_failed" :
                        expected.Length == 0 ? "not_requested" : readback.HtmlStatus == "absent" ? "missing_html" :
                        expected == readback.Html ? "exact_match" : "mismatch"
                };
            }
            var payloads = new Dictionary<string, object>();
            foreach (var (name, value) in new Dictionary<string, string?>
            {
                ["input_text"] = run.InputText, ["clipboard_html"] = run.CapturedHtml,
                ["clipboard_rtf"] = run.CapturedRtf, ["clipboard_formats"] = run.ClipboardFormats,
                ["raw_ai_output"] = run.RawAiOutput, ["output_text"] = run.OutputText,
                ["paste_text"] = run.RichTextReplacement.Text, ["paste_html"] = run.RichTextReplacement.Html,
                ["readback_text"] = readback?.Text, ["readback_html"] = readback?.Html,
                ["readback_rtf"] = readback?.Rtf, ["readback_formats"] = readback?.Formats
            })
            {
                payloads[name] = value is null ? new { status = "unavailable", complete = false } :
                    (name is "clipboard_html" or "clipboard_rtf") && value.Length == 0 &&
                    (run.ClipboardFormats.Length == 0 || run.ClipboardFormats.Split(',').Contains(name == "clipboard_html" ? DataFormats.Html : DataFormats.Rtf))
                    ? new { status = "source_format_read_unconfirmed", complete = false } : WritePayload(name, value);
            }
            var contexts = _contexts.Select(task =>
                ObserveContext(task)).ToArray();
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema_version = 1, run_id = _runId, channel = BuildChannel.ChannelName,
                app_version = BuildChannel.AppVersion, created_at = DateTimeOffset.UtcNow,
                source_target = Identity(run.ActiveWindowAtStart),
                paste_target = run.ActiveWindowAtPaste is null ? null : Identity(run.ActiveWindowAtPaste),
                destination_before_context = run.ActiveWindowAtPaste is null ? "no_paste" :
                    run.ActiveWindowAtPaste.WindowHandle == run.ActiveWindowAtStart.WindowHandle ? "source_window_capture_available_if_timely" : "unavailable_destination_changed",
                payloads, contexts, source_clipboard_captured_at = run.SourceClipboardCapturedAt,
                paste_issued_at = Interlocked.Read(ref _pasteIssued) == 0 ? (DateTimeOffset?)null :
                    new DateTimeOffset(Interlocked.Read(ref _pasteIssued), TimeSpan.Zero),
                clipboard_readback_at = readback?.CapturedAt,
                clipboard_readback_phase = readback is null ? "not_captured" :
                    Interlocked.Read(ref _pasteIssued) == 0 ? "no_paste" :
                    readback.CapturedAt.UtcTicks < Interlocked.Read(ref _pasteIssued) ? "before_paste" : "after_paste",
                readback_status = readback is null ? null : new { status = readback.Status, sequence = readback.Sequence, text = readback.TextStatus, html = readback.HtmlStatus, rtf = readback.RtfStatus },
                capture_contract = "Decoded Unicode/CF_HTML/RTF strings; proprietary clipboard formats are listed but not serialized."
            });
            var saved = WriteBytes("manifest.json", manifest);
            return new { schema_version = 1, manifest_path = manifestPath, status = saved.Status };
        }
        catch (Exception ex) { return new { schema_version = 1, manifest_path = manifestPath, status = "failed", error_type = ex.GetType().Name }; }
        finally { Interlocked.Decrement(ref _activeJobs); }
    }

    private static object ObserveContext(Task<object> task)
    {
        try { return task.Wait(1500) ? task.GetAwaiter().GetResult() : new { status = "capture_pending_or_timeout" }; }
        catch (Exception ex) { return new { status = "failed", error_type = ex.GetType().Name }; }
    }

    private object WritePayload(string name, string value)
    {
        var bytes = Encoding.UTF8.GetByteCount(value);
        if (bytes > MaxPayloadBytes) return new { status = "size_limit", complete = false, chars = value.Length, bytes };
        var saved = WriteBytes(name + ".txt", Encoding.UTF8.GetBytes(value));
        return new { path = saved.Path, sha256 = saved.Hash, bytes, chars = value.Length, complete = saved.Status == "ok", status = saved.Status };
    }

    private Artifact WriteBytes(string name, byte[] bytes)
    {
        lock (StorageGate)
        {
            var root = Path.Combine(_logDirectory, "developer-evidence");
            Directory.CreateDirectory(root);
            using var quotaMutex = new Mutex(false, BuildChannel.DeveloperEvidenceMutexName);
            var acquired = false;
            try
            {
                try { acquired = quotaMutex.WaitOne(1000); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) return new(name, "", "storage_busy");
                var quotaPath = Path.Combine(root, ".reserved-bytes");
                long storedBytes;
                if (File.Exists(quotaPath) && long.TryParse(File.ReadAllText(quotaPath), out var reserved) && reserved >= 0)
                    storedBytes = reserved;
                else
                {
                    storedBytes = 0;
                    var count = 0;
                    foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    {
                        storedBytes += new FileInfo(path).Length;
                        if (++count > 20000 || storedBytes > MaxStoreBytes) { storedBytes = MaxStoreBytes; break; }
                    }
                }
                if (storedBytes >= MaxStoreBytes || storedBytes + bytes.Length > MaxStoreBytes) return new(name, "", "storage_limit");
                // Reserve first: failed writes may overcount but cannot exceed the quota.
                File.WriteAllText(quotaPath, (storedBytes + bytes.Length).ToString(System.Globalization.CultureInfo.InvariantCulture));
                var directory = Path.Combine(root, _runId);
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(Path.Combine(directory, name), bytes);
            }
            finally { if (acquired) quotaMutex.ReleaseMutex(); }
            return new(name, Convert.ToHexStringLower(SHA256.HashData(bytes)), "ok");
        }
    }
    private sealed record Artifact(string Path, string Hash, string Status);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
}
