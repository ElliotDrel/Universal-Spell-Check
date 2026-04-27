namespace UniversalSpellCheck;

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
}
