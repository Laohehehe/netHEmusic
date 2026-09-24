using System;
using System.Runtime.InteropServices;
using netHEmusic.Core.Logging;

namespace netHEmusic.Core;

/// <summary>系统托盘图标（Win32 Shell_NotifyIcon + 隐藏消息窗）。用于「关闭=最小化到托盘」：提供 打开/退出 菜单与双击恢复。</summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_USER = 0x0400;
    private const int WM_TRAYICON = WM_USER + 1;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_CONTEXTMENU = 0x007B;
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_CLOSE = 0x0010;
    private const uint MF_STRING = 0x0, TPM_LEFTALIGN = 0x0, TPM_RIGHTBUTTON = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize; public IntPtr hWnd; public uint uID; public uint uFlags; public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState; public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo; public uint uTimeout; public uint uVersion;
    }

    [DllImport("shell32.dll")] private static extern bool Shell_NotifyIcon(uint m, ref NOTIFYICONDATA d);
    [DllImport("user32.dll")] private static extern IntPtr CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr p, IntPtr m, IntPtr i, IntPtr pv);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    // 注意：GetModuleHandle 在 kernel32.dll，不在 user32.dll。
    // 之前写成 user32 会抛 EntryPointNotFoundException，托盘图标一直没建起来，
    // 而且异常从 Closing 处理里抛出去会把"关闭到托盘"整个带崩。
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadIconW(IntPtr h, IntPtr id);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconExW(string file, int index, out IntPtr large, out IntPtr small, uint count);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr h, uint f, UIntPtr id, string s);
    [DllImport("user32.dll")] private static extern bool TrackPopupMenu(IntPtr h, uint f, int x, int y, int r, IntPtr w, IntPtr rc);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int idx, IntPtr v);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);

    private IntPtr _hwnd, _hIcon; private readonly IntPtr _mainHwnd; private readonly Action _onOpen, _onExit;
    private readonly WndProcDelegate _wp; private readonly string _tip = "netHEmusic 网易云音乐下载器";
    private delegate IntPtr WndProcDelegate(IntPtr h, uint m, IntPtr w, IntPtr l);

    public TrayIcon(IntPtr mainHwnd, Action onOpen, Action onExit)
    {
        _mainHwnd = mainHwnd; _onOpen = onOpen; _onExit = onExit; _wp = WndProc;
        _hwnd = CreateWindowEx(0, "STATIC", "netHEmusicTray", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        SetWindowLongPtr(_hwnd, -4, Marshal.GetFunctionPointerForDelegate(_wp));
        _hIcon = LoadAppIcon();
        var nid = new NOTIFYICONDATA { cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = _hwnd, uID = 1, uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP, uCallbackMessage = WM_TRAYICON, hIcon = _hIcon, szTip = _tip };
        Shell_NotifyIcon(NIM_ADD, ref nid);
        LogManager.Log("托盘图标已创建");
    }

    /// <summary>
    /// 取托盘图标：优先从自身 exe 里抽第一个图标组（ExtractIconEx），
    /// 失败再退回系统默认应用图标。之前用 LoadIcon(自身模块, 32512) 在 .NET exe 上取不到，
    /// 返回 NULL → Shell_NotifyIcon 照样"添加成功"，但托盘上是看不见的空白。
    /// </summary>
    private static IntPtr LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && ExtractIconExW(exe, 0, out var big, out var small, 1) > 0)
            {
                if (small != IntPtr.Zero) { if (big != IntPtr.Zero) DestroyIcon(big); return small; }
                if (big != IntPtr.Zero) return big;
            }
        }
        catch { }
        try { return LoadIconW(IntPtr.Zero, new IntPtr(32512)); } catch { }
        return IntPtr.Zero;
    }

    private IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        try
        {
            if (m == WM_TRAYICON)
            {
                var evt = (uint)l.ToInt64();
                if (evt == WM_LBUTTONDBLCLK) { _onOpen(); return IntPtr.Zero; }
                if (evt == WM_CONTEXTMENU)
                {
                    var menu = CreatePopupMenu();
                    AppendMenuW(menu, MF_STRING, new UIntPtr(1), "打开 netHEmusic");
                    AppendMenuW(menu, MF_STRING, new UIntPtr(2), "退出");
                    TrackPopupMenu(menu, TPM_LEFTALIGN | TPM_RIGHTBUTTON, 0, 0, 0, h, IntPtr.Zero);
                    DestroyMenu(menu); return IntPtr.Zero;
                }
            }
            else if (m == WM_COMMAND)
            {
                var id = w.ToInt64();
                if (id == 1) _onOpen(); else if (id == 2) _onExit();
                return IntPtr.Zero;
            }
            else if (m == WM_CLOSE) return IntPtr.Zero;
        }
        catch (Exception e) { LogManager.Error("托盘消息失败: " + e.Message); }
        return DefWindowProcW(h, m, w, l);
    }

    public static void ShowMainWindow(IntPtr hwnd) { try { ShowWindow(hwnd, 5); SetForegroundWindow(hwnd); } catch { } }

    public void Dispose()
    {
        try { var nid = new NOTIFYICONDATA { cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = _hwnd, uID = 1 }; Shell_NotifyIcon(NIM_DELETE, ref nid); if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon); if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd); } catch { }
    }
}
