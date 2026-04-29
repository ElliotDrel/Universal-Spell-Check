using System.Diagnostics;
using System.Text.Json;
using System.Windows.Forms;

namespace UniversalSpellCheck.Bench;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"bench fatal: {ex}");
            return 2;
        }
    }

    private static int Run(string[] args)
    {
        var opts = ParseArgs(args);
        Console.WriteLine($"bench starting mode={(opts.E2e ? "e2e" : "headless")} variant={opts.Variant} model={opts.Model} runs={opts.Runs} warmup={opts.Warmup}");

        // Load inputs from bench/inputs.json
        var inputsJsonPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "inputs.json");
        if (!File.Exists(inputsJsonPath))
        {
            // Fallback: beside the exe (publish layout)
            inputsJsonPath = Path.Combine(AppContext.BaseDirectory, "inputs.json");
        }
        if (!File.Exists(inputsJsonPath))
        {
            Console.Error.WriteLine($"inputs.json not found. Run extract_inputs.py first.");
            return 1;
        }
        var inputEntries = JsonSerializer.Deserialize<List<InputEntry>>(
            File.ReadAllText(inputsJsonPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var inputs = inputEntries.Select(e => (Name: e.Id, Text: e.Text)).ToList();
        Console.WriteLine($"loaded {inputs.Count} input(s) from {inputsJsonPath}");

        // Wire production services with bench-specific logger interceptor.
        var benchLogPath = Path.Combine(
            AppPaths.LogDirectory,
            $"bench-{DateTime.Now:yyyy-MM-dd-HHmmss}.jsonl");
        var capturingLogger = new CapturingLogger(benchLogPath);

        var settingsStore = new SettingsStore(capturingLogger);
        var cachedSettings = new CachedSettings(settingsStore);
        if (string.IsNullOrWhiteSpace(cachedSettings.ApiKey))
        {
            Console.Error.WriteLine($"No OpenAI API key at {AppPaths.ApiKeyPath}. Set one via the prod app Settings, then rerun.");
            return 1;
        }

        using var spellService = new OpenAiSpellcheckService(cachedSettings, capturingLogger, opts.Model);
        spellService.StartConnectionWarmer();
        var postProcessor = new TextPostProcessor(capturingLogger);

        using var coordinator = new SpellcheckCoordinator(
            capturingLogger,
            spellService,
            postProcessor,
            notify: (_, _) => { },
            setBusy: _ => { },
            showSettings: () => { });

        Application.EnableVisualStyles();
        BenchTargetForm? form = null;
        HotkeyWindow? hotkey = null;

        if (opts.E2e)
        {
            // Hidden form on the UI thread.
            form = new BenchTargetForm();
            form.Show();
            Application.DoEvents();

            // Hotkey window registers Ctrl+Alt+B — distinct from prod (Ctrl+Alt+U) and dev (Ctrl+Alt+D).
            hotkey = new HotkeyWindow();
            hotkey.HotkeyPressed += (_, _) => _ = coordinator.RunAsync();
            hotkey.Register(HotkeyInjector.HotkeyModifiers, HotkeyInjector.HotkeyVk);
        }

        var harness = new BenchHarness(form, coordinator, capturingLogger, opts.Runs, opts.Warmup, opts.E2e);
        capturingLogger.OnSpellcheckDetail = harness.RecordCoordinatorTimings;

        // Drive async bench while pumping the WinForms message loop.
        var task = Task.Run(() => harness.RunAllAsync(inputs));
        while (!task.IsCompleted)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }

        var results = task.GetAwaiter().GetResult();

        form?.Close();
        hotkey?.Dispose();

        var resultsDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "results");
        Directory.CreateDirectory(resultsDir);
        var sha = TryGitSha();
        var outPath = Path.Combine(resultsDir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{sha}-{opts.Variant}.json");
        ResultsWriter.Write(outPath, opts, results);

        Console.WriteLine($"results: {outPath}");
        Console.WriteLine($"bench log: {benchLogPath}");
        return 0;
    }

    private sealed class InputEntry
    {
        public string Id { get; set; } = "";
        public string Text { get; set; } = "";
    }

    private sealed class BenchOptions
    {
        public int Runs { get; init; } = 10;
        public int Warmup { get; init; } = 2;
        public string Model { get; init; } = "gpt-4.1-nano";
        public string Variant { get; init; } = "baseline";
        public bool E2e { get; init; } = false;
    }

    private static BenchOptions ParseArgs(string[] args)
    {
        int runs = 10, warmup = 2;
        string model = "gpt-4.1-nano", variant = "baseline";
        var e2e = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--runs": runs = int.Parse(args[++i]); break;
                case "--warmup": warmup = int.Parse(args[++i]); break;
                case "--model": model = args[++i]; break;
                case "--variant": variant = args[++i]; break;
                case "--e2e": e2e = true; break;
                default: throw new ArgumentException($"Unknown arg: {args[i]}");
            }
        }
        return new BenchOptions { Runs = runs, Warmup = warmup, Model = model, Variant = variant, E2e = e2e };
    }

    private static string TryGitSha()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse --short HEAD")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            using var p = Process.Start(psi)!;
            var sha = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);
            return string.IsNullOrEmpty(sha) ? "nogit" : sha;
        }
        catch { return "nogit"; }
    }
}

/// <summary>
/// Subclasses DiagnosticsLogger to intercept spellcheck_detail events and
/// hand per-trial timings to the bench harness without modifying the coordinator.
/// </summary>
internal sealed class CapturingLogger : DiagnosticsLogger
{
    public Action<TrialTimings>? OnSpellcheckDetail { get; set; }

    public CapturingLogger(string logPath) : base(logPath) { }

    public override void LogData(string eventName, object data)
    {
        base.LogData(eventName, data);
        if (eventName != "spellcheck_detail") return;

        try
        {
            var json = JsonSerializer.Serialize(data);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var status = root.GetProperty("status").GetString() ?? "unknown";
            var timings = root.GetProperty("timings");
            var tokens = root.GetProperty("tokens");
            OnSpellcheckDetail?.Invoke(new TrialTimings
            {
                Status = status,
                TotalMs = timings.GetProperty("total_ms").GetInt64(),
                ClipboardMs = timings.GetProperty("clipboard_ms").GetInt64(),
                RequestMs = timings.GetProperty("request_ms").GetInt64(),
                ReplacementsMs = timings.GetProperty("replacements_ms").GetInt64(),
                PromptGuardMs = timings.GetProperty("prompt_guard_ms").GetInt64(),
                PasteMs = timings.GetProperty("paste_ms").GetInt64(),
                InputTokens = tokens.GetProperty("input").GetInt32(),
                OutputTokens = tokens.GetProperty("output").GetInt32(),
                CachedTokens = tokens.GetProperty("cached").GetInt32(),
                ErrorMessage = root.TryGetProperty("error", out var err) ? err.GetString() : null,
            });
        }
        catch
        {
            // Never fail the bench on a parse error — just skip this trial's timings.
        }
    }
}
