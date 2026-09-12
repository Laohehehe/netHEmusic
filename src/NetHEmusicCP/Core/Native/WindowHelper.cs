using System;
using System.IO;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using netHEmusic.Core.Logging;

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

    /// <summary>应用 Mica 背景（禁用时回退为实色背景）。</summary>
    public static void ApplyBackdrop(Window w, bool enableMica)
    {
        try
        {
            if (enableMica) w.SystemBackdrop = new MicaBackdrop();
            else w.SystemBackdrop = null;
        }
        catch (Exception e) { LogManager.Debug("背景应用失败: " + e.Message); }
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
