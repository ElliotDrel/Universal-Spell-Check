namespace UniversalSpellCheck;

// In-memory API key cache primed at startup so the spellcheck hot path never
// hits disk + DPAPI to read the key.
internal sealed class CachedSettings
{
    private readonly SettingsStore _store;
    private volatile string? _apiKey;

    public CachedSettings(SettingsStore store)
    {
        _store = store;
        _apiKey = store.LoadApiKey();
        store.ApiKeyChanged += OnApiKeyChanged;
    }

    public string? ApiKey => _apiKey;

    private void OnApiKeyChanged()
    {
        _apiKey = _store.LoadApiKey();
    }
}
