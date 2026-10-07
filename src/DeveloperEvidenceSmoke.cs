using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace UniversalSpellCheck;

// Exercises native capture/paste and evidence in the rebuilt app without an API request.
internal static class DeveloperEvidenceSmoke
{
    public static int Run()
    {
        var logger = new DiagnosticsLogger(() => AppPaths.LogPath);
        Exception? failure = null;
        using var timeout = new System.Threading.Timer(_ => Environment.Exit(1), null, 20000, Timeout.Infinite);
        using var form = new Form { Text = "Developer evidence disposable fixture", Width = 700, Height = 350 };
        using var editor = new RichTextBox { Dock = DockStyle.Fill, Text = "This is teh bold paragraph." };
        form.Controls.Add(editor);
        form.Shown += async (_, _) =>
        {
            var originalClipboard = ClipboardLoop.TryGetClipboardDataObject();
            try
            {
                form.Show(); form.BringToFront();
                var foreground = GetForegroundWindow();
                var foregroundThread = GetWindowThreadProcessId(foreground, out _);
                var thread = GetCurrentThreadId();
                AttachThreadInput(thread, foregroundThread, true);
                try { SetForegroundWindow(form.Handle); BringWindowToTop(form.Handle); }
                finally { AttachThreadInput(thread, foregroundThread, false); }
                SwitchToThisWindow(form.Handle, true);
                form.Activate(); editor.Focus(); editor.SelectAll();
                await Task.Delay(300);
                var settings = new AppSettings();
                var settingsDirectory = Path.Combine(Path.GetTempPath(), "usc-evidence-smoke-" + Guid.NewGuid().ToString("N"));
                var store = new SettingsStore(logger, settingsDirectory, Path.Combine(settingsDirectory, "unused-api-key"));
                var cached = new CachedSettings(store);
                Assert(!cached.DeveloperLogging, "Cache default must be disabled.");
                store.Save(new AppSettings { DeveloperLogging = true });
                Assert(cached.DeveloperLogging, "Enabled setting must apply live.");
                store.Save(new AppSettings { DeveloperLogging = false });
                Assert(!cached.DeveloperLogging, "Disabled setting must apply live.");
                Assert(!settings.DeveloperLogging, "Default must be disabled.");
                var target = ActiveWindowInfo.Capture();
                Assert(target.ProcessId == Environment.ProcessId, "Fixture must own focus.");
                var record = new RunRecord { ActiveWindowAtStart = target };
                record.Evidence = DeveloperEvidence.TryCreate(record.RunId)!;
                var started = Stopwatch.StartNew();
                record.Evidence.CaptureContext("before-source", target);
                var scheduledMs = started.ElapsedMilliseconds;
                Assert(scheduledMs < 100, "Optional capture must return immediately.");
                var capture = await ClipboardLoop.CaptureSelectionAsync();
                Assert(capture.Success, "Native selected text capture failed: " + capture.FailureReason + " " + capture.Detail);
                record.InputText = capture.Text; record.CapturedHtml = capture.Html;
                record.CapturedRtf = capture.Rtf; record.ClipboardFormats = capture.Formats;
                Assert(capture.Rtf.Length > 0, "Rich editor source must expose RTF.");
                record.OutputText = "This is the bold paragraph.";
                record.RichTextReplacement = RichTextClipboard.TryCreateReplacement(capture.Html, capture.Text!, record.OutputText);
                record.ReplacementClipboard = await ClipboardLoop.TrySetReplacementTextAsync(record.RichTextReplacement.Text, record.RichTextReplacement.Html, true);
                Assert((await record.ReplacementClipboard.ReadbackCapture!).Text == record.OutputText, "Enabled readback mismatch.");
                await Task.Delay(250);
                record.Evidence.MarkPasteIssued(); SendKeys.SendWait("^v");
                record.Evidence.CaptureContext("after-paste", ActiveWindowInfo.Capture(), true);
                await Task.Delay(1100);
                Assert(editor.Text == record.OutputText, "Actual native editor paste mismatch.");
                var reference = record.Evidence.Save(record);
                logger.LogData("developer_evidence_smoke", new { status = "enabled_ok", developer_evidence = reference });
                var json = JsonSerializer.SerializeToElement(reference);
                Assert(json.GetProperty("status").GetString() == "ok", "Manifest save failed.");
                var path = Path.Combine(AppPaths.LogDirectory, json.GetProperty("manifest_path").GetString()!);
                using var manifest = JsonDocument.Parse(File.ReadAllText(path));
                var input = manifest.RootElement.GetProperty("payloads").GetProperty("input_text");
                Assert(input.GetProperty("complete").GetBoolean(), "Input must be complete.");
                Assert(File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, input.GetProperty("path").GetString()!)) == capture.Text, "Saved source mismatch.");
                var contexts = manifest.RootElement.GetProperty("contexts");
                Assert(contexts.GetArrayLength() == 2, "Before/after contexts missing.");
                Assert(contexts[0].GetProperty("image").GetProperty("status").GetString() == "ok", "Before image missing.");
                Assert(contexts[1].GetProperty("image").GetProperty("status").GetString() == "ok", "After image missing.");
                var disabled = await ClipboardLoop.TrySetReplacementTextAsync("disabled check", "", false);
                Assert(disabled.ReadbackCapture is null, "Disabled logging must not capture readback.");
                var large = new RunRecord { InputText = new string('x', 600000), OutputText = "output" };
                large.Evidence = DeveloperEvidence.TryCreate(large.RunId)!;
                var largeRef = JsonSerializer.SerializeToElement(large.Evidence.Save(large));
                using var largeManifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppPaths.LogDirectory, largeRef.GetProperty("manifest_path").GetString()!)));
                Assert(largeManifest.RootElement.GetProperty("payloads").GetProperty("input_text").GetProperty("chars").GetInt32() == 600000, "Above inline cap must survive.");
                logger.LogData("developer_evidence_smoke", new { status = "ok", disabled_readback = true, large_payload_chars = 600000, capture_schedule_ms = scheduledMs });
            }
            catch (Exception ex) { failure = ex; logger.LogData("developer_evidence_smoke_failed", new { error = ex.Message, stack = ex.ToString() }); }
            finally { ClipboardLoop.RestoreClipboard(originalClipboard); form.Close(); }
        };
        System.Windows.Forms.Application.Run(form);
        return failure is null ? 0 : 1;
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint one, uint two, bool attach);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern void SwitchToThisWindow(IntPtr hwnd, bool altTab);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
