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
    /// <summary>更新公告：从 GitHub Release 取正文（不在程序里写死更新日志）。</summary>
    private async Task HandleReleaseNotes(JsonElement doc)
    {
        var want = doc.TryGetProperty("version", out var v) ? (v.GetString() ?? "") : "";
        var (tag, body, err) = await AppServices.Updater.FetchReleaseNotesAsync(want);
        LogManager.Log($"更新公告: 请求 {want} → 取到 {tag}，正文 {body.Length} 字" + (err.Length > 0 ? "（" + err + "）" : ""));
        PostToWeb(new { type = "release_notes", want, version = tag, body, error = err });
    }

    private async Task HandleWebDiscover() { var songs = await AppServices.Netease.RecommendSongs(); PostToWeb(new { type = "songs", songs = songs.Select(ToSongDto).ToList(), discover = true }); }
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

    private void HandleWebDownload(JsonElement doc)
    {
        if (!doc.TryGetProperty("song", out var s)) return;
        var song = SongFromWeb(s);          // 带全 Duration/Album/Pic，下载历史和列表要用
        if (song is null || song.Id <= 0) return;
        AppServices.Download.Enqueue(song, AppServices.Config.DownloadDir, AppServices.Config.Quality);
        PostDownloadState();
    }
}