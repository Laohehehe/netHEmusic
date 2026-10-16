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
    // ================= 下载管理（正在下载 / 已下载音乐） =================

    /// <summary>把下载队列快照推给前端。</summary>
    private void PostDownloadState()
        => PostToWeb(new { type = "download_state", tasks = AppServices.Download.Snapshot() });

    /// <summary>扫下载目录 + 和下载历史按文件名匹配后的「已下载音乐」列表。</summary>
    private void PostDownloadedList()
        => PostToWeb(new { type = "download_list", dir = AppServices.Config.DownloadDir, items = AppServices.Download.DownloadedList() });

    private void HandleDownloadOp(JsonElement doc, string op)
    {
        if (!doc.TryGetProperty("id", out var idv) || !idv.TryGetInt64(out var id)) return;
        switch (op)
        {
            case "pause": AppServices.Download.Pause(id); break;
            case "resume": AppServices.Download.Resume(id); break;
            case "remove": AppServices.Download.Remove(id); break;
        }
        PostDownloadState();
    }

    /// <summary>删除已下载的音乐（前端已二次确认）→ 移入回收站 + 清历史。</summary>
    private void HandleDeleteDownloaded(JsonElement doc)
    {
        var name = doc.TryGetProperty("fileName", out var f) ? (f.GetString() ?? "") : "";
        var (ok, msg) = AppServices.Download.DeleteDownloaded(name);
        LogManager.Log("删除已下载音乐: " + name + " → " + msg + (ok ? "" : "（失败）"));
        PostToWeb(new { type = "toast", text = msg });
        PostDownloadedList();
    }

    /// <summary>在资源管理器里定位已下载的文件。</summary>
    private void HandleOpenDownloaded(JsonElement doc)
    {
        var name = doc.TryGetProperty("fileName", out var f) ? (f.GetString() ?? "") : "";
        try
        {
            var path = Path.Combine(AppServices.Config.DownloadDir, Path.GetFileName(name));
            if (!File.Exists(path)) { PostToWeb(new { type = "toast", text = "文件不存在：" + name }); PostDownloadedList(); return; }
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
            LogManager.Log("已在资源管理器中定位: " + path);
        }
        catch (Exception e) { PostToWeb(new { type = "toast", text = "打开失败：" + e.Message }); }
    }

    /// <summary>
    /// 前端媒体错误：code 4（源不支持，多半是直链过期）先清掉缓存直链、重新解析并重放同一首；
    /// 仍失败（或不是 code 4）才切歌。以前这里无条件 AutoNext，于是"直链过期"表现成"这首歌被跳过"。
    /// </summary>
    private async Task HandleAudioErrorAsync(int code)
    {
        var cur = AppServices.Player.Current;
        if (code == 4)
        {
            try
            {
                if (await AppServices.Player.RetryCurrentAsync())
                {
                    LogManager.Log("已用重新解析的直链重试当前曲目" + (cur is null ? "" : "：" + cur.DisplayName));
                    return;
                }
            }
            catch (Exception e) { LogManager.Error("重试播放失败: " + e.Message); }
        }
        LogManager.Warn("跳过当前曲目" + (cur is null ? "" : "：" + cur.DisplayName) + "（code=" + code + "，重试未成功）");
        await AppServices.Player.AutoNextAsync();
    }
}