using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace UniversalSpellCheck.Bench;

// A copy-source fixture must not introduce WinForms/OLE flush callbacks into the
// same STA that is measuring the product. Publish the two standard native formats.
internal static class FixtureClipboard
{
    internal static void Copy(IntPtr owner, string text, string html)
    {
        int attempt = 0;
        while (!OpenClipboard(owner))
        {
            if (++attempt == 20) throw new Win32Exception(Marshal.GetLastWin32Error(), "Fixture clipboard stayed locked before copy.");
            Thread.Sleep(10);
        }
        try
        {
            if (!EmptyClipboard()) throw new Win32Exception(Marshal.GetLastWin32Error(), "Fixture could not empty clipboard.");
            Set(13, Encoding.Unicode.GetBytes(text + "\0"));
            var format = RegisterClipboardFormat("HTML Format");
            if (format == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            Set(format, Encoding.UTF8.GetBytes(html + "\0"));
        }
        finally { CloseClipboard(); }
    }

    private static void Set(uint format, byte[] bytes)
    {
        var memory = GlobalAlloc(2, (UIntPtr)bytes.Length);
        if (memory == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var buffer = GlobalLock(memory);
            if (buffer == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { Marshal.Copy(bytes, 0, buffer, bytes.Length); }
            finally { GlobalUnlock(memory); }
            if (SetClipboardData(format, memory) == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            memory = IntPtr.Zero; // Windows owns the allocation after successful SetClipboardData.
        }
        finally { if (memory != IntPtr.Zero) GlobalFree(memory); }
    }

    [DllImport("user32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll", SetLastError=true)] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
}
