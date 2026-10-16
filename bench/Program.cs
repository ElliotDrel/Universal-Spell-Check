using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Forms;

namespace UniversalSpellCheck.Bench;

internal static class Program
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var opts = Options.Parse(args);
            if (opts.Mode == "self-test") return BenchTests.Run();
            var configuration = typeof(Program).Assembly.GetCustomAttributes(false)
                .OfType<System.Reflection.AssemblyConfigurationAttribute>().Single().Configuration;
            if (configuration != "Release") throw new InvalidOperationException("Benchmarks require a Release build.");
            if (opts.Desktop && !opts.AllowDesktop)
                throw new ArgumentException("Desktop modes require --allow-desktop and an idle computer.");
            var cases = JsonSerializer.Deserialize<List<BenchCase>>(File.ReadAllText(opts.Cases), Json)!;
            if (cases.Count == 0 || cases.Select(c => c.Id).Distinct().Count() != cases.Count
                || cases.Any(c => string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Text)))
                throw new ArgumentException("Cases must be nonempty with unique IDs and nonempty text.");
            Directory.CreateDirectory(opts.Output);
            var fixtures = opts.Fixtures is null ? new List<ReplayFixture>()
                : JsonSerializer.Deserialize<List<ReplayFixture>>(File.ReadAllText(opts.Fixtures), Json)!;
            if (opts.Mode is "compute" or "local")
                foreach (var c in cases)
                    if (!fixtures.Any(f => f.Id == c.Id && f.Input == c.Text))
                        throw new ArgumentException($"Missing or mismatched recorded fixture: {c.Id}");
            var logger = new CapturingLogger(Path.Combine(opts.Output, $"spellcheck-{DateTime.Now:yyyy-MM-dd}.jsonl"));
            var store = opts.Live
                ? new SettingsStore(logger, opts.Settings, Path.Combine(opts.Settings, "apikey.dat"))
                : new SettingsStore(logger, Path.Combine(opts.Output, "offline-settings"));
            // Replay cannot reach the network and does not read or change user keys/settings.
            if (!opts.Live) store.SaveApiKey("offline-replay-no-network");
            var cached = new CachedSettings(store);
            if (string.IsNullOrWhiteSpace(cached.ApiKey)) throw new InvalidOperationException("No saved API key available.");
            var model = opts.Model ?? store.Load().Model;
            if (model != OpenAiSpellcheckService.DefaultModel && model != OpenAiSpellcheckService.Gpt54MiniModel)
                throw new ArgumentException("Use one of the app's supported models.");
            using var service = new OpenAiSpellcheckService(cached, logger, model,
                opts.Live ? null : new ReplayTransport(fixtures));
            var post = new TextPostProcessor(logger);
            using var coordinator = new SpellcheckCoordinator(logger, service, post, new TargetFormattingPipeline(),
                (_, _) => { }, _ => { }, () => { }, developerLogging: () => opts.DeveloperLogging);
            var manifest = new
            {
                schema_version = 2, created_utc = DateTimeOffset.UtcNow, options = opts,
                worktree_commit = Git("rev-parse HEAD"), dirty = Git("status --porcelain"),
                product_build = typeof(SpellcheckCoordinator).Assembly.GetCustomAttributes(false)
                    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion,
                os = Environment.OSVersion.ToString(), runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                processor_count = Environment.ProcessorCount, stopwatch_frequency = Stopwatch.Frequency,
                model, prompt = OpenAiSpellcheckService.PromptInstruction,
                cases_sha256 = Hash(opts.Cases), replacements_sha256 = Hash(AppPaths.ReplacementsPath),
                product_dll_sha256 = Hash(typeof(SpellcheckCoordinator).Assembly.Location),
                harness_sha256 = Hash(typeof(Program).Assembly.Location),
                fixture_sha256 = opts.Fixtures is null ? null : Hash(opts.Fixtures),
                configuration, developer_logging = opts.Desktop && opts.DeveloperLogging, saved_developer_logging = cached.DeveloperLogging,
                machine_name = Environment.MachineName, case_ids = cases.Select(c => c.Id).ToArray(),
                notes = "Headless excludes clipboard/target hooks/rich mapping. Compute excludes clipboard/network. Local uses the real coordinator with recorded-output HTTP transport. Live quality_match is informational; replay requires exact output. Null timing means unmeasured."
            };
            File.WriteAllText(Path.Combine(opts.Output, "manifest.json"), JsonSerializer.Serialize(manifest, Json));
            var harness = new BenchHarness(opts, cases, fixtures, service, post, coordinator, logger);
            int exit;
            if (opts.Desktop)
            {
                Application.EnableVisualStyles();
                using var form = new BenchTargetForm();
                using var hotkey = new HotkeyWindow("UniversalSpellCheck.Benchmark");
                hotkey.Register(HotkeyInjector.HotkeyModifiers, HotkeyInjector.HotkeyVk);
                Task? running = null;
                hotkey.HotkeyPressed += (_, _) => running = coordinator.RunAsync();
                harness.CoordinatorTask = () => running;
                exit = 2;
                form.Shown += async (_, _) =>
                {
                    try { exit = await harness.RunAsync(form); }
                    catch (Exception e) { Console.Error.WriteLine(e); exit = 2; }
                    finally { form.Close(); }
                };
                Application.Run(form);
            }
            else exit = harness.RunAsync(null).GetAwaiter().GetResult();
            if (opts.Live)
                File.WriteAllText(Path.Combine(opts.Output, "fixtures.json"), JsonSerializer.Serialize(harness.Fixtures, Json));
            Console.WriteLine($"RESULTS {Path.GetFullPath(opts.Output)} exit={exit}");
            return exit;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 2; }
    }

    internal static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Git(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("git", args)
        { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
        var output = p.StandardOutput.ReadToEnd().Trim(); p.WaitForExit(); return output;
    }
}

