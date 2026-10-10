using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using UniversalSpellCheck;
using Forms = System.Windows.Forms;

internal static class AppContextTests
{
    internal static void Run(UpdateFlowTests.FakeManager manager,
        UpdateFlowTests.RecordingProcess process, DiagnosticsLogger logger, string directory)
    {
        manager.CheckResult = null;
        manager.DownloadFails = false;
        process.Fail = false;
        using (var fixture = new Fixture(manager, logger, directory))
        {
            var correction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Correction = () => correction.Task;
            fixture.Invoke("OnHotkeyPressed", null, EventArgs.Empty);
            fixture.Invoke("OnCommandRestartRequested", null, EventArgs.Empty);
            fixture.Invoke("OnCommandRestartRequested", null, EventArgs.Empty);
            fixture.Invoke("OnHotkeyPressed", null, EventArgs.Empty);
            PumpUntil(() => false, 1200, false);
            Require(fixture.Notices.Count == 1 && fixture.Notices[0].Title == "Restarting",
                "Duplicate commands must produce exactly one progress notification.");
            Require(!fixture.Exited && fixture.CorrectionCount == 1,
                "Restart must wait for active correction and reject further hotkeys.");
            correction.SetResult();
            PumpUntil(() => fixture.Exited);
            Require(process.LastArguments.Contains("start") && fixture.Notices.Count == 1,
                "Pure restart must use the native helper with no additional notice.");
        }
        using (var fixture = new Fixture(manager, logger, directory))
        {
            fixture.Invoke("ShowRestartNotice"); // Closed-app CLI constructor branch.
            var check = fixture.Service.CheckAsync(UpdateTrigger.Launch);
            PumpUntil(() => check.IsCompleted && fixture.Exited);
            check.GetAwaiter().GetResult();
            Require(fixture.Notices.Count == 1 && process.LastArguments.Contains("apply"),
                "Startup installation must preserve the one-notification command contract.");
        }
        var target = manager.Target;
        manager.Target = null;
        using (var fixture = new Fixture(manager, logger, directory))
        {
            var check = fixture.Service.CheckAsync(UpdateTrigger.Launch);
            PumpUntil(() => check.IsCompleted);
            check.GetAwaiter().GetResult();
            Require(!fixture.Exited && fixture.Notices.Count == 0,
                "The internal relaunch must not notify or loop when current.");
        }
        manager.Target = target;
        foreach (var downloadFailure in new[] { false, true })
        {
            manager.DownloadFails = downloadFailure;
            manager.CheckResult = downloadFailure ? null
                : Task.FromException<Velopack.UpdateInfo?>(new IOException("fixture check failure"));
            using var fixture = new Fixture(manager, logger, directory);
            fixture.Set("_startupChecking", true);
            var check = fixture.Service.CheckAsync(UpdateTrigger.Launch);
            PumpUntil(() => check.IsCompleted && !fixture.Get<bool>("_startupChecking"));
            check.GetAwaiter().GetResult();
            fixture.Invoke("OnHotkeyPressed", null, EventArgs.Empty);
            Require(fixture.Notices.Count == 1 && fixture.Notices[0].Title == "Update failed"
                && !fixture.Exited && fixture.CorrectionCount == 1,
                "Check/download failures must notify once and restore correction availability.");
        }
        manager.CheckResult = null;
        manager.DownloadFails = false;
        process.Fail = true;
        using (var fixture = new Fixture(manager, logger, directory))
        {
            fixture.Invoke("OnCommandRestartRequested", null, EventArgs.Empty);
            PumpUntil(() => !fixture.Get<bool>("_restarting"));
            fixture.Invoke("OnHotkeyPressed", null, EventArgs.Empty);
            Require(!fixture.Exited && fixture.CorrectionCount == 1
                && fixture.Notices.Select(n => n.Title).SequenceEqual(["Restarting", "Restart failed"]),
                "Helper failure must add one failure notice and keep the current app usable.");
        }
        process.Fail = false;
        Console.WriteLine("app_context_tests_ok one-notice/duplicates/correction-wait/relaunch/failure-recovery");
    }

    private static void PumpUntil(Func<bool> done, int timeout = 5000, bool requireDone = true)
    {
        var watch = Stopwatch.StartNew();
        while (!done() && watch.ElapsedMilliseconds < timeout)
        {
            Forms.Application.DoEvents();
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
        if (requireDone) Require(done(), "App-context operation timed out.");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    // Exercise actual private handlers without starting the real tray app,
    // network warmer, global hotkey, user settings, or installer processes.
    private sealed class Fixture : IDisposable
    {
        private readonly SpellCheckAppContext _context =
            (SpellCheckAppContext)RuntimeHelpers.GetUninitializedObject(typeof(SpellCheckAppContext));
        internal readonly UpdateService Service;
        internal readonly List<(string Title, string Message)> Notices = [];
        internal Func<Task> Correction = () => Task.CompletedTask;
        internal int CorrectionCount;
        internal bool Exited;
        private readonly Forms.ToolStripMenuItem _version = new();
        private readonly Forms.ToolStripMenuItem _check = new();

        internal Fixture(UpdateFlowTests.FakeManager manager, DiagnosticsLogger logger, string directory)
        {
            Service = new UpdateService(logger, manager, directory);
            Set("_logger", logger);
            Set("_updateService", Service);
            Set("_dispatcher", Dispatcher.CurrentDispatcher);
            Set("_activeCorrection", Task.CompletedTask);
            Set("_versionItem", _version);
            Set("_checkForUpdatesItem", _check);
            Set("_notify", (Action<string, string>)((title, message) => Notices.Add((title, message))));
            Set("_runCorrection", (Func<Task>)(() => { CorrectionCount++; return Correction(); }));
            Set("_exit", (Action)(() => Exited = true));
            Service.RestartRequested += apply => Invoke("OnRestartRequested", apply);
            Service.StateChanged += (sender, state) => Invoke("OnUpdateStateChanged", sender, state);
            Service.CheckCompleted += (sender, args) => Invoke("OnUpdateCheckCompleted", sender, args);
        }
        internal void Set(string name, object value) => Field(name).SetValue(_context, value);
        internal T Get<T>(string name) => (T)Field(name).GetValue(_context)!;
        private static FieldInfo Field(string name) => typeof(SpellCheckAppContext)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        internal void Invoke(string name, params object?[] args) => typeof(SpellCheckAppContext)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_context, args);
        public void Dispose()
        {
            Service.Dispose();
            _version.Dispose();
            _check.Dispose();
        }
    }
}
