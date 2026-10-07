using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UniversalSpellCheck;

internal static class DeveloperEvidenceTests
{
    public static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "usc-evidence-tests-" + Guid.NewGuid().ToString("N"));
        var logger = new DiagnosticsLogger(Path.Combine(root, $"spellcheck-{DateTime.Now:yyyy-MM-dd}.jsonl"));
        try
        {
            var record = new RunRecord
            {
                InputText = "teh 🐈\r\n" + new string('x', 600000), OutputText = "the 🐈\r\n",
                CapturedHtml = "<b>teh 🐈</b>", CapturedRtf = "{\\rtf1 teh}",
                ClipboardFormats = "UnicodeText,HTML Format,Rich Text Format"
            };
            var evidence = DeveloperEvidence.TryCreate(record.RunId, root)!;
            var reference = JsonSerializer.SerializeToElement(evidence.Save(record));
            var directory = Path.Combine(root, "developer-evidence", record.RunId);
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
            var input = manifest.RootElement.GetProperty("payloads").GetProperty("input_text");
            var bytes = File.ReadAllBytes(Path.Combine(directory, input.GetProperty("path").GetString()!));
            Check(input.GetProperty("complete").GetBoolean(), "Full source must be complete.");
            Check(Encoding.UTF8.GetString(bytes) == record.InputText, "UTF8 source must roundtrip.");
            Check(input.GetProperty("sha256").GetString() == Convert.ToHexStringLower(SHA256.HashData(bytes)), "Hash must match artifact.");
            Check(input.GetProperty("chars").GetInt32() == record.InputText.Length, "Characters must count UTF16 code units.");
            Check(manifest.RootElement.GetProperty("run_id").GetString() == record.RunId, "Stable ID mismatch.");

            var incomplete = new RunRecord { InputText = "text", OutputText = "text", ClipboardFormats = "UnicodeText,HTML Format" };
            DeveloperEvidence.TryCreate(incomplete.RunId, root)!.Save(incomplete);
            using var missing = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "developer-evidence", incomplete.RunId, "manifest.json")));
            Check(!missing.RootElement.GetProperty("payloads").GetProperty("clipboard_html").GetProperty("complete").GetBoolean(), "Offered unreadable markup must be incomplete.");
            Check(missing.RootElement.GetProperty("payloads").GetProperty("clipboard_rtf").GetProperty("complete").GetBoolean(), "Confirmed absent RTF must be complete empty.");

            var oversized = new RunRecord { InputText = new string('x', 16 * 1024 * 1024 + 1), ClipboardFormats = "UnicodeText" };
            DeveloperEvidence.TryCreate(oversized.RunId, root)!.Save(oversized);
            using var large = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "developer-evidence", oversized.RunId, "manifest.json")));
            Check(large.RootElement.GetProperty("payloads").GetProperty("input_text").GetProperty("status").GetString() == "size_limit", "Oversized payload must be explicit.");

            var fullRoot = Path.Combine(root, "full");
            Directory.CreateDirectory(Path.Combine(fullRoot, "developer-evidence"));
            File.WriteAllText(Path.Combine(fullRoot, "developer-evidence", ".reserved-bytes"), (256L * 1024 * 1024).ToString());
            var full = new RunRecord { InputText = "unchanged" };
            var fullRef = JsonSerializer.SerializeToElement(DeveloperEvidence.TryCreate(full.RunId, fullRoot)!.Save(full));
            Check(fullRef.GetProperty("status").GetString() == "storage_limit", "Full store must skip explicitly.");
            Check(!Directory.Exists(Path.Combine(fullRoot, "developer-evidence", full.RunId)), "Full store must not write artifacts.");

            var admitted1 = DeveloperEvidence.TryCreate("test1", root)!;
            var admitted2 = DeveloperEvidence.TryCreate("test2", root)!;
            Check(DeveloperEvidence.TryCreate("test3", root) is null, "Optional evidence must have bounded admission.");
            admitted1.Save(new RunRecord()); admitted2.Save(new RunRecord());
            var orderingRoot = Path.Combine(root, "ordering");
            var orderingLog = Path.Combine(orderingRoot, "spellcheck-ordering.jsonl");
            var orderingLogger = new DiagnosticsLogger(orderingLog);
            var settings = new SettingsStore(orderingLogger, orderingRoot, Path.Combine(orderingRoot, "unused-api-key"));
            using var service = new OpenAiSpellcheckService(new CachedSettings(settings), orderingLogger);
            using var coordinator = new SpellcheckCoordinator(orderingLogger, service, new TextPostProcessor(orderingLogger),
                new TargetFormattingPipeline(), (_, _) => { }, _ => { }, () => { });
            var ordering = new RunRecord { InputText = "teh", OutputText = "the", DeveloperLoggingEnabled = true };
            ordering.Evidence = DeveloperEvidence.TryCreate(ordering.RunId, orderingRoot)!;
            using var heldQuota = new Mutex(false, BuildChannel.DeveloperEvidenceMutexName);
            heldQuota.WaitOne();
            Task finalization;
            try
            {
                finalization = Task.Run(() => typeof(SpellcheckCoordinator)
                    .GetMethod("FinalizeAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(coordinator, [ordering]));
                var deadline = System.Diagnostics.Stopwatch.StartNew();
                while (deadline.ElapsedMilliseconds < 700 &&
                    (!File.Exists(orderingLog) || !File.ReadAllText(orderingLog).Contains("spellcheck_detail"))) Thread.Sleep(10);
                var ordinaryLogs = File.Exists(orderingLog) ? File.ReadAllText(orderingLog) : "";
                Check(ordinaryLogs.Contains("run_completed") && ordinaryLogs.Contains("spellcheck_detail"),
                    "Ordinary logs must precede optional storage waits.");
                Check(ordinaryLogs.Contains("\"status\":\"pending\""), "Detail must link pending evidence.");
                Check(!finalization.IsCompleted, "Test must hold optional evidence storage pending.");
            }
            finally { heldQuota.ReleaseMutex(); }
            Check(finalization.Wait(5000), "Evidence finalization must complete after quota release.");
            var completeLogs = File.ReadAllText(orderingLog);
            Check(completeLogs.Contains("developer_evidence_completed"), "Final evidence status event missing.");
            Check(completeLogs.Split("spellcheck_detail").Length == 2, "Ordinary detail must not be duplicated.");
            logger.Log("developer_evidence_tests_ok");
            return 0;
        }
        catch (Exception ex) { logger.LogData("developer_evidence_tests_failed", new { error = ex.Message, stack = ex.ToString() }); return 1; }
    }
    private static void Check(bool passed, string reason) { if (!passed) throw new InvalidOperationException(reason); }
}
