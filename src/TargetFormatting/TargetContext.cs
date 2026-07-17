namespace UniversalSpellCheck;

internal sealed record TargetContext(
    string ProcessName,
    int ProcessId,
    IntPtr WindowHandle,
    IntPtr RootOwnerWindowHandle,
    string WindowTitle,
    BrowserTargetContext? Browser)
{
    public bool HasSameDesktopDestination(TargetContext other)
    {
        return ProcessId != 0
            && RootOwnerWindowHandle != IntPtr.Zero
            && ProcessId == other.ProcessId
            && RootOwnerWindowHandle == other.RootOwnerWindowHandle;
    }
}

internal sealed record BrowserTargetContext(
    string Browser,
    bool Focused,
    int WindowId,
    int TabId,
    string Scheme,
    string Host,
    string Path,
    long ReceivedAtStopwatchTicks,
    long ExtensionObservedAtUnixMs)
{
    public override string ToString() => $"{Browser}:{Host}";
}

internal static class TargetMatch
{
    public static bool ProcessName(TargetContext context, string processName)
    {
        return string.Equals(context.ProcessName, processName, StringComparison.OrdinalIgnoreCase);
    }

    public static bool Host(BrowserTargetContext? browser, string host, bool includeSubdomains = false)
    {
        if (browser is null || !IsWebScheme(browser.Scheme)) return false;

        var normalizedActual = browser.Host.TrimEnd('.');
        var normalizedExpected = host.Trim().TrimEnd('.');
        if (string.Equals(normalizedActual, normalizedExpected, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return includeSubdomains
            && normalizedActual.Length > normalizedExpected.Length
            && normalizedActual.EndsWith('.' + normalizedExpected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWebScheme(string scheme)
    {
        return string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSupportedWebContext(BrowserTargetContext? browser)
    {
        return browser is { Focused: true } && IsWebScheme(browser.Scheme);
    }
}
