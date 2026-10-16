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

/// <summary>
/// 主窗口：无边框 Chrome（logo/主题/窗口控制）+ WebView2 完整 YesPlayMusic 风格播放器。
/// WebView2 前端与 C# 壳通过 WebMessage 通信；播放/进度由 C# 推送前端；通用网易云 API 透传。
/// </summary>
public sealed partial class MainWindow : Window
{

    /// <summary>应用原生配色（根背景 + 无边框标题栏），保证标题栏与 #main/#sidebar 同色。</summary>
    private void ApplyNativeTheme()
    {
        AppServices.Theme.Apply(Root);
        try
        {
            bool dark = AppServices.Theme.IsDark;
            bool mica = AppServices.Config.Mica;
            // 标题栏高度为 0（用户要求），配色渐变/液态玻璃都在网页里画，这里只留窗口底色与系统按钮字色
            Root.Background = mica ? null : new Microsoft.UI.Xaml.Media.SolidColorBrush(AppServices.Theme.SurfaceColor(dark));
            // 最小化/最大化/关闭三个字的颜色跟着配色，否则浅色方案下是白字看不见
            try
            {
                var fg = AppServices.Theme.TextColor(dark);
                AppWindow.TitleBar.ButtonForegroundColor = fg;
                AppWindow.TitleBar.ButtonHoverForegroundColor = fg;
                AppWindow.TitleBar.ButtonInactiveForegroundColor = global::Windows.UI.Color.FromArgb(150, fg.R, fg.G, fg.B);
            }
            catch { }
            WindowHelper.SyncBackdropTheme(dark);   // 材质的深浅跟着应用配色走（否则切浅色后标题栏还是深的）
            WindowHelper.SyncSystemMenuTheme(dark);  // 托盘右键等系统菜单也跟着深浅色（最简改法，不重写菜单）
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
            ExtendsContentIntoTitleBar = true;      // 系统按钮浮在网页上；不再 SetTitleBar（标题栏行高为 0，网页那边也不再自绘拖拽带）
            AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        }
        catch (Exception e) { LogManager.Debug("标题栏: " + e.Message); }
        WindowHelper.ApplyBackdrop(this, AppServices.Config.Mica, AppServices.Theme.IsDark, AppServices.Config.Get("App", "ui_material", "acrylic"));
        // 隐藏标题栏的系统菜单（右键 / Alt+Space）
        try { WindowMenuBlocker.Attach(WinRT.Interop.WindowNative.GetWindowHandle(this)); } catch (Exception ex) { LogManager.Debug("Attach 标题栏菜单屏蔽失败: " + ex.Message); }

        AppServices.OnThemeApplied = () => { try { ApplyNativeTheme(); } catch { } PushTheme(); };
        // 上次开着控制台就继续开着（设置→高级）
        try { if (AppServices.Config.Get("App", "ui_console", "false").Equals("true", StringComparison.OrdinalIgnoreCase)) WindowHelper.ShowConsole(true); } catch { }
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
        // 队列/进度变化（C# 侧已节流）→ 推给前端下载管理页刷新
        AppServices.Download.Changed += () => AppServices.RunOnUi(PostDownloadState);

        LogManager.Log("MainWindow 构造完成");
        _ = StartupFlowAsync();
    }














}
