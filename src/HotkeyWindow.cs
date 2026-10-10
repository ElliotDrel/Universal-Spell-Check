using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UniversalSpellCheck;

internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int HotkeyId = 1;
    private const int WmHotkey = 0x0312;
    private const uint SmtoAbortIfHung = 0x0002;

    private static readonly uint UpdateMessage = RegisterWindowMessage(BuildChannel.UpdateRequestMessage);
    private bool _registered;

    public event EventHandler? HotkeyPressed;
    public event EventHandler? RestartRequested;

    public HotkeyWindow(string? windowTitle = null)
    {
        CreateHandle(new CreateParams
        {
            Caption = windowTitle ?? BuildChannel.HotkeyWindowTitle
        });
    }

    public static bool RequestRestart(string? windowTitle = null)
    {
        var window = FindWindow(null, windowTitle ?? BuildChannel.HotkeyWindowTitle);
        return window != IntPtr.Zero && UpdateMessage != 0
            && SendMessageTimeout(window, UpdateMessage, IntPtr.Zero, IntPtr.Zero,
                SmtoAbortIfHung, 2000, out var accepted) != IntPtr.Zero && accepted == new IntPtr(1);
    }

    public void Register(uint modifiers, uint vk)
    {
        if (_registered)
        {
            return;
        }

        if (!RegisterHotKey(Handle, HotkeyId, modifiers, vk))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Failed to register hotkey (vk=0x{vk:X2}).");
        }

        _registered = true;
    }

    public void Unregister()
    {
        if (!_registered)
        {
            return;
        }

        UnregisterHotKey(Handle, HotkeyId);
        _registered = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyId)
        {
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (UpdateMessage != 0 && (uint)m.Msg == UpdateMessage)
        {
            m.Result = IntPtr.Zero;
            if (RestartRequested is not null)
            {
                RestartRequested.Invoke(this, EventArgs.Empty);
                m.Result = new IntPtr(1);
            }
            return;
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Unregister();
        DestroyHandle();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message,
        IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
