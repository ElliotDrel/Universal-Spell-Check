using System.Runtime.InteropServices;

namespace UniversalSpellCheck;

internal static class KeyboardState
{
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;

    public static async Task<bool> WaitForHotkeyReleaseAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsHotkeyDown())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return !IsHotkeyDown();
    }

    // Physical key state snapshot for capture-failure diagnostics. A modifier
    // still reading "1" when Ctrl+C was injected explains a swallowed copy
    // (the target app saw Ctrl+Alt+C / Alt+C instead).
    public static string DescribeModifierState()
    {
        return $"ctrl={(IsDown(VkControl) ? 1 : 0)}" +
               $" alt={(IsDown(VkMenu) ? 1 : 0)}" +
               $" shift={(IsDown(VkShift) ? 1 : 0)}" +
               $" win={(IsDown(VkLWin) || IsDown(VkRWin) ? 1 : 0)}" +
               $" hotkey_key={(IsDown((int)BuildChannel.HotkeyVk) ? 1 : 0)}";
    }

    private static bool IsHotkeyDown()
    {
        return IsDown(VkControl) || IsDown(VkMenu) || IsDown((int)BuildChannel.HotkeyVk);
    }

    private static bool IsDown(int virtualKey)
    {
        return (GetAsyncKeyState(virtualKey) & unchecked((short)0x8000)) != 0;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
