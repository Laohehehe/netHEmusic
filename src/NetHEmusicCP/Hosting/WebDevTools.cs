using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using netHEmusic.Core;
using netHEmusic.Shell;
using netHEmusic.Core.Api;
using netHEmusic.Core.Download;
using netHEmusic.Core.Logging;
using netHEmusic.Core.Model;
using netHEmusic.Platform;
using netHEmusic.Core.Playback;
using netHEmusic.Core.Theme;
using netHEmusic.Core.Update;
using netHEmusic.Windows;

namespace netHEmusic;

/// <summary>MainWindow 的部分实现（拆分自 MainWindow.xaml.cs，纯搬移，行为不变）。</summary>
public sealed partial class MainWindow
{
    // ---------------- F12 开发者工具 ----------------
    //
    // 为什么不用 WebView2 自带的 F12：CoreWebView2Settings.AreDevToolsEnabled 在运行期改了
    // 要重启才生效（用户反馈的「开发者工具要重启软件才能按 F12 打开」就是它），而且 WinUI3 的
    // WebView2 控件拿不到 CoreWebView2Controller，用不了 AcceleratorKeyPressed。
    // 所以：设置里那个开关**始终**开着 AreDevToolsEnabled（保证 OpenDevToolsWindow 随时可用），
    // 真正的开/关在下面这个挂着 WebView 子窗口上的 WndProc 里现场判断 —— 一拨就生效。
    private IntPtr _webHwnd = IntPtr.Zero, _webOldProc = IntPtr.Zero;
    private WndProcDelegate? _webProc;
    private delegate IntPtr WndProcDelegate(IntPtr h, uint m, IntPtr w, IntPtr l);

    private const uint WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    private const int VK_F12 = 0x7B;

    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int idx, IntPtr v);
    [DllImport("user32.dll")] private static extern IntPtr CallWindowProc(IntPtr prev, IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc cb, IntPtr p);
    private delegate bool EnumChildProc(IntPtr h, IntPtr p);

    /// <summary>
    /// 把 WebView2 的子窗口（Chrome_WidgetWin_*）子类化，好接管 F12。
    /// 键盘消息是发给那个子窗口的，不是主窗口，所以只能挂在它身上。
    /// </summary>
    private void HookWebViewKeys()
    {
        try
        {
            var main = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (main == IntPtr.Zero) return;
            IntPtr found = IntPtr.Zero;
            EnumChildWindows(main, (h, p) =>
            {
                var sb = new System.Text.StringBuilder(256);
                GetClassName(h, sb, 256);
                if (sb.ToString().StartsWith("Chrome_WidgetWin", StringComparison.Ordinal)) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            if (found == IntPtr.Zero || found == _webHwnd) return;
            _webHwnd = found;
            _webProc = WebViewKeyProc;
            _webOldProc = SetWindowLongPtr(found, -4, Marshal.GetFunctionPointerForDelegate(_webProc));
            LogManager.Debug("F12 拦截已挂上 WebView 子窗口 " + found);
        }
        catch (Exception e) { LogManager.Debug("挂 F12 拦截失败: " + e.Message); }
    }

    private IntPtr WebViewKeyProc(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        try
        {
            if ((m == WM_KEYDOWN || m == WM_SYSKEYDOWN) && w.ToInt64() == VK_F12)
            {
                if (DevToolsOn())
                {
                    try { WebView.CoreWebView2?.OpenDevToolsWindow(); LogManager.Log("开发者工具: 已打开（F12）"); } catch (Exception e) { LogManager.Debug("打开开发者工具失败: " + e.Message); }
                }
                else LogManager.Debug("开发者工具: 开关是关的，F12 已忽略");
                return IntPtr.Zero;   // 吞掉：开不开都由这里说了算
            }
        }
        catch { }
        return CallWindowProc(_webOldProc, h, m, w, l);
    }

    /// <summary>开发者工具开关（设置里可开，默认关）。</summary>
    private static bool DevToolsOn()
        => !AppServices.Config.Get("App", "ui_devtools", "false").Equals("false", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 给 WebView2 打底：AreDevToolsEnabled 一律置 true（保证 OpenDevToolsWindow 随时能用）。
    /// 「开不开」由 WebViewKeyProc 现场读 DevToolsOn() 决定，所以改完立刻生效，不用重启。
    /// </summary>
    private void ApplyDevTools(CoreWebView2? core = null)
    {
        try
        {
            core ??= WebView.CoreWebView2;
            if (core is null) return;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.AreBrowserAcceleratorKeysEnabled = true;    // 其它浏览器快捷键（F5 / Ctrl+F…）照旧可用
            LogManager.Log("开发者工具: " + (DevToolsOn() ? "已启用（F12 打开）" : "已关闭"));
        }
        catch (Exception e) { LogManager.Debug("设置开发者工具失败: " + e.Message); }
    }
}