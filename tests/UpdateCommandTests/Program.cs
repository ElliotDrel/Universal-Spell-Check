using System.Diagnostics;
using UniversalSpellCheck;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--send")) return HotkeyWindow.RequestRestart(args[1]) ? 0 : 1;

        var testWindowTitle = "UpdateCommandTests." + Guid.NewGuid();
        try
        {
            Require(UniversalSpellCheck.Program.IsRestartCommand(["--ReStArT"]), "Restart flag is case-insensitive.");
            Require(UniversalSpellCheck.Program.IsRestartCommand(["--UPDATE"]), "Existing update flag remains an alias.");
            Require(!UniversalSpellCheck.Program.IsRestartCommand([]), "Normal launch is not a command request.");
            Require(!HotkeyWindow.RequestRestart(testWindowTitle), "Missing receiver must fail.");
            using (var receiver = new HotkeyWindow(testWindowTitle))
            {
                using var unhandled = SendFromAnotherProcess(testWindowTitle);
                PumpUntilExit(unhandled);
                Require(unhandled.ExitCode == 1, "Receiver without a handler must reject.");

                var received = 0;
                EventHandler handler = (_, _) => received++;
                receiver.RestartRequested += handler;
                using var accepted = SendFromAnotherProcess(testWindowTitle);
                PumpUntilExit(accepted);
                Require(accepted.ExitCode == 0 && received == 1,
                    "Another process must deliver exactly one acknowledged request.");
                receiver.RestartRequested -= handler;

                using var blocked = SendFromAnotherProcess(testWindowTitle);
                // Deliberately do not pump the receiver: CLI must time out, not hang.
                if (!blocked.WaitForExit(5000))
                {
                    blocked.Kill();
                    blocked.WaitForExit();
                    throw new TimeoutException("Hung receiver exceeded five seconds.");
                }
                Require(blocked.ExitCode == 1, "Hung receiver must fail.");
                Application.DoEvents();
            }
            Require(!HotkeyWindow.RequestRestart(testWindowTitle), "Disposed receiver must fail.");
            UpdateFlowTests.Run();
            Console.WriteLine("update_command_tests_ok missing/unhandled/accepted/timeout/disposed");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static Process SendFromAnotherProcess(string windowTitle)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--send");
        start.ArgumentList.Add(windowTitle);
        return Process.Start(start)!;
    }

    private static void PumpUntilExit(Process process)
    {
        var timeout = Stopwatch.StartNew();
        while (!process.HasExited && timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
        if (!process.HasExited)
        {
            process.Kill();
            process.WaitForExit();
            throw new TimeoutException("Request process did not exit.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
