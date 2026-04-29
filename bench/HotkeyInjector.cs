using System.Runtime.InteropServices;

namespace UniversalSpellCheck.Bench;

/// <summary>
/// Synthesizes a Ctrl+Alt+B keystroke via Win32 SendInput. Used by the bench
/// to fire the registered global hotkey from the same path the OS uses for a
/// physical keypress, so we measure the real WM_HOTKEY dispatch cost.
/// </summary>
internal static class HotkeyInjector
{
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12;       // Alt
    private const ushort VK_B = 0x42;
    public const uint HotkeyVk = VK_B;
    public const uint HotkeyModifiers = 0x0001 /*MOD_ALT*/ | 0x0002 /*MOD_CONTROL*/ | 0x4000 /*MOD_NOREPEAT*/;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    public static void FireCtrlAltB()
    {
        // Press Ctrl, press Alt, press B, release B, release Alt, release Ctrl.
        var inputs = new[]
        {
            BuildKey(VK_CONTROL, keyUp: false),
            BuildKey(VK_MENU,    keyUp: false),
            BuildKey(VK_B,       keyUp: false),
            BuildKey(VK_B,       keyUp: true),
            BuildKey(VK_MENU,    keyUp: true),
            BuildKey(VK_CONTROL, keyUp: true),
        };

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            throw new InvalidOperationException(
                $"SendInput delivered {sent}/{inputs.Length} events. LastError={Marshal.GetLastWin32Error()}");
        }
    }

    private static INPUT BuildKey(ushort vk, bool keyUp)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                }
            }
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
