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
    // ============ WebView2 + 桥 ============
    private async void InitWebView()
    {
        try
        {
            // 允许网页 <audio> 在无用户手势时也能播放（前端播放内核需要：自动下一首 / SMTC 触发）
            // 同时阻止 WebView2/Chromium 自己注册媒体会话并抢媒体键：否则系统媒体浮出控件里会出现
            // 一条 msedgewebview2.exe 的重复条目（标题=网页 title），"当前会话"归属还会在两条之间乱跳。
            // SMTC 统一由 C# 侧（PlayerService）提供元数据与控制。
            var browserArgs = "--autoplay-policy=no-user-gesture-required --disable-features=MediaSessionService,HardwareMediaKeyHandling";
            // 设置 → 性能 → GPU 加速：关掉后走软件渲染（省 GPU / 兼容老显卡，需重启生效）
            bool gpuOn = !AppServices.Config.Get("App", "perf_gpu", "true").Equals("false", StringComparison.OrdinalIgnoreCase);
            if (!gpuOn)
            {
                browserArgs += " --disable-gpu --disable-gpu-compositing --disable-accelerated-2d-canvas --disable-accelerated-video-decode";
                LogManager.Log("GPU 加速已关闭（软件渲染）");
            }
#if DEBUG
            // 仅 Debug 构建：设了 NETHE_DEV_CDP=1 才开远程调试端口，供自动化测试（读 DOM / 按选择器点击 / 只截页面）。
            // Release 构建这段整个被编译掉，发布的 EXE 里没有任何调试代码。
            try
            {
                if (Environment.GetEnvironmentVariable("NETHE_DEV_CDP") == "1")
                {
                    browserArgs += " --remote-debugging-port=9222";
                    LogManager.Log("[DEV] 远程调试端口已开启: 9222");
                }
            }
            catch { }
#endif
            var envOptions = new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = browserArgs
            };
            // 用户数据放到 %APPDATA%\netHEmusic\webview：安装目录保持只读，MSI 卸载才干净
            string webviewData = Path.Combine(AppServices.Config.DataDir, "webview");
            try { Directory.CreateDirectory(webviewData); } catch { }
            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateWithOptionsAsync(null, webviewData, envOptions);
            await WebView.EnsureCoreWebView2Async(env);
            // 页面自己绘制所有面板；WebView 背景透明，窗口材质（Mica Alt）才能从面板后面透出来
            try { WebView.DefaultBackgroundColor = global::Windows.UI.Color.FromArgb(0, 0, 0, 0); } catch (Exception wb) { LogManager.Debug("WebView 透明背景失败: " + wb.Message); }
            var core = WebView.CoreWebView2;
            var webFolder = FindWebFolder();
            core.SetVirtualHostNameToFolderMapping("appassets", webFolder, CoreWebView2HostResourceAccessKind.Allow);
            // 离线播放：本地音频不走「目录映射」（实测 SetVirtualHostNameToFolderMapping 那个 host 解析不了，
            // 报"不知道这样的主机"），改成自己拦请求回文件 —— 可控、能记日志、能自己定 Content-Type。
            try
            {
                var audioDir = Path.GetFullPath(AppServices.Cache.AudioDir);
                core.AddWebResourceRequestedFilter("https://mediacache/*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (s, e) =>
                {
                    try
                    {
                        var name = Path.GetFileName(new Uri(e.Request.Uri).LocalPath);
                        var file = Path.Combine(audioDir, name);
                        if (!string.IsNullOrEmpty(name) && File.Exists(file) && file.StartsWith(audioDir, StringComparison.OrdinalIgnoreCase))
                        {
                            var ext = Path.GetExtension(file).ToLowerInvariant();
                            var mime = ext switch { ".flac" => "audio/flac", ".mp3" => "audio/mpeg", ".m4a" or ".mp4" => "audio/mp4", ".wav" => "audio/wav", ".ogg" => "audio/ogg", _ => "application/octet-stream" };
                            long total = new FileInfo(file).Length;
                            // 注意：整份文件直接回（200 + Content-Length），不切 Range 段 ——
                            // AsRandomAccessStream() 要求流可寻址（CanSeek=true），自定义裁段流会让它抛
                            // “does not support seeking”，播放器拿到空响应就报 Format error。本地文件整份给最稳，
                            // 媒体管线自己会缓存/定位。Range 请求也回 200 全量（Chromium 接受，仅少了省流意义）。
                            var fs = File.OpenRead(file);
                            var headers = "Content-Type: " + mime + "\r\nContent-Length: " + total +
                                          "\r\nAccept-Ranges: bytes\r\nCache-Control: no-store\r\nAccess-Control-Allow-Origin: *";
                            e.Response = core.Environment.CreateWebResourceResponse(fs.AsRandomAccessStream(), 200, "OK", headers);
                            LogManager.Debug("本地音频响应: " + name + " (" + total / 1024 / 1024 + " MB, " + mime + ")");
                        }
                        else
                        {
                            e.Response = core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
                            LogManager.Debug("本地音频缺失: " + name);
                        }
                    }
                    catch (Exception ex2) { LogManager.Debug("本地音频响应失败: " + ex2.Message); }
                };
                LogManager.Log("离线音频通道就绪（拦截 https://mediacache/* → " + audioDir + "）");
            }
            catch (Exception mv) { LogManager.Debug("离线音频通道失败: " + mv.Message); }
            // 关闭 WebView2 缓存，保证热重载后拿到最新的 HTML/CSS/JS
            try { await core.CallDevToolsProtocolMethodAsync("Network.setCacheDisabled", "{\"cacheDisabled\":true}"); } catch (Exception ce) { LogManager.Debug("禁用缓存失败: " + ce.Message); }
            SetupHotReload(webFolder);
            ApplyDevTools(core);                       // 按设置给 WebView2 打底（真正的开关在下面 F12 拦截里）
            HookWebViewKeys();                        // F12 自己拦（见下）
            core.WebMessageReceived += OnWebMessage;
            core.NavigationCompleted += (s, e) =>
            {
                AppServices.Player.ResetFrontendAudio();   // 网页重载后 <audio> 是空的
                PushTheme(); PushLang(); PostToWeb(new { type = "nav", view = "home" }); CheckVersionNotice();
                // 离线状态由 C# 侧持有：页面重载/切回来后要重新下发，否则"未缓存置灰"会消失
                PostToWeb(new { type = "net_state", offline = netHEmusic.Core.Playback.PlayerService.Offline });
                HookWebViewKeys();                         // 导航后 WebView 可能换了子窗口，补挂一次
            };
            WebView.Source = new Uri("https://appassets/index.html");
            LogManager.Log("WebView2 已加载: " + webFolder);
        }
        catch (Exception e)
        {
            LogManager.Error("WebView2 初始化失败: " + e.Message + Environment.NewLine + e);
            ShowWebViewStartupError(e);
        }
    }

    // 「即用即丢」：网页切走一个页面后，让渲染进程把内部缓存（布局/绘制/图片解码）主动收缩。
    // MemoryUsageTargetLevel=Low 只是给渲染进程一个“可以省内存”的信号，收缩后立刻恢复 High，
    // 避免长期 Low 拖慢动画；1.2s 后自动回 High。
    private void TrimRendererMemory()
    {
        try
        {
            var core = WebView?.CoreWebView2; if (core == null) return;
            core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
            var dq = WebView.DispatcherQueue;
            dq?.TryEnqueue(async () =>
            {
                try
                {
                    await Task.Delay(1200);
                    if (WebView?.CoreWebView2 != null)
                        WebView.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
                }
                catch { }
            });
            LogManager.Debug("渲染进程内存收缩(Low → 1.2s 后 High)");
        }
        catch (Exception ex) { LogManager.Debug("渲染进程内存收缩失败: " + ex.Message); }
    }

    private string FindWebFolder()    {
        // 开发期优先使用“源码 Web 目录”：改 HTML/CSS/JS 后无需重新构建，热重载即可看到效果
        var root = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && !string.IsNullOrEmpty(root); i++)
        {
            var src = Path.Combine(Path.GetFullPath(root), "src", "NetHEmusicCP", "Web");
            if (Directory.Exists(src)) return src;
            root = Path.GetDirectoryName(root);
        }
        return Path.Combine(AppContext.BaseDirectory, "Web");
    }

    // ---- Web 热重载：监听 Web 目录改动并自动刷新 WebView2 ----
    private FileSystemWatcher? _webWatcher;
    private DateTime _lastReload = DateTime.MinValue;
    private void SetupHotReload(string webFolder)
    {
        try
        {
            _webWatcher = new FileSystemWatcher(webFolder) { IncludeSubdirectories = true, EnableRaisingEvents = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            FileSystemEventHandler h = (s, e) => DebouncedReload();
            _webWatcher.Changed += h; _webWatcher.Created += h; _webWatcher.Deleted += h;
            _webWatcher.Renamed += (s, e) => DebouncedReload();
            LogManager.Log("Web 热重载已启用: " + webFolder);
        }
        catch (Exception e) { LogManager.Debug("热重载不可用: " + e.Message); }
    }
    private void DebouncedReload()
    {
        var now = DateTime.Now;
        if ((now - _lastReload).TotalMilliseconds < 400) return;
        _lastReload = now;
        AppServices.RunOnUi(() =>
        {
            try
            {
                // 热重载前先关掉缓存，否则 WebView2 会用旧的 css/js（改了没反应的元凶）
                _ = WebView.CoreWebView2?.CallDevToolsProtocolMethodAsync("Network.setCacheDisabled", "{\"cacheDisabled\":true}");
                WebView.CoreWebView2?.Reload();
                LogManager.Log("Web 热重载完成");
            }
            catch { }
        });
    }

    // ============ WebView2 初始化失败的可见兜底 ============
    // 页面是窗口唯一的绘制者，WebView2 起不来时窗口只剩材质底（表现成"半透明窗口"/"什么都没有"）。
    // 以前这里只写日志，用户完全不知道发生了什么；现在把原因、重试、装运行时、开日志都摆到界面上。

    private void ShowWebViewStartupError(Exception e)
    {
        AppServices.RunOnUi(() =>
        {
            try
            {
                bool missingRuntime = IsWebView2RuntimeLikelyMissing(e);
                StartupErrorTitle.Text = missingRuntime ? "缺少 WebView2 运行时" : "界面组件加载失败";
                StartupErrorDetail.Text =
                    (missingRuntime
                        ? "本机没有安装 Microsoft Edge WebView2 Runtime，程序的界面无法显示。\n" +
                          "点“下载 WebView2 运行时”装好后再点“重试”即可。"
                        : "WebView2 初始化失败，程序的界面无法显示。") +
                    "\n\n错误信息：" + e.Message +
                    "\n数据目录：" + AppServices.DataDirHint +
                    "\n日志文件：" + (string.IsNullOrEmpty(LogManager.LogDirectory) ? "（未初始化）" : LogManager.LogFilePath);
                StartupErrorFixBtn.Visibility = missingRuntime ? Visibility.Visible : Visibility.Collapsed;
                StartupErrorHint.Text = LogManager.UsingFallback
                    ? "注意：正常日志目录写不进去，本次日志已写到 " + LogManager.LogDirectory + "（末尾有 [WARN] 行说明原因）"
                    : "";
                StartupErrorPanel.Visibility = Visibility.Visible;
                LogManager.Log("[兜底] 已显示启动失败面板: " + StartupErrorTitle.Text);
            }
            catch (Exception ex) { LogManager.Debug("显示启动失败面板失败: " + ex.Message); }
        });
    }

    /// <summary>判断是不是"没装 WebView2 运行时"：异常文案 + Evergreen 安装目录是否存在。</summary>
    private static bool IsWebView2RuntimeLikelyMissing(Exception e)
    {
        try
        {
            var msg = e.Message ?? "";
            if (msg.Contains("runtime", StringComparison.OrdinalIgnoreCase) &&
                (msg.Contains("not found", StringComparison.OrdinalIgnoreCase) || msg.Contains("找不到") || msg.Contains("未安装"))) return true;
            if (e.GetType().Name.Contains("RuntimeNotFound", StringComparison.OrdinalIgnoreCase)) return true;
            var all = e.ToString();
            if (all.Contains("WebView2RuntimeNotFoundException", StringComparison.OrdinalIgnoreCase)) return true;
            // 显式指定了浏览器目录（测试/绿色版）：以它的存在与否为准
            var explicitDir = Environment.GetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER");
            if (!string.IsNullOrEmpty(explicitDir)) return !Directory.Exists(explicitDir) || !Directory.EnumerateFileSystemEntries(explicitDir).Any();
            foreach (var root in new[]
                     {
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "EdgeWebView", "Application"),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "EdgeWebView", "Application")
                     })
            {
                if (Directory.Exists(root) && Directory.EnumerateDirectories(root).Any()) return false;
            }
            return true;
        }
        catch { return false; }
    }

    private void OnStartupErrorRetry(object sender, RoutedEventArgs e)
    {
        LogManager.Log("用户点击重试：重新初始化 WebView2");
        StartupErrorPanel.Visibility = Visibility.Collapsed;
        InitWebView();
    }

    private void OnStartupErrorFix(object sender, RoutedEventArgs e)
    {
        // WebView2 Evergreen 引导安装器（微软官方短链，约 2MB，装完自动跟随 Edge 更新）
        OpenExternal("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
    }

    private void OnStartupErrorOpenLog(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = LogManager.LogDirectory;
            if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir)) OpenExternal(dir);
            else OpenExternal(AppServices.DataDirHint);
        }
        catch (Exception ex) { LogManager.Debug("打开日志目录失败: " + ex.Message); }
    }

    private static void OpenExternal(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { LogManager.Debug("打开链接/目录失败: " + ex.Message); }
    }
}