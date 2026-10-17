using System.Diagnostics;
using System.Text.Json;
using System.Windows.Forms;

namespace UniversalSpellCheck.Bench;

internal sealed record Trial(string CaseId, int Index, bool Warmup, bool Success,
    bool QualityMatch, Dictionary<string, double?> Timings, string Output, string? Error);

internal sealed class BenchHarness(Options opts, List<BenchCase> cases, List<ReplayFixture> fixtures,
    OpenAiSpellcheckService service, TextPostProcessor post, SpellcheckCoordinator coordinator, CapturingLogger logger)
{
    internal List<ReplayFixture> Fixtures { get; } = fixtures;
    internal Func<Task?> CoordinatorTask { get; set; } = () => null;
    internal Action TriggerHotkey { get; set; } = () => throw new InvalidOperationException("No hotkey target.");
    private TaskCompletionSource<long>? _dispatch;
    internal void MarkDispatched() => _dispatch?.TrySetResult(Stopwatch.GetTimestamp());

    internal async Task<int> RunAsync(BenchTargetForm? form)
    {
        var trials = new List<Trial>();
        using var raw = new StreamWriter(Path.Combine(opts.Output, "trials.jsonl"), append: false) { AutoFlush = true };
        var aborted = false;
        foreach (var c in cases)
        {
            for (int i = -opts.Warmup; i < opts.Runs; i++)
            {
                bool warmup = i < 0;
                Trial trial;
                try
                {
                    trial = opts.Mode == "compute" ? await ComputeAsync(c, i, warmup)
                        : await PipelineAsync(c, i, warmup, form);
                }
                catch (Exception e)
                {
                    trial = new(c.Id, i, warmup, false, false, new(), "", e.Message);
                }
                trials.Add(trial);
                raw.WriteLine(JsonSerializer.Serialize(trial, Program.Json).Replace("\r", "").Replace("\n", ""));
                ResultsWriter.Write(Path.Combine(opts.Output, "summary.json"), trials);
                Console.WriteLine($"{opts.Mode} {c.Id} {(warmup ? "warmup" : "trial")} {i + 1}: {(trial.Success ? "ok" : "FAIL")} quality={trial.QualityMatch} {trial.Error}");
                // A desktop failure can mean focus/selection changed. Stop rather than sending more keys.
                if (opts.Desktop && !trial.Success && (trial.Timings.Count == 0 || form is null || !form.Foreground))
                { aborted = true; break; }
                if (opts.Desktop) await Task.Delay(150);
            }
            if (aborted) break;
        }
        return aborted ? 3 : trials.Any(t => !t.Success) ? 1 : 0;
    }

    private async Task<Trial> ComputeAsync(BenchCase c, int index, bool warmup)
    {
        var expected = Fixtures.Single(f => f.Id == c.Id);
        var total = Stopwatch.GetTimestamp();
        var start = total;
        var protection = ProtectedText.Protect(c.Text);
        double protectMs = Elapsed(start);
        var record = new RunRecord();
        start = Stopwatch.GetTimestamp();
        var result = await service.SpellcheckAsync(protection.Text, record);
        double serviceMs = Elapsed(start);
        start = Stopwatch.GetTimestamp();
        var pp = post.Process(result.OutputText ?? "", protection);
        double postMs = Elapsed(start);
        start = Stopwatch.GetTimestamp();
        var rich = RichTextClipboard.TryCreateReplacement(c.Html, c.Text, pp.Text);
        double richMs = Elapsed(start);
        var ms = new Dictionary<string, double?>
        {
            ["total_ms"] = Elapsed(total), ["protect_ms"] = protectMs,
            ["replay_service_ms"] = serviceMs, ["post_process_ms"] = postMs,
            ["rich_map_ms"] = richMs, ["request_ms"] = null, ["capture_ms"] = null, ["paste_ms"] = null
        };
        bool exact = result.Success && pp.ProtectionRestored && pp.Text == expected.Output
            && rich.Text == expected.PasteText && rich.Html == expected.PasteHtml;
        return new(c.Id, index, warmup, exact, Normalize(pp.Text) == Normalize(c.Expected), ms, pp.Text,
            exact ? null : "Recorded output or rich mapping differs from frozen reference.");
    }

    private async Task<Trial> PipelineAsync(BenchCase c, int index, bool warmup, BenchTargetForm? form)
    {
        if (form is not null)
        {
            await form.PrepareAsync(c);
            form.Arm();
        }
        var pending = logger.BeginTrial();
        if (form is not null) _dispatch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = Stopwatch.GetTimestamp();
        if (form is null)
            await coordinator.RunHeadlessAsync(c.Text);
        else
            TriggerHotkey();
        var returnedMs = Elapsed(start);
        var dispatchedAt = form is null ? 0 : await _dispatch!.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var detail = await pending.WaitAsync(TimeSpan.FromSeconds(70));
        if (form is not null && CoordinatorTask() is { } running) await running;
        if (form is not null && detail.GetProperty("developer_evidence").TryGetProperty("status", out var evidenceStatus)
            && evidenceStatus.GetString() == "pending")
        {
            var completed = await logger.EvidenceCompleted.WaitAsync(TimeSpan.FromSeconds(15));
            if (completed.GetProperty("run_id").GetString() != detail.GetProperty("run_id").GetString())
                throw new InvalidOperationException("Evidence completion belongs to another trial.");
        }
        var output = detail.GetProperty("output_text").GetString() ?? "";
        var rawOutput = detail.GetProperty("raw_ai_output").GetString() ?? "";
        var timings = detail.GetProperty("timings").EnumerateObject()
            .ToDictionary(p => p.Name, p => (double?)p.Value.GetDouble());
        // These production log fields are placeholders, not measurements.
        timings["payload_ms"] = null; timings["parse_ms"] = null;
        timings["coordinator_total_ms"] = timings["total_ms"];
        timings["hotkey_dispatch_ms"] = form is null ? null : (dispatchedAt - start) * 1000.0 / Stopwatch.Frequency;
        timings["total_ms"] = form is null ? returnedMs
            : form.LastPasteTicks == 0 ? null : (form.LastPasteTicks - start) * 1000.0 / Stopwatch.Frequency;
        if (form is null)
            foreach (var field in new[] { "clipboard_ms", "capture_ms", "paste_ms", "after_copy_format_ms", "before_paste_format_ms" })
                timings[field] = null;
        else timings["capture_ms"] = timings["clipboard_ms"];
        bool success = detail.GetProperty("status").GetString() == "success";
        string? error = success ? null : detail.GetProperty("error").GetString();
        var rich = RichTextClipboard.TryCreateReplacement(c.Html, c.Text, output);
        if (form is not null)
        {
            if (success && c.Html.Length > 0 && detail.GetProperty("clipboard_html").GetString() != c.Html)
                throw new InvalidOperationException("Synthetic rich copy did not supply the frozen CF_HTML; refusing a plain-text benchmark result.");
            var paste = detail.GetProperty("paste_text").GetString() ?? "";
            success &= form.LastPasteTicks > start && Normalize(form.CurrentText) == Normalize(paste) && form.Foreground;
            if (success && !string.IsNullOrEmpty(detail.GetProperty("paste_html").GetString()))
                success &= Clipboard.GetText(TextDataFormat.Html) == detail.GetProperty("paste_html").GetString();
            if (!success) error ??= "Editor text, paste receipt, foreground, or clipboard HTML did not verify.";
        }
        if (!opts.Live)
        {
            var frozen = Fixtures.Single(f => f.Id == c.Id);
            success &= output == frozen.Output && Normalize(form?.CurrentText ?? output) == Normalize(frozen.PasteText);
            if (form is not null) success &= detail.GetProperty("paste_html").GetString() == frozen.PasteHtml;
            if (!success) error ??= "Replay differs from frozen output.";
        }
        else if (success && !Fixtures.Any(f => f.Id == c.Id))
            Fixtures.Add(new(c.Id, c.Text, rawOutput, output, rich.Text, rich.Html));
        return new(c.Id, index, warmup, success, Normalize(output) == Normalize(c.Expected), timings, output, error);
    }

    internal static string Normalize(string text) => text.Replace("\r\n", "\n");
    internal static double Elapsed(long ticks) => (Stopwatch.GetTimestamp() - ticks) * 1000.0 / Stopwatch.Frequency;
}
