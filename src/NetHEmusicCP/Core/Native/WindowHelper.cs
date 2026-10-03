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

/// <summary>WinUI3 窗口辅助：图标、16:9 尺寸与居中、Mica 背景、自定义标题栏。</summary>
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

    // ---- 无原生标题栏时的窗口拖动 / 最大化（网页发起：win_drag / win_drag_move / win_max / win_min） ----
    // 说明：把按下转成 WM_NCLBUTTONDOWN(HTCAPTION) 让系统接管拖动的做法，在 WebView2 里
    // 因为跨进程消息要绕一圈、系统拖动循环启动时鼠标已经抬起，实测窗口纹丝不动；
    // 所以改成网页每次 mousemove 通知一次，这里按【原生光标位移】移动窗口（两边都用物理坐标，不混 DPI）。
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr hinst, string name, uint type, int cx, int cy, uint load);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x0010;
    private const int WM_SETICON = 0x0080, ICON_SMALL = 0, ICON_BIG = 1;
    private static POINT _dragStart; private static PointInt32 _winStart; private static bool _dragging;

    /// <summary>网页拖拽带按下：记录光标与窗口起点（最大化时先还原，符合系统习惯）。</summary>
    public static void StartDrag(Window w)
    {
        try
        {
            if (w.AppWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Maximized) p.Restore();
            ReleaseCapture();
            GetCursorPos(out _dragStart);
            _winStart = w.AppWindow.Position;
            _dragging = true;
        }
        catch (Exception e) { LogManager.Debug("拖动起点失败: " + e.Message); }
    }

    /// <summary>网页拖拽带移动：把光标位移原样加到窗口起点上。</summary>
    public static void DragMove(Window w)
    {
        if (!_dragging) return;
        try
        {
            if (!GetCursorPos(out var now)) return;
            w.AppWindow.Move(new PointInt32(_winStart.X + (now.X - _dragStart.X), _winStart.Y + (now.Y - _dragStart.Y)));
        }
        catch (Exception e) { LogManager.Debug("拖动失败: " + e.Message); }
    }

    public static void EndDrag() => _dragging = false;

    // ---- 实时日志控制台（设置→高级 里的「显示控制台」）----
    // WinUI 是 GUI 子系统程序，默认没有控制台，Console.WriteLine 全丢（这也是 -debugger 看不到日志的原因）。
    // 这里按需 AllocConsole 出一个真控制台，再把 LogManager 的实时输出接上去。
    [DllImport("kernel32.dll")] private static extern bool AllocConsole();
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
    [DllImport("kernel32.dll")] private static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandler? handler, bool add);
    private delegate bool ConsoleCtrlHandler(uint ctrlType);
    private const uint CTRL_CLOSE_EVENT = 2;
    private static ConsoleCtrlHandler? _ctrlHandler;
    private static bool _consoleOpen;

    public static void ShowConsole(bool on)
    {
        try
        {
            if (on)
            {
                if (!_consoleOpen)
                {
                    AllocConsole();
                    try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }   // 否则中文日志是乱码
                    _ctrlHandler ??= t => t == CTRL_CLOSE_EVENT;   // 关掉黑框别把播放器一起带走
                    SetConsoleCtrlHandler(_ctrlHandler, true);
                    _consoleOpen = true;
                    try { Console.Title = "netHEmusic 实时日志"; } catch { }
                }
                LogManager.SetConsoleEnabled(true);
                LogManager.Log("控制台已开启（关掉这个黑框不会退出软件）");
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

    public static void ToggleMaximize(Window w)
    {
        try
        {
            if (w.AppWindow.Presenter is not OverlappedPresenter p) return;
            if (p.State == OverlappedPresenterState.Maximized) p.Restore(); else p.Maximize();
        }
        catch (Exception e) { LogManager.Debug("最大化失败: " + e.Message); }
    }

    public static void Minimize(Window w)
    {
        try { (w.AppWindow.Presenter as OverlappedPresenter)?.Minimize(); }
        catch (Exception e) { LogManager.Debug("最小化失败: " + e.Message); }
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

    /// <summary>启用自定义标题栏：内容扩展到标题栏区域并设置拖拽区。返回可用于拖拽的标题栏根元素。</summary>
    public static void SetupTitleBar(Window w)
    {
        try
        {
            w.ExtendsContentIntoTitleBar = true;
            w.SetTitleBar(w.Content as UIElement);
        }
        catch (Exception e) { LogManager.Debug("标题栏设置失败: " + e.Message); }
    }
}
