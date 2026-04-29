using System.IO;
using Microsoft.Win32;

namespace UniversalSpellCheck;

/// <summary>
/// Manages the Windows login auto-start hook. Writes a value under
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run pointing at the
/// running exe. Prod and Dev use distinct value names (BuildChannel.MutexName)
/// so they can be toggled independently.
///
/// On first launch after install, registers itself by default. A small
/// flag file in the per-channel AppData folder records that the first-run
/// registration has happened, so users who later disable startup do not
/// get re-enabled on every launch.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static string ValueName => BuildChannel.MutexName;

    private static string FirstRunFlagPath =>
        Path.Combine(AppPaths.AppDataDirectory, "startup.initialized");

    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is not null;
    }

    public static void Register()
    {
        var exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Environment.ProcessPath was null.");
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        key.SetValue(ValueName, $"\"{exe}\"");
    }

    public static void Unregister()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public static void EnsureFirstRunRegistered(DiagnosticsLogger logger)
    {
        if (BuildChannel.IsDev) return;

        try
        {
            if (File.Exists(FirstRunFlagPath)) return;

            Register();
            Directory.CreateDirectory(AppPaths.AppDataDirectory);
            File.WriteAllText(FirstRunFlagPath, DateTimeOffset.Now.ToString("O"));
            logger.Log("startup_registered_first_run");
        }
        catch (Exception ex)
        {
            logger.Log(
                $"startup_first_run_register_failed error_type={ex.GetType().Name} " +
                $"error=\"{Escape(ex.Message)}\"");
        }
    }

    private static string Escape(string? value) =>
        (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
}
