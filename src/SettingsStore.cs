using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UniversalSpellCheck;

internal sealed class SettingsStore
{
    private readonly DiagnosticsLogger _logger;
    private readonly string _appDataDirectory;
    private readonly string _apiKeyPath;
    private readonly object _apiKeyLock = new();

    public event Action? ApiKeyChanged;
    public event Action? SettingsChanged;

    public SettingsStore(
        DiagnosticsLogger logger,
        string? appDataDirectory = null,
        string? apiKeyPath = null)
    {
        _logger = logger;
        _appDataDirectory = appDataDirectory ?? AppPaths.AppDataDirectory;
        _apiKeyPath = apiKeyPath ?? AppPaths.ApiKeyPath;
        Directory.CreateDirectory(_appDataDirectory);
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
        SettingsChanged?.Invoke();
    }

    public string? LoadApiKey()
    {
        lock (_apiKeyLock)
        {
            var document = LoadApiKeyDocument();
            return document.Keys.FirstOrDefault(key => key.Id == document.ActiveKeyId)?.Value
                ?? document.Keys.FirstOrDefault()?.Value;
        }
    }

    public IReadOnlyList<ApiKeyInfo> LoadApiKeyInfos()
    {
        lock (_apiKeyLock)
        {
            var document = LoadApiKeyDocument();
            return document.Keys
                .Select(key => new ApiKeyInfo(
                    key.Id,
                    key.Name,
                    MaskApiKey(key.Value),
                    key.Id == document.ActiveKeyId))
                .ToArray();
        }
    }

    public void AddApiKey(string name, string apiKey)
    {
        var normalizedName = name.Trim();
        var normalizedKey = apiKey.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
            throw new ArgumentException("Enter a name for the API key.", nameof(name));
        if (normalizedName.Length > 64)
            throw new ArgumentException("API key names must be 64 characters or fewer.", nameof(name));
        if (string.IsNullOrWhiteSpace(normalizedKey))
            throw new ArgumentException("Enter an API key.", nameof(apiKey));

        lock (_apiKeyLock)
        {
            var document = LoadApiKeyDocument(throwOnFailure: true);
            if (document.Keys.Any(key => string.Equals(key.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("An API key with that name already exists.");

            var entry = new StoredApiKey
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = normalizedName,
                Value = normalizedKey,
            };
            document.Keys.Add(entry);
            document.ActiveKeyId = entry.Id;
            SaveApiKeyDocument(document);
        }

        ApiKeyChanged?.Invoke();
    }

    public void SelectApiKey(string id)
    {
        lock (_apiKeyLock)
        {
            var document = LoadApiKeyDocument(throwOnFailure: true);
            if (!document.Keys.Any(key => key.Id == id))
                throw new InvalidOperationException("The selected API key no longer exists.");

            document.ActiveKeyId = id;
            SaveApiKeyDocument(document);
        }

        ApiKeyChanged?.Invoke();
    }

    public void RemoveApiKey(string id)
    {
        lock (_apiKeyLock)
        {
            var document = LoadApiKeyDocument(throwOnFailure: true);
            var removed = document.Keys.RemoveAll(key => key.Id == id);
            if (removed == 0)
                return;

            if (document.ActiveKeyId == id)
                document.ActiveKeyId = document.Keys.FirstOrDefault()?.Id;
            SaveApiKeyDocument(document);
        }

        ApiKeyChanged?.Invoke();
    }

    public void SaveApiKey(string apiKey)
    {
        lock (_apiKeyLock)
        {
            var document = LoadApiKeyDocument(throwOnFailure: true);
            var active = document.Keys.FirstOrDefault(key => key.Id == document.ActiveKeyId)
                ?? document.Keys.FirstOrDefault();
            if (active is null)
            {
                active = new StoredApiKey
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = "Default",
                };
                document.Keys.Add(active);
                document.ActiveKeyId = active.Id;
            }

            active.Value = apiKey.Trim();
            SaveApiKeyDocument(document);
        }

        ApiKeyChanged?.Invoke();
    }

    public bool HasApiKey()
    {
        return !string.IsNullOrWhiteSpace(LoadApiKey());
    }

    private ApiKeyDocument LoadApiKeyDocument(bool throwOnFailure = false)
    {
        try
        {
            if (!File.Exists(_apiKeyPath))
                return new ApiKeyDocument();

            var encrypted = File.ReadAllBytes(_apiKeyPath);
            var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            var plaintext = Encoding.UTF8.GetString(decrypted);
            if (plaintext.TrimStart().StartsWith('{'))
            {
                var document = JsonSerializer.Deserialize<ApiKeyDocument>(plaintext) ?? new ApiKeyDocument();
                document.Keys = document.Keys
                    .Where(key => !string.IsNullOrWhiteSpace(key.Id) && !string.IsNullOrWhiteSpace(key.Value))
                    .ToList();
                if (!document.Keys.Any(key => key.Id == document.ActiveKeyId))
                    document.ActiveKeyId = document.Keys.FirstOrDefault()?.Id;
                return document;
            }

            return string.IsNullOrWhiteSpace(plaintext)
                ? new ApiKeyDocument()
                : new ApiKeyDocument
                {
                    ActiveKeyId = "legacy",
                    Keys = [new StoredApiKey { Id = "legacy", Name = "Default", Value = plaintext }],
                };
        }
        catch (Exception ex)
        {
            _logger.Log($"apikey_load_failed error=\"{ex.Message}\"");
            if (throwOnFailure)
                throw new InvalidOperationException("The encrypted API-key file could not be read and was left unchanged.", ex);
            return new ApiKeyDocument();
        }
    }

    private void SaveApiKeyDocument(ApiKeyDocument document)
    {
        Directory.CreateDirectory(_appDataDirectory);
        var plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document));
        var encrypted = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
        var temporaryPath = _apiKeyPath + ".tmp";
        File.WriteAllBytes(temporaryPath, encrypted);
        File.Move(temporaryPath, _apiKeyPath, overwrite: true);
    }

    private static string MaskApiKey(string apiKey)
    {
        if (apiKey.Length <= 4)
            return new string('•', apiKey.Length);

        var prefixLength = Math.Min(3, apiKey.Length - 4);
        return $"{apiKey[..prefixLength]}…{apiKey[^4..]}";
    }
}

internal sealed record ApiKeyInfo(string Id, string Name, string MaskedKey, bool IsActive)
{
    public string DisplayName => $"{Name}  —  {MaskedKey}";
}

internal sealed class ApiKeyDocument
{
    public string? ActiveKeyId { get; set; }
    public List<StoredApiKey> Keys { get; set; } = [];
}

internal sealed class StoredApiKey
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}
