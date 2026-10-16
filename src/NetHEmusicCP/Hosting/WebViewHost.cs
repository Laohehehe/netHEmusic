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
        catch (Exception e) { LogManager.Error("WebView2 初始化失败: " + e.Message); }
    }

    private string FindWebFolder()
    {
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
}