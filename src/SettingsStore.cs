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
            if (!File.Exists(AppPaths.ApiKeyPath))
            {
                return null;
            }

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
