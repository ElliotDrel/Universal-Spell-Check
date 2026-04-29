using System.Reflection;

namespace UniversalSpellCheck;

internal static class BuildChannel
{
#if DEV
    public const bool IsDev = true;
    public const string DisplayName = "Universal Spell Check (Dev)";
    public const string ChannelName = "dev";
    public const string AppDataFolder = "UniversalSpellCheck.Dev";
    public const string MutexName = "UniversalSpellCheck.Dev";
    public const string TrayTooltip = "Universal Spell Check (Dev)";

    // Ctrl+Alt+D for Dev so it cannot collide with the Prod hotkey on the same machine.
    public const uint HotkeyVk = 0x44; // VK_D
#else
    public const bool IsDev = false;
    public const string DisplayName = "Universal Spell Check";
    public const string ChannelName = "prod";
    public const string AppDataFolder = "UniversalSpellCheck";
    public const string MutexName = "UniversalSpellCheck";
    public const string TrayTooltip = "Universal Spell Check";

    // Ctrl+Alt+U for Prod (the daily-driver hotkey).
    public const uint HotkeyVk = 0x55; // VK_U
#endif

    public const uint HotkeyModAlt = 0x0001;
    public const uint HotkeyModControl = 0x0002;
    public const uint HotkeyModNoRepeat = 0x4000;
    public const uint HotkeyModifiers = HotkeyModControl | HotkeyModAlt | HotkeyModNoRepeat;

    public static string AppVersion { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
#if DEV
        return "0.0.0-dev";
#else
        var asm = Assembly.GetEntryAssembly();
        var info = asm?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            // Strip git-hash metadata that AssemblyInformationalVersion sometimes appends (e.g. "1.2.3+abcdef").
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        var ver = asm?.GetName().Version;
        return ver is null ? "0.0.0" : $"{ver.Major}.{ver.Minor}.{ver.Build}";
#endif
    }
}