internal sealed record Options(string Mode, string Cases, string Output, string Settings,
    string? Model, string? Fixtures, int Runs, int Warmup, bool AllowDesktop, bool DeveloperLogging)
{
    public bool Desktop => Mode is "local" or "e2e";
    public bool Live => Mode is "headless" or "e2e";
    public static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>(); var allow = false; var developerLogging = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--allow-desktop") { allow = true; continue; }
            if (args[i] == "--developer-logging") { developerLogging = true; continue; }
            if (args[i] is not ("--mode" or "--cases" or "--output" or "--settings" or "--model" or "--fixtures" or "--runs" or "--warmup"))
                throw new ArgumentException($"Unknown argument {args[i]}");
            if (++i == args.Length) throw new ArgumentException("Missing argument value.");
            values.Add(args[i - 1], args[i]);
        }
        string Get(string name, string fallback) => values.GetValueOrDefault(name, fallback);
        var mode = Get("--mode", "headless");
        if (mode is not ("self-test" or "compute" or "headless" or "local" or "e2e")) throw new ArgumentException("Invalid mode.");
        int runs = int.Parse(Get("--runs", "20")), warmup = int.Parse(Get("--warmup", "2"));
        if (runs < 1 || warmup < 0) throw new ArgumentException("Invalid repeat count.");
        return new(mode, Path.GetFullPath(Get("--cases", "bench/baseline-cases.json")),
            Path.GetFullPath(Get("--output", $"bench/results/{DateTime.UtcNow:yyyyMMdd-HHmmss}-{mode}")),
            Path.GetFullPath(Get("--settings", AppPaths.AppDataDirectory)),
            values.GetValueOrDefault("--model"), values.GetValueOrDefault("--fixtures"), runs, warmup, allow, developerLogging);
    }
}

internal sealed class BenchCase
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public string Expected { get; set; } = "";
    public string Html { get; set; } = "";
}

internal sealed record ReplayFixture(string Id, string Input, string RawAiOutput, string Output, string PasteText, string PasteHtml);

internal sealed class CapturingLogger(string path) : DiagnosticsLogger(path)
{
    private TaskCompletionSource<JsonElement>? _pending;
    internal Task<JsonElement> BeginTrial()
    {
        _pending = new(TaskCreationOptions.RunContinuationsAsynchronously); return _pending.Task;
    }
    public override void LogData(string eventName, object data)
    {
        // Complete only after the previous log has been written, so trials never overlap finalization.
        base.LogData(eventName, data);
        if (eventName == "spellcheck_detail") _pending?.TrySetResult(JsonSerializer.SerializeToElement(data));
    }
}
