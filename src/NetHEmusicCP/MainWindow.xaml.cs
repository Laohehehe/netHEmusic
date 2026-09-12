using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using netHEmusic.Core;
using netHEmusic.Core.Api;
using netHEmusic.Core.Download;
using netHEmusic.Core.Logging;
using netHEmusic.Core.Model;
using netHEmusic.Core.Native;
using netHEmusic.Core.Playback;
using netHEmusic.Core.Theme;
using netHEmusic.Core.Update;
using netHEmusic.Windows;

namespace netHEmusic;

/// <summary>
/// 主窗口：无边框 Chrome（logo/主题/窗口控制）+ WebView2 完整 YesPlayMusic 风格播放器。
/// WebView2 前端与 C# 壳通过 WebMessage 通信；播放/进度由 C# 推送前端；通用网易云 API 透传。
/// </summary>
public sealed partial class MainWindow : Window
{
    private bool _seeking = false;

    /// <summary>应用原生配色（根背景 + 无边框标题栏），保证标题栏与 #main/#sidebar 同色。</summary>
    private void ApplyNativeTheme()
    {
        AppServices.Theme.Apply(Root);
        try
        {
            bool dark = AppServices.Theme.IsDark;
            // ThemeResource 在运行期不会因资源字典里换对象而重新解析，这里直接给元素换刷子，标题栏才会跟着配色走
            Root.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(AppServices.Theme.SurfaceColor(dark));
            Headbar.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(AppServices.Theme.SurfaceDarken(dark));
        }
        catch (Exception e) { LogManager.Debug("原生配色失败: " + e.Message); }
    }

    public MainWindow()
    {
        InitializeComponent();
        AppServices.Ui = DispatcherQueue;
        Title = "netHEmusic";
        ApplyNativeTheme();
        WindowHelper.SetIcon(this);
        WindowHelper.Resize169(this, 1280, 720);
        WindowHelper.Center(this, 1280, 720);
        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(Headbar);
            AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        }
        catch (Exception e) { LogManager.Debug("标题栏: " + e.Message); }
        WindowHelper.ApplyBackdrop(this, AppServices.Config.Mica);

        AppServices.OnThemeApplied = () => { try { ApplyNativeTheme(); } catch { } PushTheme(); };
        InitWebView();

        // 把播放状态推给 Web 前端播放条
        AppServices.Player.SongChanged += s => { if (s is not null) AppServices.RunOnUi(() => PostToWeb(new { type = "playing", song = ToSongDto(s) })); };
        AppServices.Player.PositionChanged += pos => AppServices.RunOnUi(() => { var c = AppServices.Player.Current; PostToWeb(new { type = "position", pos = (long)pos.TotalMilliseconds, dur = c is null ? 0 : c.Duration }); });
        AppServices.Download.Completed += it => AppServices.RunOnUi(() => PostToWeb(new { type = "toast", text = "下载完成: " + it.Display }));
        AppServices.Download.Failed += it => AppServices.RunOnUi(() => PostToWeb(new { type = "toast", text = "下载失败: " + (it.Error ?? "") }));

