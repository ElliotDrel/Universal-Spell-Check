using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UniversalSpellCheck;

internal sealed class SettingsStore
{
    private readonly DiagnosticsLogger _logger;

    public event Action? ApiKeyChanged;

    public SettingsStore(DiagnosticsLogger logger)
    {
        _logger = logger;
        Directory.CreateDirectory(AppPaths.AppDataDirectory);
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsPath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(AppPaths.SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            _logger.Log($"settings_load_failed error=\"{ex.Message}\"");
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(AppPaths.AppDataDirectory);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(AppPaths.SettingsPath, json);
    }

    public string? LoadApiKey()
    {
        try
        {
            var settings = Load();
            if (settings.UseEnvApiKey)
            {
                var envKey = LoadApiKeyFromEnv();
                if (envKey is not null) return envKey;
            }

            if (!File.Exists(AppPaths.ApiKeyPath)) return null;
            var encrypted = File.ReadAllBytes(AppPaths.ApiKeyPath);
            var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception ex)
        {
            _logger.Log($"apikey_load_failed error=\"{ex.Message}\"");
            return null;
        }
    }

    public void SaveSettings(AppSettings settings)
    {
        Save(settings);
        ApiKeyChanged?.Invoke();
    }

    private static string? LoadApiKeyFromEnv()
    {
        var envKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey)) return envKey.Trim();

        try
        {
            if (!File.Exists(AppPaths.EnvFilePath)) return null;
            const string prefix = "OPENAI_API_KEY=";
            foreach (var line in File.ReadLines(AppPaths.EnvFilePath))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var value = trimmed[prefix.Length..].Trim();
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
            }
        }
        catch { /* best-effort */ }
        return null;
    }

    public void SaveApiKey(string apiKey)
    {
        Directory.CreateDirectory(AppPaths.AppDataDirectory);
        var plaintext = Encoding.UTF8.GetBytes(apiKey.Trim());
        var encrypted = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(AppPaths.ApiKeyPath, encrypted);
        ApiKeyChanged?.Invoke();
    }

    public bool HasApiKey()
    {
        return !string.IsNullOrWhiteSpace(LoadApiKey());
    }
}
