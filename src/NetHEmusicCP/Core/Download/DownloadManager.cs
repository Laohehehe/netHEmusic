using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using netHEmusic.Core.Api;
using netHEmusic.Core.Config;
using netHEmusic.Core.Logging;
using netHEmusic.Core.Model;
using netHEmusic.Core.Native;

namespace netHEmusic.Core.Download;

/// <summary>单个歌曲下载任务（流式 + 断点续传 + 可暂停）。</summary>
public class DownloadItem
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string Pic { get; set; } = "";
    public long DurationMs { get; set; }
    public string Quality { get; set; } = "high";
    public string Dir { get; set; } = "";
    /// <summary>正式文件名（含扩展名）。取到直链后定下，续传时沿用同一个名字才能接上 .part。</summary>
    public string FileName { get; set; } = "";
    public string SavePath { get; set; } = "";
    public long DoneBytes { get; set; }
    public long TotalBytes { get; set; }
    public double Progress => TotalBytes > 0 ? Math.Min(1.0, (double)DoneBytes / TotalBytes) : 0;
    /// <summary>queued / downloading / paused / done / failed</summary>
    public string Status { get; set; } = "queued";
    public string Error { get; set; } = "";
    public string Display => string.IsNullOrEmpty(Artist) ? Title : Artist + " - " + Title;

    internal long Seq;
    internal int Fee;
    internal bool PauseRequested;
    internal CancellationTokenSource Cts = new();
    internal string TempPath => SavePath + ".part";
}

/// <summary>
/// 下载管理器：多任务并行，每个任务单连接流式下载（不是分片多线程）。
/// 并发数 = [Download] concurrent；支持暂停/继续（HTTP Range 断点续传）、队列落盘、下载历史。
/// 直链优先 /song/download/url，失败回退 /song/url；直链有时效，续传前重新取一次。
/// </summary>
public class DownloadManager
{
    public const string Version = "26.9.25.5";
    private readonly NetEaseClient _client;
    private readonly AppConfig _config;
    private readonly DownloadStore _store;
    private readonly HttpClient _http = new();
    private readonly ConcurrentDictionary<long, DownloadItem> _items = new();
    private readonly SemaphoreSlim _gate;
    private long _seq;
    private DateTime _lastNotify = DateTime.MinValue;

    /// <summary>状态/进度有变化（已节流）→ MainWindow 推给前端刷新下载页。</summary>
    public event Action? Changed;
    public event Action<DownloadItem>? Completed;
    public event Action<DownloadItem>? Failed;

    public DownloadManager(NetEaseClient client, AppConfig config, DownloadStore store)
    {
        _client = client;
        _config = config;
        _store = store;
        _gate = new SemaphoreSlim(Math.Max(1, config.ConcurrentDownloads));
        RestoreFromStore();
    }

    public static int BitRate(string quality) => quality switch { "standard" => 128000, "lossless" => 999000, _ => 320000 };

    /// <summary>音质键 → 界面显示名。</summary>
    public static string QualityLabel(string quality) => quality switch
    {
        "standard" => "标准",
        "lossless" => "无损",
        "hires" => "Hi-Res",
        _ => "极高"
    };

    private static readonly char[] Illegal = Path.GetInvalidFileNameChars();

