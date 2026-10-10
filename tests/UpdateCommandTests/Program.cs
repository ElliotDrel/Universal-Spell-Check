using System.Diagnostics;
using UniversalSpellCheck;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--send")) return HotkeyWindow.RequestUpdate() ? 0 : 1;

        try
        {
            Require(!HotkeyWindow.RequestUpdate(), "Missing receiver must fail.");
            using (var receiver = new HotkeyWindow())
            {
                using var unhandled = SendFromAnotherProcess();
                PumpUntilExit(unhandled);
                Require(unhandled.ExitCode == 1, "Receiver without a handler must reject.");

                var received = 0;
                EventHandler handler = (_, _) => received++;
                receiver.UpdateRequested += handler;
                using var accepted = SendFromAnotherProcess();
                PumpUntilExit(accepted);
                Require(accepted.ExitCode == 0 && received == 1,
                    "Another process must deliver exactly one acknowledged request.");
                receiver.UpdateRequested -= handler;

                using var blocked = SendFromAnotherProcess();
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
            Require(!HotkeyWindow.RequestUpdate(), "Disposed receiver must fail.");
            Console.WriteLine("update_command_tests_ok missing/unhandled/accepted/timeout/disposed");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static Process SendFromAnotherProcess()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--send");
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
