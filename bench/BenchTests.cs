using System.Text.Json;

namespace UniversalSpellCheck.Bench;

internal static class BenchTests
{
    internal static int Run()
    {
        void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine($"PASS {name}"); }
        var stats = JsonSerializer.SerializeToElement(Stats.For(new double?[] { 0, 0, 2, null }));
        Check(stats.GetProperty("count").GetInt32() == 3 && stats.GetProperty("median").GetDouble() == 0, "zeros retained, null omitted");
        Check(Math.Abs(Stats.Percentile(new double[] { 0, 10 }, .95) - 9.5) < .0001, "p95 interpolation");
        Check(JsonSerializer.SerializeToElement(Stats.For(Array.Empty<double?>())).GetProperty("mean").ValueKind == JsonValueKind.Null, "missing data stays null");
        Check(BenchHarness.Normalize("a\r\nb") == "a\nb" && BenchHarness.Normalize("a\nb\n") != "a\nb", "only CRLF is normalized");
        bool rejected = false;
        try { Options.Parse(new[] { "--runs", "0" }); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "zero repeat count rejected");
        rejected = false;
        try { Options.Parse(new[] { "--bogus" }); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "unknown arguments rejected");
        Check(BenchTargetForm.SelectionReady(true, true, 0, 12, 12), "focused full selection admitted");
        Check(!BenchTargetForm.SelectionReady(false, true, 0, 12, 12), "background editor refuses injection");
        Check(!BenchTargetForm.SelectionReady(true, false, 0, 12, 12), "unfocused textbox refuses injection");
        Check(!BenchTargetForm.SelectionReady(true, true, 1, 11, 12)
            && !BenchTargetForm.SelectionReady(true, true, 0, 0, 0), "partial and empty selections refuse injection");
        var logger = new CapturingLogger(Path.Combine("bench", "results", "self-tests", Guid.NewGuid().ToString("N"), "events.log"));
        _ = logger.BeginTrial();
        logger.LogData("spellcheck_detail", new { run_id = "test" });
        Check(!logger.EvidenceCompleted.IsCompleted, "pipeline detail does not imply evidence completion");
        logger.LogData("developer_evidence_completed", new { run_id = "test" });
        Check(logger.EvidenceCompleted.IsCompletedSuccessfully, "separate evidence completion observed");
        var failedFixture = logger.BeginTrial();
        logger.FailCurrentTrial(new InvalidOperationException("copy fixture failure"));
        Check(failedFixture.IsFaulted && failedFixture.Exception!.ToString().Contains("copy fixture failure"),
            "fixture UI errors reach the trial instead of a modal dialog or silent timeout");
        var isolationRoot = Path.Combine("bench", "results", "isolation-tests", Guid.NewGuid().ToString("N"));
        var isolated = new SettingsStore(logger, isolationRoot, Path.Combine(isolationRoot, Path.GetFileName(AppPaths.ApiKeyPath)));
        Program.VerifyOfflineKeyPath(isolated, isolationRoot);
        var misdirected = new SettingsStore(logger, isolationRoot, Path.Combine(isolationRoot, "wrong-key-file.dat"));
        bool writeRefused = false;
        try { Program.VerifyOfflineKeyPath(misdirected, isolationRoot); } catch (InvalidOperationException) { writeRefused = true; }
        Check(writeRefused && !File.Exists(Path.Combine(isolationRoot, "wrong-key-file.dat")),
            "misdirected offline key path rejected before any write");
        using (var window = new HotkeyWindow("UniversalSpellCheck.Benchmark.Tests." + Guid.NewGuid().ToString("N")))
        {
            int dispatched = 0;
            window.HotkeyPressed += (_, _) => dispatched++;
            for (int i = 0; i < 100; i++)
            {
                HotkeyInjector.PostRegisteredMessage(window.Handle);
                System.Windows.Forms.Application.DoEvents();
                Check(dispatched == i + 1, $"acknowledged native hotkey message {i + 1}");
            }
        }
        var cases = JsonSerializer.Deserialize<List<BenchCase>>(File.ReadAllText("bench/baseline-cases.json"), Program.Json)!;
        Check(cases.Count == 10 && cases.Select(c => c.Id).Distinct().Count() == 10, "ten distinct fixed cases");
        foreach (var c in cases.Where(c => c.Html.Length > 0))
        {
            var rich = RichTextClipboard.TryCreateReplacement(c.Html, c.Text, c.Expected);
            Console.WriteLine($"{c.Id}: applied={rich.Applied} reason={rich.Reason} mode={rich.Mode} text={rich.Text}");
            Check(rich.Applied && rich.Text == c.Expected && rich.Html.Contains("<a ") == c.Html.Contains("<a "), $"{c.Id} real mapper retains rich structure");
        }
        return 0;
    }
}
