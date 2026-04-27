namespace UniversalSpellCheck;

using System.Text.Json;

internal sealed class DiagnosticsLogger
{
    private readonly object _lock = new();
    private readonly string _logPath;

    public DiagnosticsLogger(string logPath)
    {
        _logPath = logPath;
        Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
    }

    public void Log(string message)
    {
        try
        {
            lock (_lock)
            {
                File.AppendAllText(
                    _logPath,
                    $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never block the hard-loop spike.
        }
    }

    public void LogData(string eventName, object data)
    {
        try
        {
            var json = JsonSerializer.Serialize(data);
            Log($"{eventName} {json}");
        }
        catch (Exception ex)
        {
            Log($"{eventName}_log_failed error=\"{ex.Message}\"");
        }
    }
}