        LogManager.Log("MainWindow 构造完成");
        _ = StartupFlowAsync();
    }

    // ============ 启动流程：用户协议 -> 更新检测 -> 主界面 ============
    private async Task StartupFlowAsync()
    {
        try
        {
            if (!await EnsureAgreementAsync()) { App.ExitApp(); return; }
            var check = await AppServices.Updater.Check();
            if (check.HasUpdate)
            {
                var upd = new UpdateWindow(check);
                upd.Activate();
                var choice = await upd.WaitAsync();
                if (choice == UpdateChoice.Update)
                {
                    AppServices.Updater.StartDownload(check.Latest, check.DownloadUrl, check.Sha256, check.AssetName);
                    if (await upd.RunDownloadAsync()) { if (AppServices.Updater.ApplyUpdate(out _)) { App.ExitApp(); return; } }
                }
                else { App.ExitApp(); return; }
            }
        }
        catch (Exception e) { LogManager.Error("启动流程异常: " + e); }
    }
    private async Task<bool> EnsureAgreementAsync()
    {
        if (!AppServices.Config.FirstRun) return true;
        var win = new UserLicenseWindow();
        win.Activate();
        return await win.WaitAsync();
    }

    // ============ WebView2 + 桥 ============
    private async void InitWebView()
    {
        try
        {
            await WebView.EnsureCoreWebView2Async();
            var core = WebView.CoreWebView2;
            var webFolder = FindWebFolder();
            core.SetVirtualHostNameToFolderMapping("appassets", webFolder, CoreWebView2HostResourceAccessKind.Allow);
            // 关闭 WebView2 缓存，保证热重载后拿到最新的 HTML/CSS/JS
            try { await core.CallDevToolsProtocolMethodAsync("Network.setCacheDisabled", "{\"cacheDisabled\":true}"); } catch (Exception ce) { LogManager.Debug("禁用缓存失败: " + ce.Message); }
            SetupHotReload(webFolder);
            core.WebMessageReceived += OnWebMessage;
            core.NavigationCompleted += (s, e) => { PushTheme(); PostToWeb(new { type = "nav", view = "home" }); };
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
        AppServices.RunOnUi(() => { try { WebView.CoreWebView2?.Reload(); LogManager.Log("Web 热重载完成"); } catch { } });
    }
    private void PostToWeb(object msg) { try { WebView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(msg)); } catch { } }
    private void PushTheme()
    {
        var (mode, pr, se, bg, dk) = AppServices.Theme.GetScheme();
        PostToWeb(new { type = "theme", dark = mode == "dark", vars = ThemeManager.ToCssVars(mode, pr, se, bg, dk) });
    }
    private object ToSongDto(Song s) => new { s.Id, Title = s.Title, Artist = s.ArtistsName, Album = s.AlbumName, Pic = s.PicUrl, Duration = s.Duration };

    private Song? SongFromWeb(JsonElement s)
    {
        try { return new Song { Id = s.TryGetProperty("Id", out var id) ? id.GetInt64() : 0, Title = s.TryGetProperty("Title", out var t) ? t.GetString() ?? "" : "", Artists = new List<Artist> { new Artist { Name = s.TryGetProperty("Artist", out var a) ? a.GetString() ?? "" : "" } }, Album = new Album { Name = s.TryGetProperty("Album", out var al) ? al.GetString() ?? "" : "", PicUrl = s.TryGetProperty("Pic", out var p) ? p.GetString() : "" }, Pic = s.TryGetProperty("Pic", out var p2) ? p2.GetString() : "" }; } catch { return null; }
    }

    private void OnWebMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var doc = JsonDocument.Parse(e.WebMessageAsJson).RootElement;
            var type = doc.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            LogManager.Debug("web msg: " + type);
            switch (type)
            {
                case "search": _ = HandleWebSearch(doc); break;
                case "discover": _ = HandleWebDiscover(); break;
                case "playlist": _ = HandleWebPlaylist(doc); break;
                case "lyric": _ = HandleWebLyric(doc); break;
                case "play": HandleWebPlay(doc); break;
                case "play_list": HandleWebPlayList(doc); break;
                case "download": HandleWebDownload(doc); break;
                case "login": break; // 登录由 Web 前端 via /api 完成
                case "api": _ = HandleWebApi(doc); break;
                case "toggle": _ = AppServices.Player.ToggleAsync(); break;
                case "prev": _ = AppServices.Player.PrevAsync(); break;
                case "next": _ = AppServices.Player.NextAsync(); break;
                case "volume": { int.TryParse(doc.TryGetProperty("v", out var vv) ? vv.GetRawText() : "", out var n); AppServices.Player.SetVolume(n); break; }
                case "save_cookie": { var cookie = doc.TryGetProperty("cookie", out var ck) ? ck.GetString() ?? "" : ""; AppServices.Netease.SetCookie(cookie); AppServices.Config.SaveCookie(cookie); LogManager.Log("登录 cookie 已保存(脱敏)"); break; }
                case "queue_add": { if (doc.TryGetProperty("song", out var s2)) { var song = SongFromWeb(s2); if (song != null) { var q = AppServices.Player.Queue.ToList(); if (q.All(x => x.Id != song.Id)) { q.Add(song); _ = AppServices.Player.LoadQueueAsync(q, AppServices.Player.Index < 0 ? 0 : AppServices.Player.Index); } } } break; }
                case "playnext": { if (doc.TryGetProperty("song", out var s)) { var song = SongFromWeb(s); if (song != null) { var q = AppServices.Player.Queue.ToList(); int idx = q.FindIndex(x => x.Id == song.Id); if (idx < 0) { var nxt = AppServices.Player.Index + 1; q.Insert(Math.Min(nxt, q.Count), song); _ = AppServices.Player.LoadQueueAsync(q, AppServices.Player.Index); } } } break; }
                case "open_settings": AppServices.RunOnUi(() => { try { new SettingsWindow().Activate(); } catch (Exception ex) { LogManager.Error("打开设置失败: " + ex.Message); } }); break;
                case "get_settings": HandleGetSettings(); break;
                case "set_setting": HandleSetSetting(doc); break;
                case "log": LogManager.Info("web: " + (doc.TryGetProperty("msg", out var m) ? m.GetString() : "")); break;
            }
        }
        catch (Exception ex) { LogManager.Debug("Web 消息失败: " + ex.Message); }
    }

    private async Task HandleWebSearch(JsonElement doc) { var kw = doc.TryGetProperty("kw", out var k) ? k.GetString() ?? "" : ""; var songs = await AppServices.Netease.Search(kw, 1, 50); PostToWeb(new { type = "songs", songs = songs.Select(ToSongDto).ToList(), search = kw }); }
    private async Task HandleWebDiscover() { var songs = await AppServices.Netease.RecommendSongs(); PostToWeb(new { type = "songs", songs = songs.Select(ToSongDto).ToList(), discover = true }); }
    private async Task HandleWebPlaylist(JsonElement doc) { long id = 0; if (doc.TryGetProperty("id", out var i)) id = i.GetInt64(); if (id <= 0) return; var tracks = await AppServices.Netease.PlaylistTracks(id, 1000, 0); PostToWeb(new { type = "songs", songs = tracks.Select(ToSongDto).ToList() }); }
    private async Task HandleWebLyric(JsonElement doc) { long id = 0; if (doc.TryGetProperty("id", out var d)) id = d.GetInt64(); if (id <= 0 && AppServices.Player.Current != null) id = AppServices.Player.Current.Id; var json = await AppServices.Netease.JsonLyric(id); var (lrc, tl, ro) = AppServices.Netease.ParseLyric(json); PostToWeb(new { type = "lyric", lrc, tlyric = tl, romalrc = ro }); }
    private void HandleWebPlay(JsonElement doc)
    {
        if (doc.TryGetProperty("song", out var s))
        {
            var song = new Song { Id = s.TryGetProperty("Id", out var id) ? id.GetInt64() : 0, Title = s.TryGetProperty("Title", out var t) ? t.GetString() ?? "未知歌曲" : "未知歌曲", Artists = new List<Artist> { new Artist { Name = s.TryGetProperty("Artist", out var a) ? a.GetString() ?? "未知" : "未知" } }, Album = new Album { Name = s.TryGetProperty("Album", out var al) ? al.GetString() ?? "" : "", PicUrl = s.TryGetProperty("Pic", out var p) ? p.GetString() : "" }, Pic = s.TryGetProperty("Pic", out var p2) ? p2.GetString() : "" };
            if (song.Id > 0) { var q = AppServices.Player.Queue.ToList(); q.RemoveAll(x => x.Id == song.Id); q.Add(song); _ = AppServices.Player.LoadQueueAsync(q, q.Count - 1); }
        }
        else if (doc.TryGetProperty("id", out var id) && id.GetInt64() > 0) { var q = AppServices.Player.Queue; int idx = q.FindIndex(x => x.Id == id.GetInt64()); if (idx >= 0) _ = AppServices.Player.PlayAtAsync(idx); }
    }
    /// <summary>播放全部：整列表作为播放队列，并从 index 开始播放。</summary>
    private void HandleWebPlayList(JsonElement doc)
    {
        if (!doc.TryGetProperty("songs", out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        var q = new List<Song>();
        foreach (var it in arr.EnumerateArray()) { var s = SongFromWeb(it); if (s is not null && s.Id > 0) q.Add(s); }
        if (q.Count == 0) return;
        int idx = doc.TryGetProperty("index", out var i) && i.TryGetInt32(out var n) ? n : 0;
        if (idx < 0 || idx >= q.Count) idx = 0;
        LogManager.Log("播放全部: " + q.Count + " 首, 起始下标 " + idx);
        _ = AppServices.Player.LoadQueueAsync(q, idx);
    }

    private void HandleWebDownload(JsonElement doc) { if (doc.TryGetProperty("song", out var s)) { var song = new Song { Id = s.TryGetProperty("Id", out var id) ? id.GetInt64() : 0, Title = s.TryGetProperty("Title", out var t) ? t.GetString() ?? "" : "", Artists = new List<Artist> { new Artist { Name = s.TryGetProperty("Artist", out var a) ? a.GetString() ?? "" : "" } }, Album = new Album { Name = s.TryGetProperty("Album", out var al) ? al.GetString() ?? "" : "", PicUrl = s.TryGetProperty("Pic", out var p) ? p.GetString() : "" } }; if (song.Id > 0) _ = AppServices.Download.Download(song, AppServices.Config.DownloadDir, AppServices.Config.Quality); } }

    /// <summary>读取 [App] 段的布尔配置（默认值字符串）。</summary>
    private static bool BoolCfg(string key, string def = "false")
        => AppServices.Config.Get("App", key, def).Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>把当前设置推给前端设置页。</summary>
    private void HandleGetSettings()
    {
        var s = new Dictionary<string, object?>
        {
            ["scheme"] = AppServices.Config.Scheme,
            ["schemes"] = AppServices.Theme.SchemeNames(),
            ["mica"] = AppServices.Config.Mica,
            ["closeToTray"] = AppServices.Config.Get("App", "close_to_tray", "false").Equals("true", StringComparison.OrdinalIgnoreCase),
            // [App] 整段原样回传：前端自定义设置（fx_* / ui_* 等）都在这里，新增设置项无需改 C#
            ["app"] = AppServices.Config.GetSection("App"),
            ["downloadDir"] = AppServices.Config.DownloadDir,
            ["quality"] = AppServices.Config.Quality,
            ["volume"] = AppServices.Config.Volume,
            ["playMode"] = AppServices.Config.Get("Player", "mode", "order"),
            ["proxy"] = AppServices.Config.Proxy,
            ["language"] = AppServices.Config.Language,
            ["desktopLyric"] = AppServices.Config.Get("App", "desktop_lyric", "true").Equals("true", StringComparison.OrdinalIgnoreCase),
            ["desktopLyricTopmost"] = AppServices.Config.DesktopLyricTopmost,
            ["desktopSongInfo"] = AppServices.Config.DesktopSongInfo,
            ["toast"] = AppServices.Config.Get("App", "toast", "true").Equals("true", StringComparison.OrdinalIgnoreCase),
            ["version"] = AppServices.Version,
        };
        PostToWeb(new { type = "settings", data = s });
    }

    /// <summary>前端设置项变更 → 应用并持久化。</summary>
    private void HandleSetSetting(JsonElement doc)
    {
        var key = doc.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
        var value = doc.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
        bool b = value.Equals("true", StringComparison.OrdinalIgnoreCase);
        switch (key)
        {
            case "scheme":
                AppServices.Config.Scheme = value;
                PushTheme(); // 先把新配色变量推给前端 → 由前端做"水波"扩散动画
                // 等水波扫过窗口顶部(约 0.24s)后再换原生标题栏/根背景色，避免标题栏抢跑变色
                _ = Task.Delay(300).ContinueWith(_ => AppServices.RunOnUi(() => { try { ApplyNativeTheme(); } catch { } }));
                break;
            case "mica": AppServices.Config.Mica = b; WindowHelper.ApplyBackdrop(this, b); break;
            case "closeToTray": AppServices.Config.Set("App", "close_to_tray", b ? "true" : "false"); break;
            case "uiEffects": AppServices.Config.Set("App", "ui_effects", b ? "true" : "false"); break;
            default:
                // 前端自定义设置：fx_* / ui_* 原样落到 [App] 段。
                // 以后新增这类设置项只改前端即可，不用改 C#、不用重编译。
                if (key.StartsWith("fx_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("ui_", StringComparison.OrdinalIgnoreCase))
                { AppServices.Config.Set("App", key, value); LogManager.Debug("自定义设置: " + key + "=" + value); }
                break;
            case "downloadDir": try { if (!string.IsNullOrWhiteSpace(value)) AppServices.Config.DownloadDir = value; } catch { } break;
            case "quality": AppServices.Config.Quality = value; break;
            case "volume": if (int.TryParse(value, out var vol)) AppServices.Player.SetVolume(vol); break;
            case "playMode": AppServices.Config.Set("Player", "mode", value); break;
            case "proxy": AppServices.Config.Set("Network", "proxy", value); break;
            case "language": AppServices.Lang.SetLanguage(value); AppServices.Config.Language = value; break;
            case "desktopLyric": AppServices.Config.Set("App", "desktop_lyric", b ? "true" : "false"); break;
            case "desktopLyricTopmost": AppServices.Config.DesktopLyricTopmost = b; break;
            case "desktopSongInfo": AppServices.Config.DesktopSongInfo = b; break;
            case "toast": AppServices.Config.Set("App", "toast", b ? "true" : "false"); break;
        }
        LogManager.Log("设置更新: " + key + "=" + value);
    }

    /// <summary>通用 API 透传：前端按 name+args 调网易云接口，返回原始 JSON。</summary>
    private async Task HandleWebApi(JsonElement doc)
    {
        try
        {
            var name = doc.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var args = new Dictionary<string, string?>();
            if (doc.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object)
                foreach (var p in a.EnumerateObject()) args[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText();
            bool post = doc.TryGetProperty("post", out var ps) && ps.GetBoolean();
            string raw = post ? await AppServices.Netease.PostRaw("/" + name.TrimStart('/'), args) : await AppServices.Netease.GetRaw("/" + name.TrimStart('/'), args);
            var id = doc.TryGetProperty("id", out var idp) ? idp.GetString() : "";
            LogManager.Log("api 响应: " + name + " len=" + raw.Length);
            PostToWeb(new { type = "api_result", id, name, data = JsonDocument.Parse(raw).RootElement });
        }
        catch (Exception e) { LogManager.Debug("api 透传失败: " + e.Message); PostToWeb(new { type = "api_result", id = doc.TryGetProperty("id", out var id2) ? id2.GetString() : "", name = doc.TryGetProperty("name", out var n2) ? n2.GetString() : "", error = e.Message }); }
    }

    // ============ 顶栏 ============
    private void OnLogo(object sender, RoutedEventArgs e) { PostToWeb(new { type = "nav", view = "home" }); }
    private void OnSearch(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var kw = (args.QueryText ?? "").Trim();
        if (string.IsNullOrEmpty(kw)) return;
        PostToWeb(new { type = "nav", view = "search", kw });
    }
    private void OnSettings(object sender, RoutedEventArgs e) { try { var w = new SettingsWindow(); w.Activate(); } catch (Exception ex) { LogManager.Error("打开设置失败: " + ex.Message); } }
    private void OnTheme(object sender, RoutedEventArgs e) { AppServices.Theme.ToggleTheme(); ApplyNativeTheme(); PushTheme(); }
    private void OnMinimize(object sender, RoutedEventArgs e) { try { (AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter)?.Minimize(); } catch { } }
    private void OnMaximize(object sender, RoutedEventArgs e) { try { if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter op) { if (op.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized) op.Restore(); else op.Maximize(); } } catch { } }
    private void OnClose(object sender, RoutedEventArgs e) { App.ExitApp(); }
}
