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
            ["crossfade"] = AppServices.Config.Crossfade,
            ["playMode"] = AppServices.Config.PlayMode,
            ["proxy"] = AppServices.Config.Proxy,
            ["language"] = AppServices.Config.Language,
            ["desktopLyric"] = AppServices.Config.Get("App", "desktop_lyric", "false").Equals("true", StringComparison.OrdinalIgnoreCase),
            ["desktopLyricTopmost"] = AppServices.Config.DesktopLyricTopmost,
            ["desktopSongInfo"] = AppServices.Config.DesktopSongInfo,
            ["updateSource"] = AppServices.Config.UpdateSource,
            ["updateRegion"] = GeoHint.InChina() ? "cn" : "other",   // 界面里能显示「自动」挑到了哪边
            // 开源仓库地址（关于页两个圆形按钮用）：前端只回传名字，URL 一律由配置拼，避免前端能开任意地址
            ["repoGithub"] = "https://github.com/" + AppServices.Config.UpdateRepo,
            ["repoGitee"] = "https://gitee.com/" + AppServices.Config.UpdateGiteeRepo,
            ["toast"] = AppServices.Config.Get("App", "toast", "true").Equals("true", StringComparison.OrdinalIgnoreCase),
            ["version"] = AppServices.Version,
            // 缓存（[Cache] 段）：设置-性能 里显示位置/上限，以及当前实际占用
            ["cacheDir"] = AppServices.Config.CacheDir,
            ["cacheLimitMb"] = AppServices.Config.CacheLimitMb,
            ["cacheSizeMb"] = Math.Round(AppServices.Cache.SizeMb(), 2),
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
            case "mica":
                AppServices.Config.Mica = b;
                WindowHelper.ApplyBackdrop(this, b, AppServices.Theme.IsDark, AppServices.Config.Get("App", "ui_material", "acrylic"));
                ApplyNativeTheme();     // Mica 开/关时原生根背景要在透明/实色之间切换
                PushTheme();            // 面板底色跟着变成半透明/实色
                break;
            case "ui_material":
                AppServices.Config.Set("App", "ui_material", value);
                WindowHelper.ApplyBackdrop(this, AppServices.Config.Mica, AppServices.Theme.IsDark, value);
                break;
            case "ui_mica_alpha":
                AppServices.Config.Set("App", "ui_mica_alpha", value);
                PushTheme();            // 只重算 CSS 变量，不用重启
                break;
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
            case "cacheDir": try { if (!string.IsNullOrWhiteSpace(value)) { AppServices.Config.CacheDir = value; AppServices.Cache.EnsureDir(); } } catch { } break;
            case "cacheLimitMb":
                if (long.TryParse(value, out var lm))
                {
                    AppServices.Config.CacheLimitMb = lm;
                    AppServices.Cache.EnforceLimit();          // 调小了立刻按新上限清理
                    PostToWeb(new { type = "cache_stat", sizeMb = Math.Round(AppServices.Cache.SizeMb(), 2), limitMb = AppServices.Config.CacheLimitMb });
                }
                break;
            case "quality": AppServices.Config.Quality = value; break;
            case "crossfade": AppServices.Config.Crossfade = value; break;
            case "volume": if (int.TryParse(value, out var vol)) { AppServices.Player.SetVolume(vol); PostToWeb(new { type = "volume_changed", v = AppServices.Player.GetVolume() }); } break;
            case "playMode":
                AppServices.Config.PlayMode = value;
                AppServices.Player.OnModeChanged(value);   // 切到随机 = 打乱列表；切回顺序 = 还原
                break;
            case "ui_console":      // 显示控制台（实时日志）
                AppServices.Config.Set("App", "ui_console", b ? "true" : "false");
                WindowHelper.ShowConsole(b);
                break;
            case "updateSource": AppServices.Config.Set("Update", "source", value); break;
            case "proxy": AppServices.Config.Set("Network", "proxy", value); break;
            case "language": AppServices.Lang.SetLanguage(value); AppServices.Config.Language = value; PushLang(); break;
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

    /// <summary>在默认浏览器里打开开源仓库（关于页的 GitHub / Gitee 圆形按钮、设置页的反馈入口）。</summary>
    private void OpenRepo(JsonElement doc)
    {
        var name = doc.TryGetProperty("name", out var n) ? (n.GetString() ?? "").ToLowerInvariant() : "";
        var page = doc.TryGetProperty("page", out var pg) ? (pg.GetString() ?? "").ToLowerInvariant() : "";
        var repoUrl = name switch
        {
            "github" => "https://github.com/" + AppServices.Config.UpdateRepo,
            "gitee" => "https://gitee.com/" + AppServices.Config.UpdateGiteeRepo,
            _ => ""
        };
        if (repoUrl.Length == 0) return;
        // page 只认白名单里的固定页面 —— 前端依旧不能传任意 URL
        var url = page switch
        {
            "issues" => repoUrl + "/issues/new",     // 反馈：直接进「新建 issue」页
            "issueslist" => repoUrl + "/issues",
            "releases" => repoUrl + "/releases",
            _ => repoUrl
        };
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            LogManager.Log("打开仓库: " + url);
        }
        catch (Exception e)
        {
            LogManager.Error("打开仓库失败: " + e.Message);
            PostToWeb(new { type = "toast", text = "打开浏览器失败：" + e.Message });
        }
    }

    /// <summary>弹出图形化文件夹选择器（资源管理器风格），选完回传前端。key = downloadDir / cacheDir</summary>
    private void PickFolder(JsonElement doc)
    {
        var key = doc.TryGetProperty("key", out var kv) ? (kv.GetString() ?? "downloadDir") : "downloadDir";
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var isCache = key.Equals("cacheDir", StringComparison.OrdinalIgnoreCase);
            var start = isCache ? AppServices.Config.CacheDir : AppServices.Config.DownloadDir;
            var title = isCache ? "选择缓存目录" : "选择下载目录";
            var picked = FolderPickerDialog.Pick(hwnd, start, title);
            if (string.IsNullOrWhiteSpace(picked)) { LogManager.Debug("用户取消了文件夹选择"); return; }
            if (isCache) { AppServices.Config.CacheDir = picked; AppServices.Cache.EnsureDir(); }
            else AppServices.Config.DownloadDir = picked;
            PostToWeb(new { type = "folder_picked", ok = true, key = key, path = picked });
            LogManager.Log((isCache ? "缓存目录" : "下载目录") + "已改为: " + picked);
        }
        catch (Exception ex)
        {
            LogManager.Error("选择目录失败(" + key + "): " + ex.GetType().Name + " hresult=0x" + ex.HResult.ToString("X8") + " " + ex.Message);
            PostToWeb(new { type = "folder_picked", ok = false, key = key, error = ex.Message });
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
}