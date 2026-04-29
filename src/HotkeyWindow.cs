using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UniversalSpellCheck;

internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int HotkeyId = 1;
    private const int WmHotkey = 0x0312;

    private bool _registered;

    public event EventHandler? HotkeyPressed;

    public HotkeyWindow()
    {
        CreateHandle(new CreateParams
        {
            Caption = "UniversalSpellCheckHotkeyWindow"
        });
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

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Unregister();
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
