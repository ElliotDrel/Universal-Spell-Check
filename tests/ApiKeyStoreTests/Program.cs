using System.Security.Cryptography;
using System.Text;
using UniversalSpellCheck;

var testRoot = Path.Combine(Path.GetTempPath(), "UniversalSpellCheck.ApiKeyStoreTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);

try
{
    var keyPath = Path.Combine(testRoot, "apikey.dat");
    var logger = new DiagnosticsLogger(Path.Combine(testRoot, "test.log"));

    const string legacyKey = "sk-legacy-1234567890";
    File.WriteAllBytes(
        keyPath,
        ProtectedData.Protect(Encoding.UTF8.GetBytes(legacyKey), null, DataProtectionScope.CurrentUser));

    var store = new SettingsStore(logger, testRoot, keyPath);
    Assert(store.LoadApiKey() == legacyKey, "Legacy single-key file did not load.");
    var legacyInfo = store.LoadApiKeyInfos().Single();
    Assert(legacyInfo.Name == "Default", "Legacy key did not receive the Default name.");
    Assert(legacyInfo.MaskedKey == "sk-…7890", "Legacy key mask was incorrect.");
    Assert(!legacyInfo.MaskedKey.Contains("legacy", StringComparison.Ordinal), "Masked key exposed secret content.");

    var cached = new CachedSettings(store);
    var changedCount = 0;
    store.ApiKeyChanged += () => changedCount++;

    const string workKey = "sk-work-abcdefghijklmnopqrstuvwxyz";
    store.AddApiKey("Work", workKey);
    Assert(store.LoadApiKey() == workKey, "New key was not made active.");
    Assert(cached.ApiKey == workKey, "Cached key did not update after add.");

    var infos = store.LoadApiKeyInfos();
    Assert(infos.Count == 2, "Key collection did not retain the legacy key.");
    var workInfo = infos.Single(key => key.Name == "Work");
    Assert(workInfo.IsActive, "New key was not marked active.");
    Assert(workInfo.MaskedKey == "sk-…wxyz", "New key mask was incorrect.");
    Assert(!Encoding.UTF8.GetString(File.ReadAllBytes(keyPath)).Contains(workKey, StringComparison.Ordinal),
        "Encrypted key file contained the plaintext key.");

    store.SelectApiKey(legacyInfo.Id);
    Assert(store.LoadApiKey() == legacyKey, "Selecting a key did not change the active key.");
    Assert(cached.ApiKey == legacyKey, "Cached key did not update after selection.");

    store.RemoveApiKey(legacyInfo.Id);
    Assert(store.LoadApiKey() == workKey, "Removing the active key did not select the remaining key.");
    Assert(cached.ApiKey == workKey, "Cached key did not update after removal.");
    Assert(changedCount == 3, "API-key change notifications were not raised exactly once per change.");

    var otherChannelRoot = Path.Combine(testRoot, "other-channel");
    var otherStore = new SettingsStore(
        logger,
        otherChannelRoot,
        Path.Combine(otherChannelRoot, "apikey.dat"));
    Assert(otherStore.LoadApiKey() is null, "A separate channel unexpectedly shared API keys.");

    var corruptRoot = Path.Combine(testRoot, "corrupt-channel");
    Directory.CreateDirectory(corruptRoot);
    var corruptPath = Path.Combine(corruptRoot, "apikey.dat");
    var corruptBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes("{invalid-json"), null, DataProtectionScope.CurrentUser);
    File.WriteAllBytes(corruptPath, corruptBytes);
    var corruptStore = new SettingsStore(logger, corruptRoot, corruptPath);
    var mutationFailedSafely = false;
    try
    {
        corruptStore.AddApiKey("Must not save", "sk-must-not-save");
    }
    catch (InvalidOperationException)
    {
        mutationFailedSafely = true;
    }
    Assert(mutationFailedSafely, "A mutation did not stop when the encrypted collection was unreadable.");
    Assert(File.ReadAllBytes(corruptPath).SequenceEqual(corruptBytes),
        "A failed collection load overwrote the recoverable encrypted file.");

    Console.WriteLine("api_key_store_tests_ok");
    return 0;
}
finally
{
    Directory.Delete(testRoot, recursive: true);
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
