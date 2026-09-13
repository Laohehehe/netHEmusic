using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using netHEmusic.Core.Api;
using netHEmusic.Core.Config;
using netHEmusic.Core.Logging;
using netHEmusic.Core.Model;

namespace netHEmusic.Core.Download;

/// <summary>单个歌曲下载（流式，带进度/取消）。</summary>
public class DownloadItem
{
    public long Id { get; set; }
    public string Display { get; set; } = "";
    public string? Url { get; set; }
    public string SavePath { get; set; } = "";
    public double Progress { get; set; }
    public string Status { get; set; } = "queued"; // queued|downloading|done|failed|cancelled
    public string? Error { get; set; }
    public CancellationTokenSource Cts { get; set; } = new();
}

/// <summary>
/// 下载管理器：并发下载队列。优先 /song/download/url 直链，失败回退 /song/url 播放直链。
/// 进度/完成事件由 MainWindow 在 UI 线程处理。
/// </summary>
public class DownloadManager
{
    public const string Version = "26.9.13.35";
    private readonly NetEaseClient _client;
    private readonly AppConfig _config;
    private readonly HttpClient _http = new();
    private readonly ConcurrentDictionary<long, DownloadItem> _items = new();
    private readonly SemaphoreSlim _gate;

    public event Action<DownloadItem>? Progress;
    public event Action<DownloadItem>? Completed;
    public event Action<DownloadItem>? Failed;

    public DownloadManager(NetEaseClient client, AppConfig config)
    {
        _client = client;
        _config = config;
        _gate = new SemaphoreSlim(config.ConcurrentDownloads);
    }

    public static int BitRate(string quality) => quality switch { "standard" => 128000, "lossless" => 999000, _ => 320000 };

    private static readonly char[] Illegal = Path.GetInvalidFileNameChars();

    /// <summary>按设置生成下载文件名（默认：歌曲名 - 歌手）。</summary>
    private static string NameFor(Song s)
    {
        var t = (s.Title ?? "").Trim();
        var a = (s.ArtistsName ?? "").Trim();
        switch ((Core.AppServices.Config.MusicNameFormat ?? "title-artist").Trim().ToLowerInvariant())
        {
            case "artist-title": return a.Length > 0 ? a + " - " + t : t;
            case "title": return t;
            default: return a.Length > 0 ? t + " - " + a : t;
        }
    }

    public static string Sanitize(string name)
    {
        if (string.IsNullOrEmpty(name)) return "untitled";
        var s = new string(name.Select(c => Illegal.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(s) ? "untitled" : s;
    }

    /// <summary>启动一首歌下载到 saveDir。</summary>
    public async Task<DownloadItem> Download(Song song, string saveDir, string quality = "high")
    {
        var item = new DownloadItem { Id = song.Id, Display = song.DisplayName, Status = "queued", SavePath = "" };
        _items[song.Id] = item;
        _ = Task.Run(() => RunDownload(item, song, saveDir, quality));
        return item;
    }

    private async Task RunDownload(DownloadItem item, Song song, string saveDir, string quality)
    {
        try
        {
            await _gate.WaitAsync(item.Cts.Token);
            item.Status = "downloading";
            Progress?.Invoke(item);
            int br = BitRate(quality);

            var url = await _client.DownloadUrl(song.Id, br);
            if (string.IsNullOrEmpty(url)) url = await FallbackPlayUrl(song.Id, br);
            if (string.IsNullOrEmpty(url))
            {
                Fail(item, song.Fee > 0 || (song.Privilege?.Fee ?? 0) > 0 ? "VIP 歌曲需登录 VIP 账号才能下载完整" : "未获取到下载地址（可能需要登录）");
                return;
            }

            var ext = url.Contains(".flac", StringComparison.OrdinalIgnoreCase) ? ".flac" : ".mp3";
            var baseName = Sanitize(NameFor(song));   // 按「音乐命名格式」设置生成文件名
            Directory.CreateDirectory(saveDir);
            var path = Path.Combine(saveDir, baseName + ext);
            int n = 1;
            while (File.Exists(path)) { path = Path.Combine(saveDir, baseName + "_" + (n++) + ext); }

            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, item.Cts.Token);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? 0;
            var tmp = path + ".part";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                long done = 0;
                var buf = new byte[1024 * 64];
                await using var stream = await resp.Content.ReadAsStreamAsync();
                int read;
                while ((read = await stream.ReadAsync(buf, item.Cts.Token)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, read), item.Cts.Token);
                    done += read;
                    item.Progress = total > 0 ? (double)done / total : 0;
                    Progress?.Invoke(item);
                }
            }
            File.Move(tmp, path, true);
            item.SavePath = path;
            item.Status = "done";
            item.Progress = 1.0;
            LogManager.Log("下载完成: " + path);
            Completed?.Invoke(item);
        }
        catch (OperationCanceledException)
        {
            item.Status = "cancelled"; item.Error = "已取消"; Failed?.Invoke(item);
        }
        catch (Exception e)
        {
            LogManager.Error("下载失败 id=" + item.Id + " : " + e.Message);
            Fail(item, e.Message);
        }
        finally { _gate.Release(); }
    }

    private async Task<string?> FallbackPlayUrl(long id, int br)
    {
        var m = await _client.SongUrl(id.ToString(), br);
        return m.Values.FirstOrDefault(v => !string.IsNullOrEmpty(v));
    }

    private void Fail(DownloadItem item, string msg)
    {
        item.Status = "failed"; item.Error = msg; Failed?.Invoke(item);
        LogManager.Warn("下载失败 id=" + item.Id + " : " + msg);
    }

    public void Cancel(long id) { if (_items.TryGetValue(id, out var it)) it.Cts.Cancel(); }
}
