using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT;
using netHEmusic.Core;
using netHEmusic.Core.Logging;
using netHEmusic.Core.Theme;

namespace netHEmusic.Core.Native;

/// <summary>WinUI3 窗口辅助：图标、16:9 尺寸与居中、窗口材质（Mica/亚克力）、系统菜单主题、实时日志控制台。</summary>
public static class WindowHelper
{
    public static IntPtr Hwnd(Window w) => WinRT.Interop.WindowNative.GetWindowHandle(w);

    /// <summary>设置窗口图标（优先应用根目录 Assets\app.ico，其次 resources\icon.ico）。</summary>
    public static void SetIcon(Window w)
    {
        try
        {
            string? ico = null;
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"),
                Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"),
                // 开发时资源目录
                FindInParents("resources/icon.ico"),
            };
            foreach (var c in candidates) if (c is not null && File.Exists(c)) { ico = c; break; }
            if (ico is not null) w.AppWindow.SetIcon(ico);
            // 任务栏缩略图 / Alt+Tab 的标题用的是窗口的【小图标】(WM_SETICON)，AppWindow.SetIcon 只管任务栏大图标。
            // 开发时的 exe 在 bin 深处，找不到 Assets\*.ico 时就用 exe 自带的图标（否则缩略图左上角是一格白板）。
            var h = Hwnd(w);
            IntPtr small = IntPtr.Zero, big = IntPtr.Zero;
            if (ico is not null)
            {
                small = LoadImage(IntPtr.Zero, ico, IMAGE_ICON, GetSystemMetrics(49), GetSystemMetrics(50), LR_LOADFROMFILE);
                big = LoadImage(IntPtr.Zero, ico, IMAGE_ICON, GetSystemMetrics(11), GetSystemMetrics(12), LR_LOADFROMFILE);
            }
            if (small == IntPtr.Zero || big == IntPtr.Zero)
            {
                try { ExtractIconEx(Environment.ProcessPath ?? "", 0, out var eb, out var es, 1); if (big == IntPtr.Zero) big = eb; if (small == IntPtr.Zero) small = es; }
                catch { }
            }
            if (small != IntPtr.Zero) SendMessage(h, WM_SETICON, (IntPtr)ICON_SMALL, small);
            if (big != IntPtr.Zero) SendMessage(h, WM_SETICON, (IntPtr)ICON_BIG, big);
            LogManager.Debug($"窗口图标: file={ico ?? "(无)"} small={small} big={big}");
        }
        catch (Exception e) { LogManager.Debug("设置图标失败: " + e.Message); }
    }

    private static string? FindInParents(string rel)
    {
        var root = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(root); i++)
        {
            var p = Path.Combine(Path.GetFullPath(root), rel);
            if (File.Exists(p)) return p;
            root = Path.GetDirectoryName(root);
        }
        return null;
    }

    /// <summary>按 16:9 锁定比例缩放窗口（1280x720 基准）。</summary>
    public static void Resize169(Window w, int width = 1280, int height = 720)
    {
        try { w.AppWindow.Resize(new SizeInt32(width, height)); } catch (Exception e) { LogManager.Debug("Resize 失败: " + e.Message); }
    }

    // 说明：曾经有过一套"网页拖拽带"（win_drag / win_drag_move / win_drag_end + StartDrag/DragMove/EndDrag）
    // 和一套网页侧窗口按钮（win_max / win_min / win_close + ToggleMaximize/Minimize）。
    // 标题栏改成 0 高度后网页不再自绘拖拽带，最小化/最大化/关闭也由系统标题栏按钮负责，
    // 这些消息没有任何发送方 → 2026-10-03 全部删除（死代码清理）。
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr hinst, string name, uint type, int cx, int cy, uint load);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x0010;
    private const int WM_SETICON = 0x0080, ICON_SMALL = 0, ICON_BIG = 1;

    // ---- 实时日志控制台（设置→高级 里的「显示控制台」）----
    // 用 AllocConsole 在本进程里开真控制台：能看到全部输出（含原生/崩溃信息），这是选它的理由。
    // 代价说清楚：控制台属于本进程，【关掉这个黑框就等于结束程序】—— Windows 对 CTRL_CLOSE_EVENT
    // 只给一段清理时间，SetConsoleCtrlHandler 拦不住这个终止。拦截器用在刀刃上：收到关闭事件时
    // 先把播放列表/进度存好，再让系统照常结束进程。
    [DllImport("kernel32.dll")] private static extern bool AllocConsole();
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
    [DllImport("kernel32.dll")] private static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandler? handler, bool add);
    private delegate bool ConsoleCtrlHandler(uint ctrlType);
    private const uint CTRL_CLOSE_EVENT = 2, CTRL_LOGOFF_EVENT = 5, CTRL_SHUTDOWN_EVENT = 6;
    private static ConsoleCtrlHandler? _ctrlHandler;
    private static bool _consoleOpen;

    private static bool OnConsoleCtrl(uint type)
    {
        try
        {
            if (type is CTRL_CLOSE_EVENT or CTRL_LOGOFF_EVENT or CTRL_SHUTDOWN_EVENT)
            {
                LogManager.Log("控制台被关闭 → 先保存播放列表与进度，然后结束程序");
                try
                {
                    AppServices.Config.SaveQueue(AppServices.Player.Queue);
                    AppServices.Config.PlaylistIndex = Math.Max(0, AppServices.Player.Index);
                }
                catch { }
            }
        }
        catch { }
        return false;   // 交给系统默认处理（进程随之结束）
    }

    public static void ShowConsole(bool on)
    {
        try
        {
            if (on)
            {
                if (!_consoleOpen)
                {
                    AllocConsole();
                    try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }   // 中文日志别乱码
                    _ctrlHandler ??= OnConsoleCtrl;
                    SetConsoleCtrlHandler(_ctrlHandler, true);
                    _consoleOpen = true;
                    try { Console.Title = "netHEmusic 实时日志"; } catch { }
                    Console.WriteLine("提示：关闭本窗口会同时结束 netHEmusic（Windows 规则，拦不住）；日志同时全部写入 log.txt");
                }
                LogManager.SetConsoleEnabled(true);   // 会把开启之前的启动日志一并回放
                LogManager.Log("控制台已开启（进程内真控制台，含原生输出）");
            }
            else
            {
                LogManager.Log("控制台已关闭");
                LogManager.SetConsoleEnabled(false);
                if (_consoleOpen) { FreeConsole(); _consoleOpen = false; }
            }
        }
        catch (Exception e) { LogManager.Debug("控制台切换失败: " + e.Message); }
    }
    /// <summary>窗口居中到工作区。</summary>
    public static void Center(Window w, int width = 1280, int height = 720)
    {
        try
        {
            var area = DisplayArea.GetFromWindowId(w.AppWindow.Id, DisplayAreaFallback.Nearest);
            var wa = area.WorkArea;
            w.AppWindow.Move(new PointInt32(wa.X + (wa.Width - width) / 2, wa.Y + (wa.Height - height) / 2));
        }
        catch (Exception e) { LogManager.Debug("居中失败: " + e.Message); }
    }

    // 窗口材质控制器（用控制器而不是简单版 SystemBackdrop：简单版的深浅只跟系统主题走，
    // 应用内切换到浅色配色后标题栏那块材质还是深的）
    private static object? _backdrop;                  // MicaController / DesktopAcrylicController
    private static SystemBackdropConfiguration? _micaCfg;

    // ---- 让系统右键菜单（含托盘菜单）跟随应用的深/浅色 ----
    // 文档/社区的做法（Win32 Dark Mode）：uxtheme 的序号导出 SetPreferredAppMode(135) + FlushMenuThemes(136)。
    // 序号导出只能用 EntryPoint="#135" 这种写法调。失败就静默退回系统默认外观。
    [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = true)] private static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#136", SetLastError = true)] private static extern void FlushMenuThemes();
    private static int _lastAppMode = -1;

    /// <summary>0=Default 1=AllowDark 2=ForceDark 3=ForceLight（跟随应用配色，而不是系统主题）。</summary>
    public static void SyncSystemMenuTheme(bool dark)
    {
        try
        {
            int mode = dark ? 2 : 3;
            if (mode == _lastAppMode) return;
            SetPreferredAppMode(mode);
            FlushMenuThemes();          // 让已经建好的菜单也刷新（切配色后立刻生效）
            _lastAppMode = mode;
            LogManager.Debug("系统菜单主题: " + (dark ? "深色" : "浅色"));
        }
        catch (Exception e) { LogManager.Debug("系统菜单主题设置失败: " + e.Message); }
    }

    /// <summary>应用窗口材质。material = "acrylic"（亚克力：能透看后方其它窗口）/ "micaAlt"（Mica Alt：只取桌面壁纸）。
    /// dark 用应用自己的深浅色传入：启动时元素还没进可视树，ActualTheme 报的是系统主题。</summary>
    public static void ApplyBackdrop(Window w, bool enable, bool dark, string material = "acrylic")
    {
        try
        {
            DetachBackdrop();
            if (!enable) { w.SystemBackdrop = null; LogManager.Log("背景材质: 关闭（实色）"); return; }
            w.SystemBackdrop = null;
            var support = w.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>();
            _micaCfg = new SystemBackdropConfiguration
            {
                IsInputActive = true,
                Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light
            };
            if ((material ?? "").Equals("acrylic", StringComparison.OrdinalIgnoreCase)
                || (material ?? "").Equals("liquid", StringComparison.OrdinalIgnoreCase))   // 液态玻璃：底层仍是亚克力（网页端再叠折射/高光）
            {
                if (!DesktopAcrylicController.IsSupported()) { LogManager.Log("背景材质: 系统不支持亚克力"); return; }
                var c = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Thin };
                c.AddSystemBackdropTarget(support);
                c.SetSystemBackdropConfiguration(_micaCfg);
                _backdrop = c;
                LogManager.Log("背景材质: " + ((material ?? "").Equals("liquid", StringComparison.OrdinalIgnoreCase) ? "液态玻璃（亚克力底）" : "亚克力") + " DesktopAcrylic(Thin) 已接管（深浅=" + (dark ? "Dark" : "Light") + "）");
            }
            else
            {
                if (!MicaController.IsSupported()) { LogManager.Log("背景材质: 系统不支持 Mica"); return; }
                var c = new MicaController { Kind = MicaKind.BaseAlt };
                c.AddSystemBackdropTarget(support);
                c.SetSystemBackdropConfiguration(_micaCfg);
                _backdrop = c;
                LogManager.Log("背景材质: Mica Alt 已接管（深浅=" + (dark ? "Dark" : "Light") + "）");
            }
            SyncBackdropTheme(dark);
        }
        catch (Exception e)
        {
            LogManager.Debug("背景应用失败: " + e.Message);
            try { w.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt }; } catch { }
        }
    }

    /// <summary>让材质的深浅/着色跟着应用配色走（切配色/切深浅时调用）。</summary>
    public static void SyncBackdropTheme(bool dark)
    {
        try
        {
            if (_micaCfg is null || _backdrop is null) return;
            var want = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
            if (_micaCfg.Theme != want) { _micaCfg.Theme = want; LogManager.Debug("材质主题同步: " + want); }
            // 材质用【配色自己的 bg】着色（页面底色那一档），面板是 bg-darken 那一档 ——
            // 这样标题栏材质和页面融合、面板又能从材质上"浮"出来，层次不会拉平
            var scheme = AppServices.Theme.GetScheme();
            var c = ThemeManager.HexToColor(scheme.bg);
            var wc = global::Windows.UI.Color.FromArgb(255, c.R, c.G, c.B);
            switch (_backdrop)
            {
                case MicaController m:
                    m.TintColor = wc; m.TintOpacity = 0.45f; m.FallbackColor = wc;
                    m.LuminosityOpacity = dark ? 0.0f : 1.0f;
                    break;
                case DesktopAcrylicController a:
                    a.TintColor = wc; a.TintOpacity = 0.30f; a.FallbackColor = wc;
                    a.LuminosityOpacity = dark ? 0.0f : 1.0f;
                    break;
            }
        }
        catch (Exception e) { LogManager.Debug("材质主题同步失败: " + e.Message); }
    }

    private static void DetachBackdrop()
    {
        try { (_backdrop as IDisposable)?.Dispose(); } catch { }
        _backdrop = null; _micaCfg = null;
    }
}
