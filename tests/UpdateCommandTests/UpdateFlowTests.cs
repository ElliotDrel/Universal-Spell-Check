using UniversalSpellCheck;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

internal static class UpdateFlowTests
{
    internal static void Run()
    {
        var directory = Directory.CreateTempSubdirectory("usc-update-tests-");
        try
        {
            var locator = new RecordingLocator(directory.FullName);
            File.WriteAllText(locator.UpdateExePath!, "fixture; never executed");
            VelopackApp.Build().SetLocator(locator).SetAutoApplyOnStartup(false).Run();
            var manager = new FakeManager(directory.FullName, locator);
            var logger = new DiagnosticsLogger(Path.Combine(directory.FullName, "test.log"));
            using var service = new UpdateService(logger, manager, directory.FullName);
            var restarts = new List<bool>();
            service.RestartRequested += restarts.Add;

            service.CheckAsync(UpdateTrigger.Launch).GetAwaiter().GetResult();
            Require(service.State is UpdateState.UpToDate && restarts.Count == 0,
                "Current startup must not request an install or restart loop.");
            service.RequestRestart();
            Require(restarts.SequenceEqual([false]), "Explicit restart requests use the shared restart path.");
            Require(service.PrepareRestart(false), "Pure restart must prepare successfully.");
            Require(locator.Recorder.LastArguments.Contains("start")
                && locator.Recorder.LastArguments.Contains(Environment.ProcessId.ToString())
                && !locator.Recorder.LastArguments.Contains("--restart"),
                "Restart must wait for the old PID and must not replay the command flag.");
            restarts.Clear();

            manager.Target = new UpdateInfo(VelopackAssetFeed.FromJson("""
                {"Assets":[{"PackageId":"Test","Version":"2.0.0","Type":"Full","FileName":"Test-2.0.0-full.nupkg","Size":1}]}
                """).Assets.Single(), false);
            service.CheckAsync(UpdateTrigger.Periodic).GetAwaiter().GetResult();
            Require(service.State is UpdateState.UpdateReady && restarts.Count == 0,
                "Background downloads must wait for restart.");
            var gate = new TaskCompletionSource<UpdateInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.CheckResult = gate.Task;
            var launch = service.CheckAsync(UpdateTrigger.Launch);
            var checksBeforeOverlap = manager.CheckCount;
            service.CheckAsync(UpdateTrigger.ManualTray).GetAwaiter().GetResult();
            Require(manager.CheckCount == checksBeforeOverlap, "Overlapping checks must not duplicate downloads.");
            gate.SetResult(manager.Target);
            launch.GetAwaiter().GetResult();
            Require(restarts.SequenceEqual([true]), "Startup must install the verified download automatically, once.");
            Require(service.PrepareRestart(true), "Verified update must prepare successfully.");
            Require(locator.Recorder.LastArguments.Contains("apply")
                && locator.Recorder.LastArguments.Contains("--silent")
                && locator.Recorder.LastArguments.Contains("--waitPid"),
                "Installation must be silent and wait for graceful app shutdown.");

            restarts.Clear();
            manager.CheckResult = Task.FromException<UpdateInfo?>(new IOException("fixture check failure"));
            service.CheckAsync(UpdateTrigger.Launch).GetAwaiter().GetResult();
            Require(service.State is UpdateState.Failed && restarts.Count == 0,
                "Failed checks must publish a failure state and leave the current app usable.");
            manager.CheckResult = null;
            manager.DownloadFails = true;
            service.CheckAsync(UpdateTrigger.Launch).GetAwaiter().GetResult();
            Require(service.State is UpdateState.Failed && restarts.Count == 0,
                "A failed download must not trigger installation.");
            Require(!service.PrepareRestart(true), "An older download must not be applied after the new download fails.");
            locator.Recorder.Fail = true;
            Require(!service.PrepareRestart(false) && service.State is UpdateState.Failed,
                "A missing restart helper must return failure instead of closing the current app.");
            locator.Recorder.Fail = false;
            manager.DownloadFails = false;
            var disposalGate = new TaskCompletionSource<UpdateInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.CheckResult = disposalGate.Task;
            var duringShutdown = service.CheckAsync(UpdateTrigger.Launch);
            var lateEvents = 0;
            service.StateChanged += (_, _) => lateEvents++;
            service.CheckCompleted += (_, _) => lateEvents++;
            service.RestartRequested += _ => lateEvents++;
            service.Dispose();
            disposalGate.SetResult(manager.Target);
            duringShutdown.GetAwaiter().GetResult();
            var checksAfterDispose = manager.CheckCount;
            service.CheckAsync(UpdateTrigger.Periodic).GetAwaiter().GetResult();
            Require(lateEvents == 0 && manager.CheckCount == checksAfterDispose,
                "Shutdown must suppress late callbacks, reject new checks, and unwind without disposing an owned gate.");
            AppContextTests.Run(manager, locator.Recorder, logger, directory.FullName);
            Require(locator.Recorder.ExitCount == 0, "The updater must not terminate the app before graceful cleanup.");
            Console.WriteLine("update_flow_tests_ok startup/background/overlap/check-failure/download-failure/helper-failure");
        }
        finally
        {
            if (!Path.GetFullPath(directory.FullName).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture cleanup escaped the temporary directory.");
            directory.Delete(true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal sealed class FakeManager : UpdateManager
    {
        internal UpdateInfo? Target;
        internal Task<UpdateInfo?>? CheckResult;
        internal int CheckCount;
        internal bool DownloadFails;
        private readonly string _directory;

        internal FakeManager(string directory, RecordingLocator locator)
            : base(new SimpleFileSource(new DirectoryInfo(directory)), locator: locator)
        {
            _directory = directory;
        }
        public override Task<UpdateInfo?> CheckForUpdatesAsync()
        {
            CheckCount++;
            return CheckResult ?? Task.FromResult(Target);
        }
        public override Task DownloadUpdatesAsync(UpdateInfo updates, Action<int>? progress = null,
            CancellationToken cancelToken = default)
        {
            if (DownloadFails) throw new IOException("fixture download failure");
            File.WriteAllText(Path.Combine(_directory, updates.TargetFullRelease.FileName), "verified fixture");
            return Task.CompletedTask;
        }
    }

    internal sealed class RecordingLocator : TestVelopackLocator
    {
        internal RecordingProcess Recorder { get; } = new();
        public override IProcessImpl Process => Recorder;
        internal RecordingLocator(string directory)
            : base("Test", "1.0.0", directory, directory, directory,
                Path.Combine(directory, "Update.exe"), processPath: Environment.ProcessPath!) { }
    }

    internal sealed class RecordingProcess : IProcessImpl
    {
        internal string[] LastArguments = [];
        internal bool Fail;
        internal int ExitCount;
        public string GetCurrentProcessPath() => Environment.ProcessPath!;
        public uint GetCurrentProcessId() => (uint)Environment.ProcessId;
        public void Exit(int exitCode) => ExitCount++;
        public void StartProcess(string exePath, IEnumerable<string> args, string workDir, bool showWindow)
        {
            if (Fail) throw new IOException("fixture missing helper");
            LastArguments = args.ToArray();
        }
    }
}
