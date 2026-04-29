namespace UniversalSpellCheck;

using System.Diagnostics;
using System.Text;
using System.Text.Json;

internal sealed class DiagnosticsLogger
{
    private static readonly int Pid = Process.GetCurrentProcess().Id;

    private readonly object _lock = new();
    private readonly string _logPath;

    public DiagnosticsLogger(string logPath)
    {
        _logPath = logPath;
        Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
    }

    public void Log(string message)
    {
        // Every log line is stamped with channel + app_version + pid so a
        // single shared daily file (Prod and Dev both append) can be filtered
        // downstream by build provenance.
        var line =
            $"{DateTimeOffset.Now:O} channel={BuildChannel.ChannelName} " +
            $"app_version={BuildChannel.AppVersion} pid={Pid} {message}" +
            Environment.NewLine;
        AppendWithRetry(line);
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

    private void AppendWithRetry(string line)
    {
        // Two processes (Prod + Dev) can append to the same file concurrently.
        // FileShare.ReadWrite plus a short retry loop covers transient sharing
        // violations without blocking the spell-check pipeline.
        var bytes = Encoding.UTF8.GetBytes(line);
        var attempts = 0;
        lock (_lock)
        {
            while (true)
            {
                try
                {
                    using var fs = new FileStream(
                        _logPath,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.ReadWrite);
                    fs.Write(bytes, 0, bytes.Length);
                    return;
                }
                catch (IOException) when (attempts < 5)
                {
                    attempts++;
                    Thread.Sleep(10 * attempts);
                }
                catch
                {
                    // Logging must never block the hot path.
                    return;
                }
            }
        }
    }
}
