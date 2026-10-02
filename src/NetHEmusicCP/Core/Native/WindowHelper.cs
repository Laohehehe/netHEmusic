using System;
using System.IO;
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
            if ((material ?? "").Equals("acrylic", StringComparison.OrdinalIgnoreCase))
            {
                if (!DesktopAcrylicController.IsSupported()) { LogManager.Log("背景材质: 系统不支持亚克力"); return; }
                var c = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Thin };
                c.AddSystemBackdropTarget(support);
                c.SetSystemBackdropConfiguration(_micaCfg);
                _backdrop = c;
                LogManager.Log("背景材质: 亚克力 DesktopAcrylic(Thin) 已接管（深浅=" + (dark ? "Dark" : "Light") + "）");
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
