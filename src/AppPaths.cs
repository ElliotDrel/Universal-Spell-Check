namespace UniversalSpellCheck;

internal static class AppPaths
{
    // Settings + API key are isolated per channel so Dev experiments cannot
    // corrupt Prod state.
    public static string AppDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        BuildChannel.AppDataFolder);

    // Logs are intentionally shared across channels — both Prod and Dev write
    // into the same daily JSONL file so the corpus stays unified for future
    // fine-tune dataset use. Each line is stamped with channel + app_version.
    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UniversalSpellCheck",
        "logs");

    public static string LogPath { get; } = Path.Combine(
        LogDirectory,
        $"spellcheck-{DateTime.Now:yyyy-MM-dd}.jsonl");

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
