using System;
using System.Runtime.InteropServices;

namespace netHEmusic.Platform;

/// <summary>
/// 原生 MessageBox 兜底提示。
/// 用途：在 WinUI 窗口还没建起来、或者窗口本身就是坏的时候（例如 WebView2 起不来、
/// 日志/配置目录写不进去）给用户一个**看得见**的错误，而不是一片透明窗口。
/// </summary>
public static class NativeDialog
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONERROR = 0x00000010;
    private const uint MB_ICONWARNING = 0x00000030;
    private const uint MB_ICONINFORMATION = 0x00000040;
    private const uint MB_SETFOREGROUND = 0x00010000;
    private const uint MB_TOPMOST = 0x00040000;

    public static void Error(string title, string text) => Show(title, text, MB_OK | MB_ICONERROR);
    public static void Warn(string title, string text) => Show(title, text, MB_OK | MB_ICONWARNING);
    public static void Info(string title, string text) => Show(title, text, MB_OK | MB_ICONINFORMATION);

    private static void Show(string title, string text, uint flags)
    {
        try { MessageBoxW(IntPtr.Zero, text ?? "", title ?? "", flags | MB_SETFOREGROUND | MB_TOPMOST); }
        catch { /* 连弹窗都失败就算了，绝不能因此再抛异常 */ }
    }
}
