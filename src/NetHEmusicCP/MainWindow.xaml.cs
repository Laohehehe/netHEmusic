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

    // ---- 记住上一次播放进度（[Player] last_song_id / last_pos）----
    private long _resumeSavedSongId = -1;   // 已经写盘的曲目 id
    private long _resumeSavedPos = -1;      // 已经写盘的位置（毫秒）
    private long _lastFrontPos = 0;         // 前端最近一次上报的位置
    private long _lastFrontDur = 0;         // 前端最近一次上报的时长

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
        // 隐藏标题栏的系统菜单（右键 / Alt+Space）
        try { WindowMenuBlocker.Attach(WinRT.Interop.WindowNative.GetWindowHandle(this)); } catch (Exception ex) { LogManager.Debug("Attach 标题栏菜单屏蔽失败: " + ex.Message); }

        AppServices.OnThemeApplied = () => { try { ApplyNativeTheme(); } catch { } PushTheme(); };
        InitWebView();

        // 记住上一次播放进度：启动时先登记"上次听到哪"，等真正加载到那一首时才续播一次
        try
        {
            var lastId = AppServices.Config.LastSongId;
            var lastPos = AppServices.Config.LastPosition;
            if (lastId > 0 && lastPos > 2000)
            {
                AppServices.Player.ArmResume(lastId, lastPos);
                LogManager.Log("[续播] 已登记上次进度: id=" + lastId + " pos=" + (lastPos / 1000) + "s");
            }
        }
        catch (Exception ex) { LogManager.Debug("登记续播失败: " + ex.Message); }

        // 上次开着桌面歌词就自动开回来
        try
        {
            if (AppServices.Config.Get("App", "desktop_lyric", "false").Equals("true", StringComparison.OrdinalIgnoreCase))
                SetDesktopLyric(true);
        }
        catch (Exception ex) { LogManager.Debug("恢复桌面歌词失败: " + ex.Message); }

        // 把播放状态推给 Web 前端播放条
        AppServices.Player.SongChanged += s =>
        {
            if (s is null) return;
            // 换歌就记住“当前是第几首”，否则退出时 index 还停在加载队列那一刻的值（重启会跳回第一首）
            SavePlaylistIndex(AppServices.Player.Index);
            AppServices.RunOnUi(() => PostToWeb(new { type = "playing", song = ToSongDto(s) }));
            if (_lyricWin is not null) _ = PushLyricToWindowAsync();
        };
        AppServices.Player.PositionChanged += pos => AppServices.RunOnUi(() =>
        {
            var total = AppServices.Player.Duration;
            var c = AppServices.Player.Current;
            var dur = total > TimeSpan.Zero ? (long)total.TotalMilliseconds : (c is null ? 0 : c.Duration);
            PostToWeb(new { type = "position", pos = (long)pos.TotalMilliseconds, dur = dur });
            try { _lyricWin?.OnPosition(pos); } catch { }
        });
        // 播放队列变化：写入 config（重启后恢复）+ 通知前端刷新播放列表
        AppServices.Player.QueueChanged += (idx, count) =>
        {
            SavePlaylist(idx);
            AppServices.RunOnUi(() => PostToWeb(new { type = "queue_changed", index = idx, count = count }));
        };
        // 前端播放模式：把直链/命令交给网页
        AppServices.Player.FrontendLoad += load => AppServices.RunOnUi(() => PostToWeb(new
        {
            type = "audio_load",
            url = load.Url,
            index = load.Index,
            autoplay = true,
            startMs = load.StartMs,
            song = ToSongDto(load.Song)
        }));
        AppServices.Player.FrontendCommand += (cmd, val) => AppServices.RunOnUi(() => PostToWeb(new { type = "audio_cmd", cmd = cmd, value = val }));

        // 关闭窗口时再存一次当前曲目下标（双保险）
        try
        {
            AppWindow.Closing += (s, e) =>
            {
                SavePlaylistIndex(AppServices.Player.Index);
                SaveResumeProgress(_lastFrontPos, _lastFrontDur, true);   // 关窗时最后落一次盘
            };
        }
        catch (Exception ex) { LogManager.Debug("挂 Closing 失败: " + ex.Message); }
        AppServices.Download.Completed += it => AppServices.RunOnUi(() => PostToWeb(new { type = "toast", kind = "download", text = "下载完成: " + it.Display }));
        AppServices.Download.Failed += it => AppServices.RunOnUi(() => PostToWeb(new { type = "toast", kind = "download", text = "下载失败: " + (it.Error ?? "") }));

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
            // 允许网页 <audio> 在无用户手势时也能播放（前端播放内核需要：自动下一首 / SMTC 触发）
            var browserArgs = "--autoplay-policy=no-user-gesture-required";
            // 设置 → 性能 → GPU 加速：关掉后走软件渲染（省 GPU / 兼容老显卡，需重启生效）
            bool gpuOn = !AppServices.Config.Get("App", "perf_gpu", "true").Equals("false", StringComparison.OrdinalIgnoreCase);
            if (!gpuOn)
            {
                browserArgs += " --disable-gpu --disable-gpu-compositing --disable-accelerated-2d-canvas --disable-accelerated-video-decode";
                LogManager.Log("GPU 加速已关闭（软件渲染）");
            }
            var envOptions = new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = browserArgs
            };
            // 用户数据放到 %APPDATA%\netHEmusic\webview：安装目录保持只读，MSI 卸载才干净
            string webviewData = Path.Combine(AppServices.Config.DataDir, "webview");
            try { Directory.CreateDirectory(webviewData); } catch { }
            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateWithOptionsAsync(null, webviewData, envOptions);
            await WebView.EnsureCoreWebView2Async(env);
            var core = WebView.CoreWebView2;
            var webFolder = FindWebFolder();
            core.SetVirtualHostNameToFolderMapping("appassets", webFolder, CoreWebView2HostResourceAccessKind.Allow);
            // 关闭 WebView2 缓存，保证热重载后拿到最新的 HTML/CSS/JS
            try { await core.CallDevToolsProtocolMethodAsync("Network.setCacheDisabled", "{\"cacheDisabled\":true}"); } catch (Exception ce) { LogManager.Debug("禁用缓存失败: " + ce.Message); }
            SetupHotReload(webFolder);
            ApplyDevTools(core);                       // 按设置决定是否允许 F12 打开开发者工具
            core.WebMessageReceived += OnWebMessage;
            core.NavigationCompleted += (s, e) =>
            {
                AppServices.Player.ResetFrontendAudio();   // 网页重载后 <audio> 是空的
                PushTheme(); PostToWeb(new { type = "nav", view = "home" }); CheckVersionNotice();
            };
            WebView.Source = new Uri("https://appassets/index.html");
            LogManager.Log("WebView2 已加载: " + webFolder);
        }
        catch (Exception e) { LogManager.Error("WebView2 初始化失败: " + e.Message); }
    }
    private DesktopLyricWindow? _lyricWin;

    /// <summary>开关桌面歌词窗口（并记忆到设置）。</summary>
    private void SetDesktopLyric(bool on)
    {
        AppServices.Config.Set("App", "desktop_lyric", on ? "true" : "false");
        AppServices.RunOnUi(() =>
        {
            try
            {
                if (on)
                {
                    if (_lyricWin is null)
                    {
                        _lyricWin = new DesktopLyricWindow();
                        _lyricWin.SetTopmost(AppServices.Config.DesktopLyricTopmost);
                        _ = PushLyricToWindowAsync();
                    }
                    _lyricWin.Activate();
                    _lyricWin.AppWindow.Show();
                }
                else { _lyricWin?.AppWindow.Hide(); }
            }
            catch (Exception e) { LogManager.Error("桌面歌词窗口失败: " + e.Message); }
            PostToWeb(new { type = "desktop_lyric_state", on = on });
            LogManager.Log("桌面歌词: " + (on ? "已开启" : "已关闭"));
        });
    }

    /// <summary>把当前歌曲的歌词推给桌面歌词窗口。</summary>
    private async Task PushLyricToWindowAsync()
    {
        try
        {
            var cur = AppServices.Player.Current; if (cur is null || _lyricWin is null) return;
            var json = await AppServices.Netease.JsonLyric(cur.Id);
            var (lrc, tl, ro, yrc) = AppServices.Netease.ParseLyric(json);
            AppServices.RunOnUi(() => { try { _lyricWin?.SetLyric(lrc, tl, ro); } catch { } });
        }
        catch (Exception e) { LogManager.Debug("推送歌词失败: " + e.Message); }
    }

    /// <summary>只更新“当前第几首”（很小，可以在每次换歌时写）。</summary>
    private void SavePlaylistIndex(int index)
    {
        try { AppServices.Config.PlaylistIndex = Math.Max(0, index); }
        catch (Exception e) { LogManager.Debug("保存播放下标失败: " + e.Message); }
    }

    /// <summary>
    /// 记住"上次听到哪"：换歌或位移超过 5 秒才写盘（audio_state 约 1 次/秒，不能每次都写）。
    /// 快播完（距结尾 3 秒内）不记，避免下次启动续播到结尾。
    /// </summary>
    private void SaveResumeProgress(long pos, long dur, bool force)
    {
        try
        {
            var s = AppServices.Player.Current;
            if (s is null || pos < 0) return;
            if (dur > 0 && pos > dur - 3000) return;
            bool songChanged = s.Id != _resumeSavedSongId;
            if (!force && !songChanged && Math.Abs(pos - _resumeSavedPos) < 5000) return;
            if (songChanged) AppServices.Config.LastSongId = s.Id;
            AppServices.Config.LastPosition = Math.Max(0, pos);
            _resumeSavedSongId = s.Id;
            _resumeSavedPos = pos;
            if (songChanged) LogManager.Debug("[续播] 已记录曲目: id=" + s.Id + " pos=" + (pos / 1000) + "s");
        }
        catch (Exception e) { LogManager.Debug("保存播放进度失败: " + e.Message); }
    }

    /// <summary>把当前播放队列写进 config，重启后可以恢复。</summary>
    private void SavePlaylist(int index)
    {
        try
        {
            var q = AppServices.Player.Queue;
            AppServices.Config.SavePlaylist(JsonSerializer.Serialize(q));
            AppServices.Config.PlaylistIndex = Math.Max(0, index);
            LogManager.Debug("播放列表已保存: " + q.Count + " 首 @ " + index);
        }
        catch (Exception e) { LogManager.Debug("保存播放列表失败: " + e.Message); }
    }

    /// <summary>config 里的版本比当前版本旧 → 通知前端弹更新公告（关闭后再回写版本号）。</summary>
    private void CheckVersionNotice()
    {
        try
        {
            var seen = (AppServices.Config.VersionSeen ?? "").Trim();
            if (!IsVersionOlder(seen, AppServices.Version)) { LogManager.Debug("版本已是最新记录: " + seen); return; }
            LogManager.Log("检测到版本变化: config=" + (string.IsNullOrEmpty(seen) ? "(空)" : seen) + " → 当前 " + AppServices.Version + "，弹出更新公告");
            PostToWeb(new { type = "update_notice", from = string.IsNullOrEmpty(seen) ? "" : seen, to = AppServices.Version });
        }
        catch (Exception e) { LogManager.Debug("版本检测失败: " + e.Message); }
    }

    private static bool IsVersionOlder(string? seen, string current)
    {
        static Version Parse(string s)
            => Version.TryParse((s ?? "").Trim(), out var v) ? v : new Version(0, 0, 0, 0);
        return Parse(seen) < Parse(current);
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
    /// <summary>开发者工具开关（设置里可开，默认关）。开了之后 F12 / 右键"检查"可用。</summary>
    private void ApplyDevTools(Microsoft.Web.WebView2.Core.CoreWebView2? core = null)
    {
        try
        {
            core ??= WebView.CoreWebView2;
            if (core is null) return;
            bool on = !AppServices.Config.Get("App", "ui_devtools", "false").Equals("false", StringComparison.OrdinalIgnoreCase);
            core.Settings.AreDevToolsEnabled = on;
            core.Settings.AreBrowserAcceleratorKeysEnabled = true;   // F12 属于浏览器快捷键
            LogManager.Log("开发者工具: " + (on ? "已启用（F12 打开）" : "已关闭"));
        }
        catch (Exception e) { LogManager.Debug("设置开发者工具失败: " + e.Message); }
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
        try
        {
            // 时长：前端 DTO 用 PascalCase 的 Duration，旧数据可能是 duration/dt，都要认
            long dur = 0;
            foreach (var key in new[] { "Duration", "duration", "dt", "DurationMs" })
                if (s.TryGetProperty(key, out var dv) && dv.ValueKind == JsonValueKind.Number) { dur = dv.GetInt64(); break; }
            var song = new Song
            {
                Id = s.TryGetProperty("Id", out var id) ? id.GetInt64() : 0,
                Title = s.TryGetProperty("Title", out var t) ? t.GetString() ?? "" : (s.TryGetProperty("name", out var t2) ? t2.GetString() ?? "" : ""),
                Artists = new List<Artist> { new Artist { Name = s.TryGetProperty("Artist", out var a) ? a.GetString() ?? "" : "" } },
                Album = new Album { Name = s.TryGetProperty("Album", out var al) ? al.GetString() ?? "" : "", PicUrl = s.TryGetProperty("Pic", out var p) ? p.GetString() : "" },
                Pic = s.TryGetProperty("Pic", out var p2) ? p2.GetString() : "",
                DurationMs = dur
            };
            return song;
        }
        catch { return null; }
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
                case "pick_folder": PickFolder(); break;
                case "open_folder": OpenDownloadDir(); break;
                case "release_notes": _ = HandleReleaseNotes(doc); break;
                case "notice_seen": AppServices.Config.VersionSeen = AppServices.Version; LogManager.Log("[Version] version 已更新为 " + AppServices.Version); break;
                case "queue_get": PostToWeb(new { type = "queue", songs = AppServices.Player.Queue.Select(ToSongDto).ToList(), index = AppServices.Player.Index }); break;
                case "play_index": { if (doc.TryGetProperty("index", out var qi) && qi.TryGetInt32(out var qn)) _ = AppServices.Player.PlayAtAsync(qn); break; }
                case "queue_clear": AppServices.Player.ClearQueue(); LogManager.Log("已清空播放列表"); break;
                case "audio_ended": _ = AppServices.Player.AutoNextAsync(); break;
                case "audio_state":
                    {
                        bool playing = doc.TryGetProperty("playing", out var pl) && pl.ValueKind == JsonValueKind.True;
                        long pos = doc.TryGetProperty("pos", out var pv) && pv.ValueKind == JsonValueKind.Number ? pv.GetInt64() : 0;
                        long dur = doc.TryGetProperty("dur", out var dv) && dv.ValueKind == JsonValueKind.Number ? dv.GetInt64() : 0;
                        AppServices.Player.SetFrontendState(playing, pos, dur);
                        _lastFrontPos = pos; _lastFrontDur = dur;
                        try { _lyricWin?.OnPosition(TimeSpan.FromMilliseconds(pos)); } catch { }   // 前端模式进度只从这里来
                        SaveResumeProgress(pos, dur, false);
                        break;
                    }
                case "audio_error":
                    LogManager.Warn("前端播放失败: " + (doc.TryGetProperty("message", out var em) ? em.GetString() : ""));
                    _ = AppServices.Player.AutoNextAsync();
                    break;
                case "seek": { if (doc.TryGetProperty("pos", out var sk) && sk.TryGetInt64(out var skn)) AppServices.Player.Seek(TimeSpan.FromMilliseconds(skn)); break; }
                case "queue_remove": { if (doc.TryGetProperty("index", out var ri) && ri.TryGetInt32(out var rn)) AppServices.Player.RemoveAt(rn); break; }
                case "queue_next": { if (doc.TryGetProperty("index", out var ni) && ni.TryGetInt32(out var nn)) AppServices.Player.MoveToNext(nn); break; }
                case "share":
                    {
                        var song = doc.TryGetProperty("song", out var ss) ? SongFromWeb(ss) : null;
                        if (song is not null)
                        {
                            var text = "分享单曲《" + song.Title + "》- " + song.ArtistsName
                                     + "：" + "https://music.163.com/song?id=" + song.Id + " (@网易云音乐)";
                            var ok = ClipboardHelper.SetText(text);
                            PostToWeb(new { type = "toast", text = ok ? "分享链接已复制到剪贴板" : "复制失败（剪贴板被占用）" });
                            LogManager.Log("分享: " + (ok ? "已复制 " : "复制失败 ") + text);
                        }
                        break;
                    }
                case "desktop_lyric": SetDesktopLyric(doc.TryGetProperty("on", out var dlOn) && dlOn.ValueKind == JsonValueKind.True); break;
                case "set_setting": HandleSetSetting(doc); break;
                case "log": LogManager.Info("web: " + (doc.TryGetProperty("msg", out var m) ? m.GetString() : "")); break;
            }
        }
        catch (Exception ex) { LogManager.Debug("Web 消息失败: " + ex.Message); }
    }

    private async Task HandleWebSearch(JsonElement doc) { var kw = doc.TryGetProperty("kw", out var k) ? k.GetString() ?? "" : ""; var songs = await AppServices.Netease.Search(kw, 1, 50); PostToWeb(new { type = "songs", songs = songs.Select(ToSongDto).ToList(), search = kw }); }
    /// <summary>更新公告：从 GitHub Release 取正文（不在程序里写死更新日志）。</summary>
    private async Task HandleReleaseNotes(JsonElement doc)
    {
        var want = doc.TryGetProperty("version", out var v) ? (v.GetString() ?? "") : "";
        var (tag, body, err) = await AppServices.Updater.FetchReleaseNotesAsync(want);
        LogManager.Log($"更新公告: 请求 {want} → 取到 {tag}，正文 {body.Length} 字" + (err.Length > 0 ? "（" + err + "）" : ""));
        PostToWeb(new { type = "release_notes", want, version = tag, body, error = err });
    }

    private async Task HandleWebDiscover() { var songs = await AppServices.Netease.RecommendSongs(); PostToWeb(new { type = "songs", songs = songs.Select(ToSongDto).ToList(), discover = true }); }
    private async Task HandleWebPlaylist(JsonElement doc) { long id = 0; if (doc.TryGetProperty("id", out var i)) id = i.GetInt64(); if (id <= 0) return; var tracks = await AppServices.Netease.PlaylistTracks(id, 1000, 0); PostToWeb(new { type = "songs", songs = tracks.Select(ToSongDto).ToList() }); }
    private async Task HandleWebLyric(JsonElement doc) { long id = 0; if (doc.TryGetProperty("id", out var d)) id = d.GetInt64(); if (id <= 0 && AppServices.Player.Current != null) id = AppServices.Player.Current.Id; var json = await AppServices.Netease.JsonLyric(id); var (lrc, tl, ro, yrc) = AppServices.Netease.ParseLyric(json); PostToWeb(new { type = "lyric", lrc, tlyric = tl, romalrc = ro, yrc }); }
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
            ["crossfade"] = AppServices.Config.Get("Player", "crossfade", "0"),
            ["playMode"] = AppServices.Config.Get("Player", "mode", "order"),
            ["proxy"] = AppServices.Config.Proxy,
            ["language"] = AppServices.Config.Language,
            ["desktopLyric"] = AppServices.Config.Get("App", "desktop_lyric", "false").Equals("true", StringComparison.OrdinalIgnoreCase),
            ["desktopLyricTopmost"] = AppServices.Config.DesktopLyricTopmost,
            ["desktopSongInfo"] = AppServices.Config.DesktopSongInfo,
            ["toast"] = AppServices.Config.Get("App", "toast", "true").Equals("true", StringComparison.OrdinalIgnoreCase),
            ["version"] = AppServices.Version,
            ["fonts"] = SystemFonts.Families(),   // 本机字体列表，设置里的字体下拉用
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
            case "custom_accent":
                // 自定义强调色：不在任何前缀白名单里，必须显式存，否则重启就丢
                AppServices.Config.Set("App", "custom_accent", value);
                break;
            case "ui_devtools":
                AppServices.Config.Set("App", "ui_devtools", b ? "true" : "false");
                ApplyDevTools();
                break;
            default:
                // 前端自定义设置：fx_* / ui_* 原样落到 [App] 段。
                // 以后新增这类设置项只改前端即可，不用改 C#、不用重编译。
                if (key.StartsWith("fx_", StringComparison.OrdinalIgnoreCase)
                    || key.StartsWith("ui_", StringComparison.OrdinalIgnoreCase)
                    || key.StartsWith("lyric_", StringComparison.OrdinalIgnoreCase)
                    || key.StartsWith("hk_", StringComparison.OrdinalIgnoreCase)
                    || key.StartsWith("vz_", StringComparison.OrdinalIgnoreCase)
                    || key.StartsWith("perf_", StringComparison.OrdinalIgnoreCase)
                    || key.StartsWith("dl_", StringComparison.OrdinalIgnoreCase))     // dl_* = 桌面歌词外观
                {
                    AppServices.Config.Set("App", key, value);
                    LogManager.Debug("自定义设置: " + key + "=" + value);
                    if (key.StartsWith("dl_", StringComparison.OrdinalIgnoreCase))
                    { try { _lyricWin?.ApplySettings(); } catch { } }   // 桌面歌词外观即时生效
                }
                break;
            case "downloadDir": try { if (!string.IsNullOrWhiteSpace(value)) AppServices.Config.DownloadDir = value; } catch { } break;
            case "quality": AppServices.Config.Quality = value; break;
            case "crossfade": AppServices.Config.Set("Player", "crossfade", value); break;
            case "volume": if (int.TryParse(value, out var vol)) { AppServices.Player.SetVolume(vol); PostToWeb(new { type = "volume_changed", v = AppServices.Player.GetVolume() }); } break;
            case "playMode": AppServices.Config.Set("Player", "mode", value); break;
            case "proxy": AppServices.Config.Set("Network", "proxy", value); break;
            case "language": AppServices.Lang.SetLanguage(value); AppServices.Config.Language = value; break;
            case "desktopLyric": AppServices.Config.Set("App", "desktop_lyric", b ? "true" : "false"); break;
            case "desktopLyricTopmost":
                AppServices.Config.DesktopLyricTopmost = b;
                try { _lyricWin?.SetTopmost(b); } catch { }
                break;
            case "desktopSongInfo": AppServices.Config.DesktopSongInfo = b; break;
            case "toast": AppServices.Config.Set("App", "toast", b ? "true" : "false"); break;
        }
        LogManager.Log("设置更新: " + key + "=" + value);
    }

    /// <summary>弹出图形化文件夹选择器（资源管理器风格），选完回传前端。</summary>
    private void PickFolder()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var picked = FolderPickerDialog.Pick(hwnd, AppServices.Config.DownloadDir, "选择下载目录");
            if (string.IsNullOrWhiteSpace(picked)) { LogManager.Debug("用户取消了文件夹选择"); return; }
            AppServices.Config.DownloadDir = picked;
            PostToWeb(new { type = "folder_picked", ok = true, key = "downloadDir", path = picked });
            LogManager.Log("下载目录已改为: " + picked);
        }
        catch (Exception ex)
        {
            LogManager.Error("选择下载目录失败: " + ex.GetType().Name + " hresult=0x" + ex.HResult.ToString("X8") + " " + ex.Message);
            PostToWeb(new { type = "folder_picked", ok = false, error = ex.Message });
            PostToWeb(new { type = "toast", text = "打开文件夹选择器失败：" + ex.Message });
        }
    }

    /// <summary>用资源管理器打开当前下载目录。</summary>
    private void OpenDownloadDir()
    {
        try
        {
            var dir = AppServices.Config.DownloadDir;
            if (string.IsNullOrWhiteSpace(dir)) return;
            if (!Directory.Exists(dir)) { PostToWeb(new { type = "toast", text = "下载目录不存在：" + dir }); return; }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
            LogManager.Log("已打开下载目录: " + dir);
        }
        catch (Exception ex)
        {
            LogManager.Error("打开下载目录失败: " + ex.Message);
            PostToWeb(new { type = "toast", text = "打开下载目录失败：" + ex.Message });
        }
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
