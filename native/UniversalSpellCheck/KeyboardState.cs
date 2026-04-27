using System.Runtime.InteropServices;

namespace UniversalSpellCheck;

internal static class KeyboardState
{
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkU = 0x55;

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

    private static bool IsHotkeyDown()
    {
        return IsDown(VkControl) || IsDown(VkMenu) || IsDown(VkU);
    }

    private static bool IsDown(int virtualKey)
    {
        return (GetAsyncKeyState(virtualKey) & unchecked((short)0x8000)) != 0;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
