using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using netHEmusic.Core.Config;
using netHEmusic.Core.Logging;

namespace netHEmusic.Core.Download;

/// <summary>一条已完成的下载记录。和磁盘上的文件靠 FileName 对上（文件名是程序自己生成的）。</summary>
public sealed class DownloadRecord
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string Pic { get; set; } = "";
    public string Quality { get; set; } = "";      // standard / exhigh / lossless
    public string FileName { get; set; } = "";     // 含扩展名，用于和磁盘文件匹配
    public string Dir { get; set; } = "";          // 下载当时的目录（换目录后仍能按 FileName 找回）
    public long SizeBytes { get; set; }
    public long DurationMs { get; set; }
    public string FinishedAt { get; set; } = "";   // 2026-09-25 19:20
}

/// <summary>未完成任务的状态快照（队列落盘用）。</summary>
public sealed class DownloadTaskState
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string Pic { get; set; } = "";
    public string Quality { get; set; } = "high";
    public string Status { get; set; } = "queued";   // queued / downloading / paused / done / failed
    public long Done { get; set; }
    public long Total { get; set; }
    public string FileName { get; set; } = "";
    public string Dir { get; set; } = "";
    public string Error { get; set; } = "";
}

public sealed class DownloadDoc
{
    public List<DownloadTaskState> Queue { get; set; } = new();
    public List<DownloadRecord> History { get; set; } = new();
}

/// <summary>
/// downloads.json：下载队列（未完成）+ 下载历史（已完成）。
/// 进度回调每秒可能来几十次，所以写盘做了节流（force 才立刻落盘）。
/// </summary>
public sealed class DownloadStore
{
    private readonly AppConfig _config;
    private readonly string _path;
    private readonly object _lock = new();
    private readonly DownloadDoc _doc;
    private DateTime _lastSave = DateTime.MinValue;
    private const int SaveIntervalMs = 1500;

    public DownloadStore(AppConfig config)
    {
        _config = config;
        _path = Path.Combine(config.DataDir, "downloads.json");
        _doc = JsonFile.Read(_path, () => new DownloadDoc());
        LogManager.Log("下载记录已加载: 队列 " + _doc.Queue.Count + " 项 / 历史 " + _doc.History.Count + " 条");
    }

    public string FilePath => _path;

    private void Save(bool force)
    {
        lock (_lock)
        {
            if (!force && (DateTime.Now - _lastSave).TotalMilliseconds < SaveIntervalMs) return;
            _lastSave = DateTime.Now;
            JsonFile.Write(_path, _doc);
        }
    }

    // ---------- 队列 ----------
    public List<DownloadTaskState> LoadQueue()
    {
        lock (_lock) { return _doc.Queue.Select(Clone).ToList(); }
    }

    public void SaveQueue(IEnumerable<DownloadTaskState> tasks, bool force = false)
    {
        lock (_lock) { _doc.Queue = tasks.Select(Clone).ToList(); }
        Save(force);
    }

    // ---------- 历史 ----------
    public List<DownloadRecord> History()
    {
        lock (_lock) { return _doc.History.Select(Clone).ToList(); }
    }

    /// <summary>新增/更新一条历史（同一个文件名只留一条）。</summary>
    public void AddHistory(DownloadRecord rec)
    {
        lock (_lock)
        {
            _doc.History.RemoveAll(r => string.Equals(r.FileName, rec.FileName, StringComparison.OrdinalIgnoreCase));
            _doc.History.Insert(0, Clone(rec));
        }
        Save(true);
    }

    public bool RemoveHistory(string fileName)
    {
        bool removed;
        lock (_lock)
        {
            removed = _doc.History.RemoveAll(r => string.Equals(r.FileName, fileName, StringComparison.OrdinalIgnoreCase)) > 0;
        }
        if (removed) Save(true);
        return removed;
    }

    public void ClearHistory()
    {
        lock (_lock) { _doc.History.Clear(); }
        Save(true);
    }

    private static DownloadRecord Clone(DownloadRecord r) => new()
    {
        Id = r.Id, Title = r.Title, Artist = r.Artist, Album = r.Album, Pic = r.Pic, Quality = r.Quality,
        FileName = r.FileName, Dir = r.Dir, SizeBytes = r.SizeBytes, DurationMs = r.DurationMs, FinishedAt = r.FinishedAt
    };

    private static DownloadTaskState Clone(DownloadTaskState t) => new()
    {
        Id = t.Id, Title = t.Title, Artist = t.Artist, Album = t.Album, Pic = t.Pic, Quality = t.Quality,
        Status = t.Status, Done = t.Done, Total = t.Total, FileName = t.FileName, Dir = t.Dir, Error = t.Error
    };
}
