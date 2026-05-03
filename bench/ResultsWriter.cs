using System.Text.Json;

namespace UniversalSpellCheck.Bench;

internal static class ResultsWriter
{
    public static void Write(string path, object opts, IReadOnlyList<InputResult> results)
    {
        var summary = results.Select(input => new
        {
            name = input.Name,
            input_chars = input.InputChars,
            trial_count = input.Trials.Count,
            success_count = input.Trials.Count(t => t.Success),
            total_ms = Stats.For(input.Trials.Where(t => t.Success), t => t.TotalMs),
            coordinator_total_ms = Stats.For(input.Trials.Where(t => t.Success), t => t.CoordinatorTotalMs),
            capture_ms = Stats.For(input.Trials.Where(t => t.Success), t => t.CaptureMs),
            request_ms = Stats.For(input.Trials.Where(t => t.Success), t => t.RequestMs),
            post_process_ms = Stats.For(input.Trials.Where(t => t.Success), t => t.PostProcessMs),
            paste_ms = Stats.For(input.Trials.Where(t => t.Success), t => t.PasteMs),
            tokens = new
            {
                input_avg = input.Trials.Where(t => t.Success).DefaultIfEmpty().Average(t => t?.InputTokens ?? 0),
                output_avg = input.Trials.Where(t => t.Success).DefaultIfEmpty().Average(t => t?.OutputTokens ?? 0),
                cached_avg = input.Trials.Where(t => t.Success).DefaultIfEmpty().Average(t => t?.CachedTokens ?? 0),
            },
            sample_output = input.SampleOutput,
        });

        var aggregate = new
        {
            generated_at_utc = DateTime.UtcNow.ToString("O"),
            options = opts,
            inputs = summary,
            trials = results.SelectMany(r => r.Trials),  // raw per-trial dump for re-aggregation
        };

        File.WriteAllText(
            path,
            JsonSerializer.Serialize(aggregate, new JsonSerializerOptions { WriteIndented = true }));
    }
}

internal static class Stats
{
    public static object For<T>(IEnumerable<T> items, Func<T, long> selector)
    {
        var values = items.Select(selector).Where(v => v > 0).OrderBy(v => v).ToList();
        if (values.Count == 0)
        {
            return new { count = 0, mean = 0.0, median = 0.0, p95 = 0.0, min = 0L, max = 0L, stddev = 0.0 };
        }
        var mean = values.Average();
        var sumSq = values.Sum(v => (v - mean) * (v - mean));
        var stddev = values.Count > 1 ? Math.Sqrt(sumSq / (values.Count - 1)) : 0.0;
        return new
        {
            count = values.Count,
            mean,
            median = Percentile(values, 0.50),
            p95 = Percentile(values, 0.95),
            min = values[0],
            max = values[^1],
            stddev,
        };
    }

    private static double Percentile(IReadOnlyList<long> sorted, double p)
    {
        if (sorted.Count == 1) return sorted[0];
        var idx = p * (sorted.Count - 1);
        var lo = (int)Math.Floor(idx);
        var hi = (int)Math.Ceiling(idx);
        if (lo == hi) return sorted[lo];
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (idx - lo);
    }
}
