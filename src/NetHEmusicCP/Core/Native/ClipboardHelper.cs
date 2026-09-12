using System;
using System.Runtime.InteropServices;

namespace netHEmusic.Core.Native;

/// <summary>Win32 剪贴板写入（用于“分享”复制链接）。unpackaged 应用里比 WinRT Clipboard 更稳。</summary>
internal static class ClipboardHelper
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);

    public static bool SetText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        IntPtr hMem = IntPtr.Zero;
        try
        {
            var bytes = (text.Length + 1) * 2;                 // UTF-16 + 结尾 0
            hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
            if (hMem == IntPtr.Zero) return false;
            var target = GlobalLock(hMem);
            if (target == IntPtr.Zero) return false;
            Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
            Marshal.WriteInt16(target, text.Length * 2, 0);
            GlobalUnlock(hMem);

            if (!OpenClipboard(IntPtr.Zero)) return false;
            EmptyClipboard();
            if (SetClipboardData(CF_UNICODETEXT, hMem) == IntPtr.Zero) { CloseClipboard(); return false; }
            CloseClipboard();
            hMem = IntPtr.Zero;                                 // 所有权已交给剪贴板
            return true;
        }
        catch { return false; }
    }
}