    /// <summary>按设置生成下载文件名（默认：歌曲名 - 歌手）。</summary>
    private static string NameFor(string title, string artist)
    {
        var t = (title ?? "").Trim();
        var a = (artist ?? "").Trim();
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

    // ================= 队列操作 =================

    /// <summary>加入下载队列（同一首已在队列里就不重复加）。</summary>
    public DownloadItem Enqueue(Song song, string saveDir, string quality = "high")
    {
        if (_items.TryGetValue(song.Id, out var exist) && exist.Status is "queued" or "downloading" or "paused")
        {
            Notify(true);
            return exist;
        }
        var item = new DownloadItem
        {
            Id = song.Id,
            Title = song.Title,
            Artist = song.ArtistsName,
            Album = song.AlbumName,
            Pic = song.PicUrl,
            DurationMs = song.Duration,
            Fee = Math.Max(song.Fee, song.Privilege?.Fee ?? 0),
            Quality = quality,
            Dir = string.IsNullOrWhiteSpace(saveDir) ? AppServices.Config.DownloadDir : saveDir,
            Status = "queued",
            Seq = Interlocked.Increment(ref _seq)
        };
        _items[song.Id] = item;
        PersistQueue(true);
        Notify(true);
        _ = RunAsync(item);
        LogManager.Log("加入下载队列: " + item.Display + "（" + QualityLabel(quality) + "）");
        return item;
    }

    /// <summary>暂停：正在进行的中断连接（已下字节留在 .part，可续传）。</summary>
    public void Pause(long id)
    {
        if (!_items.TryGetValue(id, out var item)) return;
        if (item.Status is not ("downloading" or "queued")) return;
        item.PauseRequested = true;
        try { item.Cts.Cancel(); } catch { }
        if (item.Status == "queued") { item.Status = "paused"; PersistQueue(true); Notify(true); }
        LogManager.Log("下载已暂停: " + item.Display);
    }

    /// <summary>继续/重试：重新取直链，从 .part 的大小处 Range 续传。</summary>
    public void Resume(long id)
    {
        if (!_items.TryGetValue(id, out var item)) return;
        if (item.Status is "downloading" or "done") return;
        item.PauseRequested = false;
        item.Cts = new CancellationTokenSource();
        item.Status = "queued";
        item.Error = "";
        PersistQueue(true);
        Notify(true);
        _ = RunAsync(item);
    }

    /// <summary>开始全部：暂停的 + 失败的都重新跑起来。</summary>
    public void ResumeAll()
    {
        foreach (var item in _items.Values.Where(i => i.Status is "paused" or "failed").OrderBy(i => i.Seq).ToList()) Resume(item.Id);
    }

    /// <summary>暂停全部：正在下载的断开连接，等待中的也一起暂停。</summary>
    public void PauseAll()
    {
        foreach (var item in _items.Values.Where(i => i.Status is "downloading" or "queued").ToList()) Pause(item.Id);
    }

    /// <summary>从队列里移除（顺手删掉没下完的 .part；已完成的任务只移除记录，文件不动）。</summary>
    public void Remove(long id)
    {
        if (!_items.TryRemove(id, out var item)) return;
        item.PauseRequested = false;
        try { item.Cts.Cancel(); } catch { }
        try { if (!string.IsNullOrEmpty(item.SavePath) && File.Exists(item.TempPath)) File.Delete(item.TempPath); } catch { }
        PersistQueue(true);
        Notify(true);
    }

    /// <summary>清掉已完成/失败的行（文件留着）。</summary>
    public void ClearFinished()
    {
        foreach (var item in _items.Values.Where(i => i.Status is "done" or "failed").ToList()) Remove(item.Id);
    }

    /// <summary>给前端的队列快照。</summary>
    public List<object> Snapshot() => _items.Values
        .OrderBy(i => i.Status == "done" ? 1 : 0)
        .ThenBy(i => i.Seq)
        .Select(i => (object)new
        {
            id = i.Id,
            title = i.Title,
            artist = i.Artist,
            album = i.Album,
            pic = i.Pic,
            quality = i.Quality,
            qualityLabel = QualityLabel(i.Quality),
            status = i.Status,
            progress = Math.Round(i.Progress, 4),
            done = i.DoneBytes,
            total = i.TotalBytes,
            error = i.Error,
            fileName = i.FileName,
            dir = i.Dir,
            duration = i.DurationMs,
            display = i.Display
        }).ToList();

    // ================= 已下载（历史 ∩ 目录文件） =================

    private static bool IsAudio(string ext) =>
        ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".flac", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".m4a", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".ape", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 扫一遍下载目录，只把"能和下载历史按文件名对上"的文件列出来（对不上的不显示）。
    /// </summary>
    public List<object> DownloadedList()
    {
        var dir = _config.DownloadDir;
        var files = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Directory.Exists(dir))
                foreach (var f in new DirectoryInfo(dir).EnumerateFiles())
                    if (IsAudio(f.Extension)) files[f.Name] = f;
        }
        catch (Exception e) { LogManager.Warn("扫描下载目录失败: " + e.Message); }

