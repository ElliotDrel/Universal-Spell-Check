using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace UniversalSpellCheck;

internal sealed class ActiveWindowInfo
{
    public string ProcessName { get; init; } = "unknown";
    public string WindowTitle { get; init; } = "";

    public static ActiveWindowInfo Capture()
    {
        try
        {
            var handle = GetForegroundWindow();
            if (handle == IntPtr.Zero)
            {
                return new ActiveWindowInfo();
            }

            GetWindowThreadProcessId(handle, out var processId);
            var processName = "unknown";
            if (processId != 0)
            {
                processName = Process.GetProcessById((int)processId).ProcessName;
            }

            var title = new StringBuilder(256);
            GetWindowText(handle, title, title.Capacity);

            return new ActiveWindowInfo
            {
                ProcessName = processName,
                WindowTitle = title.ToString()
            };
        }
        catch
        {
            return new ActiveWindowInfo();
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
}
