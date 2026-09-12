using System;
using System.Runtime.InteropServices;
using netHEmusic.Core.Logging;

namespace netHEmusic.Core.Native;

/// <summary>
/// 屏蔽标题栏的系统菜单：
///   - 在自定义标题栏（拖拽区）上点右键不再弹出「还原/移动/大小/最小化/最大化/关闭」
///   - 顺带屏蔽 Alt+Space 的系统菜单
/// 做法是用 comctl32 的 SetWindowSubclass 挂一层子类化 WndProc，
/// 只吞掉标题栏相关的非客户区右键消息，WebView 内容区的右键不受影响。
/// </summary>
internal static class WindowMenuBlocker
{
    private const uint WM_CONTEXTMENU = 0x007B;
    private const uint WM_NCRBUTTONDOWN = 0x00A4;
    private const uint WM_NCRBUTTONUP = 0x00A5;
    private const uint WM_NCRBUTTONDBLCLK = 0x00A6;
    private const uint WM_SYSCOMMAND = 0x0112;
    private const uint SC_KEYMENU = 0xF100;
    private const uint WM_NCHITTEST = 0x0084;

    // 屏幕坐标 -> 窗口命中测试：标题栏区域返回 HTCAPTION，我们据此判断右键是否落在标题栏
    private const int HTCAPTION = 2;

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);

    // 必须保持引用，否则委托会被 GC 回收导致崩溃
    private static SubclassProc? _proc;
    private static IntPtr _hwnd = IntPtr.Zero;

    public static void Attach(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || hwnd == _hwnd) return;
        try
        {
            _proc = WndProc;
            if (SetWindowSubclass(hwnd, _proc, UIntPtr.Zero, UIntPtr.Zero))
            {
                _hwnd = hwnd;
                LogManager.Log("已屏蔽标题栏系统菜单（右键 / Alt+Space）");
            }
            else LogManager.Debug("SetWindowSubclass 失败: " + Marshal.GetLastWin32Error());
        }
        catch (Exception e) { LogManager.Debug("屏蔽标题栏菜单失败: " + e.Message); }
    }

    private static bool IsOnCaption(IntPtr hWnd)
    {
        try
        {
            if (!GetCursorPos(out var p)) return false;
            var lp = ((p.Y & 0xFFFF) << 16) | (p.X & 0xFFFF);
            return (long)SendMessage(hWnd, WM_NCHITTEST, IntPtr.Zero, (IntPtr)lp) == HTCAPTION;
        }
        catch { return false; }
    }

    private static IntPtr WndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
    {
        switch (uMsg)
        {
            case WM_NCRBUTTONDOWN:
            case WM_NCRBUTTONUP:
            case WM_NCRBUTTONDBLCLK:
                return IntPtr.Zero;                                  // 非客户区（标题栏）右键：直接吞掉
            case WM_CONTEXTMENU:
                if (wParam == hWnd && IsOnCaption(hWnd)) return IntPtr.Zero;
                break;
            case WM_SYSCOMMAND:
                if (((uint)wParam & 0xFFF0) == SC_KEYMENU) return IntPtr.Zero;   // Alt+Space
                break;
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }
}
