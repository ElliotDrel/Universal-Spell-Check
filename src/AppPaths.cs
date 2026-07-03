namespace UniversalSpellCheck;

internal static class AppPaths
{
    private const string MigrationMarkerName = ".migrated-from-install-directory-v1";

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
        BuildChannel.SharedDataFolder,
        "logs");

    public static string LogPath => SpellcheckLogPathFor(DateTime.Now);

    public static string SpellcheckLogPathFor(DateTime date) => Path.Combine(
        LogDirectory,
        $"spellcheck-{date:yyyy-MM-dd}.jsonl");

    public static string SettingsPath { get; } = Path.Combine(
        AppDataDirectory,
        "settings.json");

    public static string ApiKeyPath { get; } = Path.Combine(
        AppDataDirectory,
        "apikey.dat");

    public static string ReplacementsPath { get; } = FindRepoFile("replacements.json");

    public static string EnsureDataMigration()
    {
        Directory.CreateDirectory(AppDataDirectory);
        Directory.CreateDirectory(LogDirectory);

        var sharedDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            BuildChannel.SharedDataFolder);
        var markerPath = Path.Combine(sharedDataDirectory, MigrationMarkerName);
        using var migrationMutex = new Mutex(false, "UniversalSpellCheck.DataMigration.v1");
        var lockTaken = false;
        try
        {
            lockTaken = migrationMutex.WaitOne(TimeSpan.FromSeconds(10));
        }
        catch (AbandonedMutexException)
        {
            lockTaken = true;
        }

        if (!lockTaken)
            return "data_migration_failed reason=mutex_timeout";

        try
        {
            var migrationStartedUtc = DateTime.UtcNow;
            var legacyDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                BuildChannel.LegacyInstallFolder);
            var lastMigrationUtc = File.Exists(markerPath)
                ? File.GetLastWriteTimeUtc(markerPath)
                : DateTime.MinValue;
            var copiedFiles = 0;
            var mergedLogLines = 0;

            if (Directory.Exists(legacyDirectory))
            {
                copiedFiles += CopyIfNewer(legacyDirectory, sharedDataDirectory, "settings.json");
                copiedFiles += CopyIfNewer(legacyDirectory, sharedDataDirectory, "apikey.dat");
                copiedFiles += CopyIfNewer(legacyDirectory, sharedDataDirectory, "startup.initialized");
                copiedFiles += CopyIfNewer(legacyDirectory, sharedDataDirectory, "last-update-check.txt");

                var legacyLogs = Path.Combine(legacyDirectory, "logs");
                if (Directory.Exists(legacyLogs))
                {
                    foreach (var sourcePath in Directory.GetFiles(legacyLogs, "spellcheck-*.jsonl"))
                    {
                        var destinationPath = Path.Combine(LogDirectory, Path.GetFileName(sourcePath));
                        if (!File.Exists(destinationPath))
                        {
                            File.Copy(sourcePath, destinationPath);
                            copiedFiles++;
                            continue;
                        }

                        if (File.GetLastWriteTimeUtc(sourcePath) <= lastMigrationUtc)
                            continue;

                        var existingLines = new HashSet<string>(File.ReadLines(destinationPath), StringComparer.Ordinal);
                        var missingLines = File.ReadLines(sourcePath).Where(existingLines.Add).ToArray();
                        if (missingLines.Length > 0)
                        {
                            File.AppendAllLines(destinationPath, missingLines);
                            mergedLogLines += missingLines.Length;
                        }
                    }
                }
            }

            File.WriteAllText(markerPath, DateTimeOffset.UtcNow.ToString("O"));
            File.SetLastWriteTimeUtc(markerPath, migrationStartedUtc);
            return copiedFiles == 0 && mergedLogLines == 0
                ? "data_migration_skipped reason=no_new_legacy_data"
                : $"data_migration_completed copied_files={copiedFiles} merged_log_lines={mergedLogLines}";
        }
        catch (Exception ex)
        {
            return $"data_migration_failed error_type={ex.GetType().Name} error=\"{Escape(ex.Message)}\"";
        }
        finally
        {
            if (lockTaken)
                migrationMutex.ReleaseMutex();
        }
    }

    private static int CopyIfNewer(string sourceDirectory, string destinationDirectory, string fileName)
    {
        var sourcePath = Path.Combine(sourceDirectory, fileName);
        var destinationPath = Path.Combine(destinationDirectory, fileName);
        if (!File.Exists(sourcePath))
            return 0;
        if (File.Exists(destinationPath) &&
            File.GetLastWriteTimeUtc(sourcePath) <= File.GetLastWriteTimeUtc(destinationPath))
            return 0;

        File.Copy(sourcePath, destinationPath, overwrite: true);
        return 1;
    }

    private static string Escape(string? value)
    {
        return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
    }

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
