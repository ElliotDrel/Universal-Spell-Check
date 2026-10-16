using System.Text.Json;

namespace UniversalSpellCheck.Bench;

internal static class ResultsWriter
{
    internal static void Write(string path, List<Trial> trials)
    {
        object Summary(IEnumerable<Trial> source)
        {
            var all = source.ToArray(); var ok = all.Where(t => t.Success).ToArray();
            return new
            {
                attempted = all.Length, succeeded = ok.Length, failed = all.Length - ok.Length,
                quality_matches = all.Count(t => t.QualityMatch),
                timings = ok.SelectMany(t => t.Timings.Keys).Distinct().ToDictionary(k => k,
                    k => Stats.For(ok.Select(t => t.Timings.GetValueOrDefault(k))))
            };
        }
        var measured = trials.Where(t => !t.Warmup).ToArray();
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            aggregate = Summary(measured), warmup = Summary(trials.Where(t => t.Warmup)),
            cases = measured.GroupBy(t => t.CaseId).ToDictionary(g => g.Key, g => Summary(g))
        }, Program.Json));
    }
}

internal static class Stats
{
    internal static object For(IEnumerable<double?> samples)
    {
        var values = samples.Where(v => v.HasValue).Select(v => v!.Value).Order().ToArray();
        if (values.Length == 0) return new { count = 0, mean = (double?)null, median = (double?)null, p95 = (double?)null, min = (double?)null, max = (double?)null };
        return new { count = values.Length, mean = (double?)values.Average(), median = (double?)Percentile(values, .5),
            p95 = (double?)Percentile(values, .95), min = (double?)values[0], max = (double?)values[^1] };
    }
    internal static double Percentile(double[] sorted, double p)
    {
        var position = p * (sorted.Length - 1); var lo = (int)position; var hi = (int)Math.Ceiling(position);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (position - lo);
    }
}
