namespace UniversalSpellCheck;

internal static class AppPaths
{
    public static string AppDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UniversalSpellCheck");

    public static string LogDirectory { get; } = Path.Combine(
        AppDataDirectory,
        "native-spike-logs");

    public static string LogPath { get; } = Path.Combine(
        LogDirectory,
        $"phase5-{DateTime.Now:yyyy-MM-dd}.log");

    public static string SettingsPath { get; } = Path.Combine(
        AppDataDirectory,
        "settings.json");

    public static string ApiKeyPath { get; } = Path.Combine(
        AppDataDirectory,
        "apikey.dat");

    public static string ReplacementsPath { get; } = FindRepoFile("replacements.json");

    private static string FindRepoFile(string fileName)
    {
        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            var parent = Directory.GetParent(directory);
            if (parent is null)
            {
                break;
            }

            directory = parent.FullName;
        }

        return Path.Combine(AppContext.BaseDirectory, fileName);
    }
}
