namespace UniversalSpellCheck;

// In-memory API key cache primed at startup so the spellcheck hot path never
// hits disk + DPAPI to read the key.
internal sealed class CachedSettings
{
    private readonly SettingsStore _store;
    private volatile string? _apiKey;
    private volatile bool _developerLogging;

    public CachedSettings(SettingsStore store)
    {
        _store = store;
        _apiKey = store.LoadApiKey();
        _developerLogging = store.Load().DeveloperLogging;
        store.SettingsChanged += () => _developerLogging = store.Load().DeveloperLogging;
        store.ApiKeyChanged += OnApiKeyChanged;
    }

    public bool DeveloperLogging => _developerLogging;
    public string? ApiKey => _apiKey;
    public SettingsStore SettingsStore => _store;

    private void OnApiKeyChanged()
    {
        _apiKey = _store.LoadApiKey();
    }
}