        var list = new List<object>();
        var dropped = 0;
        foreach (var r in _store.History())
        {
            if (files.TryGetValue(r.FileName, out var fi))
            {
                list.Add(new
                {
                    id = r.Id,
                    title = r.Title,
                    artist = r.Artist,
                    album = r.Album,
                    pic = r.Pic,
                    quality = r.Quality,
                    qualityLabel = QualityLabel(r.Quality),
                    fileName = r.FileName,
                    path = fi.FullName,
                    size = fi.Length,
                    duration = r.DurationMs,
                    finishedAt = r.FinishedAt
                });
                continue;
            }
            // 历史里有、文件却不在当前目录：要么只是换过目录（原路径还在 → 留着），要么本机真没了 → 清掉这条历史
            var recorded = string.IsNullOrEmpty(r.Dir) ? "" : Path.Combine(r.Dir, r.FileName);
            if (recorded.Length == 0 || File.Exists(recorded)) continue;
            _store.RemoveHistory(r.FileName);
            dropped++;
        }
        LogManager.Log("已下载列表: 目录音频 " + files.Count + " 个，匹配上历史 " + list.Count + " 条"
            + (dropped > 0 ? "，清理失效历史 " + dropped + " 条" : ""));
        return list;
    }

    /// <summary>清除全部下载历史（只清记录，磁盘上的音乐不动）。</summary>
    public void ClearHistory()
    {
        _store.ClearHistory();
        Notify(true);
        LogManager.Log("下载历史已清除（文件未删除）");
    }

    /// <summary>删除已下载的音乐文件（移入回收站）+ 清掉历史记录。</summary>
    public (bool Ok, string Message) DeleteDownloaded(string fileName)
    {
        try
        {
            var name = Path.GetFileName(fileName ?? "");
            if (string.IsNullOrEmpty(name)) return (false, "文件名无效");
            var path = Path.Combine(_config.DownloadDir, name);
            if (!File.Exists(path)) { _store.RemoveHistory(name); Notify(true); return (false, "文件不存在"); }
            if (!RecycleBin.Delete(path)) return (false, "移入回收站失败");
            _store.RemoveHistory(name);
            Notify(true);
            return (true, "已移入回收站：" + name);
        }
        catch (Exception e) { LogManager.Error("删除已下载文件失败: " + e.Message); return (false, e.Message); }
    }

    // ================= 下载主流程 =================

    private async Task RunAsync(DownloadItem item)
    {
        var cts = item.Cts;
        try { await _gate.WaitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            if (item.Status == "queued") item.Status = "paused";
            PersistQueue(true); Notify(true);
            return;
        }

        try
        {
            if (item.Status == "paused") return;
            item.Status = "downloading";
            item.Error = "";
            PersistQueue(true);
            Notify(true);

            int br = BitRate(item.Quality);
            var url = await _client.DownloadUrl(item.Id, br);
            if (string.IsNullOrEmpty(url)) url = await FallbackPlayUrl(item.Id, br);
            if (string.IsNullOrEmpty(url))
            {
                Fail(item, item.Fee > 0 ? "VIP 歌曲需登录 VIP 账号才能下载完整" : "未获取到下载地址（可能需要登录）");
                return;
            }

            // 文件名只在第一次取到直链时定；续传沿用旧名，否则接不上 .part
            if (string.IsNullOrEmpty(item.FileName))
            {
                var ext = url.Contains(".flac", StringComparison.OrdinalIgnoreCase) ? ".flac" : ".mp3";
                var baseName = Sanitize(NameFor(item.Title, item.Artist));
                Directory.CreateDirectory(item.Dir);
                var path = Path.Combine(item.Dir, baseName + ext);
                int n = 1;
                while (File.Exists(path) || File.Exists(path + ".part")) path = Path.Combine(item.Dir, baseName + "_" + (n++) + ext);
                item.FileName = Path.GetFileName(path);
            }
            item.SavePath = Path.Combine(item.Dir, item.FileName);
            var tmp = item.TempPath;

            long start = 0;
            try { if (File.Exists(tmp)) start = new FileInfo(tmp).Length; } catch { }
            item.DoneBytes = start;

            var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (start > 0)
            {
                req.Headers.Range = new RangeHeaderValue(start, null);
                LogManager.Log("[续传] " + item.FileName + " 从 " + start + " 字节继续");
            }
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (start > 0 && resp.StatusCode != HttpStatusCode.PartialContent)
            {
                LogManager.Debug("服务器不支持 Range，改为整首重下: " + item.FileName);
                start = 0;
                item.DoneBytes = 0;
            }
            if (resp.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
                throw new HttpRequestException("HTTP " + (int)resp.StatusCode);

            long remain = resp.Content.Headers.ContentLength ?? 0;
            item.TotalBytes = remain > 0 ? start + remain : 0;

            using (var stream = await resp.Content.ReadAsStreamAsync())
            using (var fs = new FileStream(tmp, start > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buf = new byte[1024 * 64];
                int read;
                while ((read = await stream.ReadAsync(buf, cts.Token)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, read), cts.Token);
                    item.DoneBytes += read;
                    PersistQueue(false);   // 落盘带节流：断在这里也不丢进度
                    Notify(false);
                }
                await fs.FlushAsync(cts.Token);
            }

            if (item.TotalBytes > 0 && item.DoneBytes < item.TotalBytes)
                throw new IOException("下载不完整（" + item.DoneBytes + "/" + item.TotalBytes + " 字节）");

            File.Move(tmp, item.SavePath, true);
            if (item.TotalBytes > 0) item.DoneBytes = item.TotalBytes;
            item.Status = "done";
            EnsureHistory(item);
            PersistQueue(true);
            Notify(true);
            LogManager.Log("下载完成: " + item.SavePath);
            Completed?.Invoke(item);
        }
        catch (OperationCanceledException)
        {
            item.Status = "paused";   // 暂停/取消都保留 .part，随时能继续
            PersistQueue(true);
            Notify(true);
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
        item.Status = "failed";
        item.Error = msg;
        PersistQueue(true);
        Notify(true);
        LogManager.Warn("下载失败 id=" + item.Id + " : " + msg);
        Failed?.Invoke(item);
    }

    // ================= 历史 / 队列落盘 =================

    private void EnsureHistory(DownloadItem item)
    {
        try
        {
            long size = 0;
            try { if (File.Exists(item.SavePath)) size = new FileInfo(item.SavePath).Length; } catch { }
            _store.AddHistory(new DownloadRecord
            {
                Id = item.Id,
                Title = item.Title,
                Artist = item.Artist,
                Album = item.Album,
                Pic = item.Pic,
                Quality = item.Quality,
                FileName = item.FileName,
                Dir = item.Dir,
                SizeBytes = size,
                DurationMs = item.DurationMs,
                FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm")
            });
        }
        catch (Exception e) { LogManager.Debug("写下载历史失败: " + e.Message); }
    }

    private void PersistQueue(bool force)
    {
        try
        {
            _store.SaveQueue(_items.Values
                .Where(i => i.Status != "done")
                .OrderBy(i => i.Seq)
                .Select(i => new DownloadTaskState
                {
                    Id = i.Id, Title = i.Title, Artist = i.Artist, Album = i.Album, Pic = i.Pic,
                    Quality = i.Quality, Status = i.Status, Done = i.DoneBytes, Total = i.TotalBytes,
                    FileName = i.FileName, Dir = i.Dir, Error = i.Error
                }), force);
        }
        catch (Exception e) { LogManager.Debug("保存下载队列失败: " + e.Message); }
    }

    /// <summary>启动时恢复队列：未完成的一律先置为「已暂停」，等用户点继续/开始全部。</summary>
    private void RestoreFromStore()
    {
        try
        {
            var restored = 0;
            foreach (var st in _store.LoadQueue())
            {
                if (st.Id <= 0) continue;
                var item = new DownloadItem
                {
                    Id = st.Id, Title = st.Title, Artist = st.Artist, Album = st.Album, Pic = st.Pic,
                    Quality = st.Quality, Dir = st.Dir, FileName = st.FileName,
                    TotalBytes = st.Total, Error = st.Error,
                    Seq = Interlocked.Increment(ref _seq)
                };
                item.SavePath = string.IsNullOrEmpty(st.FileName) ? "" : Path.Combine(st.Dir, st.FileName);

                if (!string.IsNullOrEmpty(item.SavePath) && File.Exists(item.SavePath))
                {
                    // 文件其实已经下完了（上次完成时没记上历史）→ 补历史，不再进队列
                    try { item.DoneBytes = new FileInfo(item.SavePath).Length; item.TotalBytes = item.DoneBytes; } catch { }
                    item.Status = "done";
                    EnsureHistory(item);
                    continue;
                }
                long part = 0;
                try { if (!string.IsNullOrEmpty(item.SavePath) && File.Exists(item.TempPath)) part = new FileInfo(item.TempPath).Length; } catch { }
                item.DoneBytes = part;
                item.Status = "paused";
                _items[st.Id] = item;
                restored++;
            }
            if (restored > 0) LogManager.Log("下载队列已恢复: " + restored + " 项（已置为暂停，可继续）");
            PersistQueue(true);
        }
        catch (Exception e) { LogManager.Error("恢复下载队列失败: " + e.Message); }
    }

    private void Notify(bool force)
    {
        var now = DateTime.Now;
        if (!force && (now - _lastNotify).TotalMilliseconds < 250) return;   // 进度回调很密，节流后再推前端
        _lastNotify = now;
        try { Changed?.Invoke(); } catch { }
    }
}
