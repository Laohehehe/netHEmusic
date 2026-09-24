using System;
using System.Runtime.InteropServices;
using netHEmusic.Core.Logging;

namespace netHEmusic.Core;

/// <summary>托盘菜单需要读的状态和能触发的动作（由 MainWindow 提供，取不到就当作不可用）。</summary>
public sealed class TrayActions
{
    public Action? Prev;
    public Action? Next;
    public Action? TogglePlay;
    public Func<bool>? IsPlaying;
    public Func<string>? PlayMode;
    public Action<string>? SetPlayMode;
    public Func<bool>? IsLyricOn;
    public Action<bool>? SetLyric;
}

/// <summary>
/// 系统托盘图标（Win32 Shell_NotifyIcon + 隐藏消息窗）。
/// 左键单击 = 恢复主窗口；右键 = 菜单：打开 / 上一首 / 暂停 / 下一首 / 播放模式 ▸ / 桌面歌词 / 退出。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_USER = 0x0400;
    private const int WM_TRAYICON = WM_USER + 1;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NOTIFYICON_VERSION_4 = 4;
    private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_CONTEXTMENU = 0x007B;
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_NULL = 0x0000;
    // version 4 的回调：左键选中 / 键盘选中（这些值出现在 lParam 里）
    private const uint NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401;

    // 菜单项 id
    private const int ID_OPEN = 1, ID_EXIT = 2;
    private const int ID_PREV = 10, ID_PLAY = 11, ID_NEXT = 12;
    private const int ID_MODE_ORDER = 20, ID_MODE_LIST = 21, ID_MODE_SINGLE = 22, ID_MODE_RANDOM = 23;
    private const int ID_LYRIC = 30;

    // CreatePopupMenu / AppendMenu 标志
    private const uint MF_STRING = 0x00000000, MF_SEPARATOR = 0x00000800, MF_CHECKED = 0x00000008, MF_POPUP = 0x00000010;
    private const uint TPM_LEFTALIGN = 0x0000, TPM_RIGHTBUTTON = 0x0002, TPM_NONOTIFY = 0x0080, TPM_RETURNCMD = 0x0100;

    // 必须是完整的 NOTIFYICONDATAW（x64 上 976 字节），cbSize 对不上外壳就按老版本对待。
    // 另外 uTimeout / uVersion 在 C 里是 union，只能留一个字段，写两个会让结构凭空大 4 字节。
    // CharSet=Unicode 不能少：少了的话下面 ByValTStr 的 SizeConst 按「字节」算，
    // 整个结构会缩水到 528 字节（正确是 976），cbSize 对不上任何版本，外壳只能瞎猜。
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint m, ref NOTIFYICONDATA d);
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
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr h, uint f, UIntPtr id, string? s);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr h, uint f, int x, int y, int r, IntPtr w, IntPtr rc);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int idx, IntPtr v);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);

    private IntPtr _hwnd, _hIcon; private readonly IntPtr _mainHwnd; private readonly Action _onOpen, _onExit;
    private readonly TrayActions? _actions;
    private readonly WndProcDelegate _wp; private readonly string _tip = "netHEmusic 网易云音乐下载器";
    private delegate IntPtr WndProcDelegate(IntPtr h, uint m, IntPtr w, IntPtr l);

    public TrayIcon(IntPtr mainHwnd, Action onOpen, Action onExit, TrayActions? actions = null)
    {
        _mainHwnd = mainHwnd; _onOpen = onOpen; _onExit = onExit; _actions = actions; _wp = WndProc;
        _hwnd = CreateWindowEx(0, "STATIC", "netHEmusicTray", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        SetWindowLongPtr(_hwnd, -4, Marshal.GetFunctionPointerForDelegate(_wp));
        _hIcon = LoadAppIcon();
        var size = (uint)Marshal.SizeOf<NOTIFYICONDATA>();
        var nid = new NOTIFYICONDATA { cbSize = size, hWnd = _hwnd, uID = 1, uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP, uCallbackMessage = WM_TRAYICON, hIcon = _hIcon, szTip = _tip };
        bool ok = Shell_NotifyIcon(NIM_ADD, ref nid);
        // 关键：必须紧接着升到 version 4，否则外壳右键发过来的是 WM_RBUTTONUP（不是 WM_CONTEXTMENU），
        // 我们只认 WM_CONTEXTMENU 的话右键就完全没反应 —— 这就是「托盘不能右键」的原因。
        var ver = new NOTIFYICONDATA { cbSize = size, hWnd = _hwnd, uID = 1, uVersion = NOTIFYICON_VERSION_4 };
        bool vok = Shell_NotifyIcon(NIM_SETVERSION, ref ver);
        LogManager.Log("托盘图标已创建 add=" + ok + " setversion=" + vok + " cbSize=" + size);
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

    /// <summary>状态读取一律吞异常：托盘菜单不应该因为播放器状态拿不到就弹不出来。</summary>
    private static T Safe<T>(Func<T>? f, T def) { try { return f is null ? def : f(); } catch { return def; } }

    private static readonly (int Id, string Key, string Text)[] Modes =
    {
        (ID_MODE_ORDER,  "order",  "顺序播放"),
        (ID_MODE_LIST,   "list",   "列表循环"),
        (ID_MODE_SINGLE, "single", "单曲循环"),
        (ID_MODE_RANDOM, "random", "随机播放")
    };

    /// <summary>
    /// 右键菜单。每次弹出都重新建一遍 —— 勾选状态（播放/暂停、播放模式、桌面歌词）必须是实时的。
    /// 用 TPM_RETURNCMD 直接拿到点了哪一项，省掉 WM_COMMAND 那一套。
    /// </summary>
    private void ShowMenu(int x = int.MinValue, int y = int.MinValue)
    {
        var menu = CreatePopupMenu();
        try
        {
            bool playing = Safe(() => _actions?.IsPlaying?.Invoke() ?? false, false);
            string mode = Safe(() => _actions?.PlayMode?.Invoke() ?? "order", "order") ?? "order";
            bool lyric = Safe(() => _actions?.IsLyricOn?.Invoke() ?? false, false);

            AppendMenuW(menu, MF_STRING, new UIntPtr(ID_OPEN), "打开 netHEmusic");
            AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenuW(menu, MF_STRING, new UIntPtr(ID_PREV), "上一首");
            AppendMenuW(menu, MF_STRING, new UIntPtr(ID_PLAY), playing ? "暂停" : "播放");
            AppendMenuW(menu, MF_STRING, new UIntPtr(ID_NEXT), "下一首");

            var sub = CreatePopupMenu();
            foreach (var m in Modes)
                AppendMenuW(sub, MF_STRING | (mode == m.Key ? MF_CHECKED : 0), new UIntPtr((ulong)m.Id), m.Text);
            AppendMenuW(menu, MF_POPUP, (UIntPtr)sub, "播放模式");

            AppendMenuW(menu, MF_STRING | (lyric ? MF_CHECKED : 0), new UIntPtr(ID_LYRIC), "桌面歌词");
            AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenuW(menu, MF_STRING, new UIntPtr(ID_EXIT), "退出");

            // 标准托盘菜单三步：先把自己设成前台窗口，弹完再补一条空消息，
            // 否则点菜单外面菜单不会消失（会一直挂在屏幕上）。
            try { SetForegroundWindow(_hwnd); } catch { }
            if (x == int.MinValue || y == int.MinValue) { GetCursorPos(out var pt); x = pt.X; y = pt.Y; }
            int cmd = TrackPopupMenu(menu, TPM_LEFTALIGN | TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY, x, y, 0, _hwnd, IntPtr.Zero);
            try { PostMessageW(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero); } catch { }

            Dispatch(cmd, lyric);
        }
        catch (Exception e) { LogManager.Error("托盘菜单失败: " + e.Message); }
        finally { try { DestroyMenu(menu); } catch { } }   // DestroyMenu 会连带销毁子菜单
    }

    private void Dispatch(int cmd, bool lyricNow)
    {
        try
        {
            switch (cmd)
            {
                case ID_OPEN: _onOpen(); break;
                case ID_EXIT: _onExit(); break;
                case ID_PREV: _actions?.Prev?.Invoke(); break;
                case ID_PLAY: _actions?.TogglePlay?.Invoke(); break;
                case ID_NEXT: _actions?.Next?.Invoke(); break;
                case ID_LYRIC: _actions?.SetLyric?.Invoke(!lyricNow); break;
                case ID_MODE_ORDER: _actions?.SetPlayMode?.Invoke("order"); break;
                case ID_MODE_LIST: _actions?.SetPlayMode?.Invoke("list"); break;
                case ID_MODE_SINGLE: _actions?.SetPlayMode?.Invoke("single"); break;
                case ID_MODE_RANDOM: _actions?.SetPlayMode?.Invoke("random"); break;
            }
        }
        catch (Exception e) { LogManager.Error("托盘菜单动作失败: " + e.Message); }
    }

    private IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        try
        {
            if (m == WM_TRAYICON)
            {
                // version 4：lParam 低 16 位是鼠标消息，wParam 低/高 16 位是图标在屏幕上的坐标
                long wp = w.ToInt64();
                uint evt = (uint)(l.ToInt64() & 0xFFFF);
                int ax = (short)(wp & 0xFFFF), ay = (short)((wp >> 16) & 0xFFFF);
                LogManager.Debug("托盘回调: lParam=0x" + evt.ToString("X4") + " 坐标=" + ax + "," + ay);
                if (evt == WM_LBUTTONUP || evt == WM_LBUTTONDBLCLK || evt == NIN_SELECT || evt == NIN_KEYSELECT)
                {
                    _onOpen(); return IntPtr.Zero;
                }
                if (evt == WM_CONTEXTMENU || evt == WM_RBUTTONUP)
                {
                    // 坐标只有在屏幕范围内才信（老回调里 wParam 是图标 id，不是坐标）
                    int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77);   // SM_XVIRTUALSCREEN / SM_YVIRTUALSCREEN
                    bool sane = ax >= vx && ax < vx + GetSystemMetrics(78) && ay >= vy && ay < vy + GetSystemMetrics(79);
                    ShowMenu(sane ? ax : int.MinValue, sane ? ay : int.MinValue);
                    return IntPtr.Zero;
                }
            }
            else if (m == WM_COMMAND)
            {
                var id = w.ToInt64();
                if (id == ID_OPEN) _onOpen(); else if (id == ID_EXIT) _onExit();
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
