using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using netHEmusic.Core.Config;
using netHEmusic.Core.Logging;

namespace netHEmusic.Core.Cache;

/// <summary>
/// 磁盘缓存管理：%APPDATA%\netHEmusic\temp（默认 1GB，可改）。超限自动清理最旧缓存。
/// </summary>
public sealed class CacheManager
{
    private readonly AppConfig _config;
    public CacheManager(AppConfig config) => _config = config;

    public string CacheDir => _config.CacheDir;
    public long LimitMb => _config.CacheLimitMb;

    public void EnsureDir() => Directory.CreateDirectory(CacheDir);

    /// <summary>当前缓存占用（MB）。</summary>
    public double SizeMb()
    {
        try { return Directory.EnumerateFiles(CacheDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / (1024.0 * 1024.0); }
        catch { return 0; }
    }

    /// <summary>缓存是否超限，超限则清理最旧文件（保留最近使用）。</summary>
    public void EnforceLimit()
    {
        try
        {
            EnsureDir();
            long limitBytes = LimitMb * 1024 * 1024;
            while (SizeMb() * 1024 * 1024 > limitBytes)
            {
                var oldest = Directory.EnumerateFiles(CacheDir, "*", SearchOption.AllDirectories)
                    .OrderBy(f => File.GetLastAccessTimeUtc(f))
                    .FirstOrDefault();
                if (oldest is null) break;
                try { File.Delete(oldest); LogManager.Debug("缓存清理: " + oldest); }
                catch { break; }
            }
        }
        catch (Exception e) { LogManager.Error("缓存清理失败: " + e); }
    }

    /// <summary>清理所有缓存文件。</summary>
    public void Clear()
    {
        try { foreach (var f in Directory.EnumerateFiles(CacheDir, "*", SearchOption.AllDirectories)) try { File.Delete(f); } catch { } } catch { }
    }

    public string CachePath(string key, string ext = ".tmp")
    {
        EnsureDir();
        return Path.Combine(CacheDir, key + ext);
    }

    // ================= 页面数据缓存（列表/搜索结果等）=================
    // 思路：数据放磁盘（temp 目录），页面打开时读回内存，离开页面就把内存里的副本丢掉。

    /// <summary>写入一段文本缓存；写完按上限清理。key 会被合法化，避免路径穿越。</summary>
    public void WriteText(string key, string text)
    {
        try
        {
            EnsureDir();
            File.WriteAllText(CachePath(SafeKey(key), ".json"), text ?? "", new System.Text.UTF8Encoding(false));
            EnforceLimit();
        }
        catch (Exception e) { LogManager.Debug("写缓存失败 " + key + ": " + e.Message); }
    }

    /// <summary>读缓存；没有则返回 null。顺手刷新访问时间，便于按 LRU 清理旧文件。</summary>
    public string? ReadText(string key)
    {
        try
        {
            var path = CachePath(SafeKey(key), ".json");
            if (!File.Exists(path)) return null;
            var txt = File.ReadAllText(path);
            try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch { }
            return txt;
        }
        catch (Exception e) { LogManager.Debug("读缓存失败 " + key + ": " + e.Message); return null; }
    }

    /// <summary>删掉某个缓存文件。</summary>
    public bool Remove(string key)
    {
        try
        {
            var path = CachePath(SafeKey(key), ".json");
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch { return false; }
    }

    private static string SafeKey(string key)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in (key ?? ""))
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_');
        var s = sb.ToString();
        if (s.Length == 0) return "k";
        return s.Length > 80 ? s.Substring(0, 80) : s;
    }

    // ================= 离线播放：音频缓存 =================
    // 与页面数据缓存共用同一个 temp 目录与「缓存上限」设置（EnforceLimit 已经按 LRU 覆盖全部文件），
    // 文件名形如 audio\<歌曲id>.mp3；播放时本地有就直接走本地（虚拟主机 https://mediacache/），
    // 没有就用在线直链并在后台存一份，下次断网也能听。
    public string AudioDir
    {
        get { var d = Path.Combine(CacheDir, "audio"); try { Directory.CreateDirectory(d); } catch { } return d; }
    }

    /// <summary>已缓存的音频文件路径（没有则 null）。</summary>
    public string? FindAudio(long songId)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(AudioDir, songId + ".*"))
                if (new FileInfo(f).Length > 64 * 1024) return f;   // 太小的当坏文件忽略
        }
        catch { }
        return null;
    }

    /// <summary>已经缓存的歌曲 id 列表（前端用来把没缓存的歌在断网时置灰）。</summary>
    public List<long> AudioIds()
    {
        var list = new List<long>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(AudioDir, "*"))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (long.TryParse(name, out var id) && new FileInfo(f).Length > 64 * 1024) list.Add(id);
            }
        }
        catch { }
        return list;
    }

    /// <summary>给网页用的本地播放地址（InitWebView 里把 mediacache 映射到 AudioDir）。</summary>
    public string AudioVirtualUrl(long songId, string ext)
        => "https://mediacache/audio/" + songId + (string.IsNullOrEmpty(ext) ? ".mp3" : ext);

    /// <summary>后台把在线直链存一份到本地缓存（失败静默；前端播放不受影响）。</summary>
    public async System.Threading.Tasks.Task CacheAudioAsync(string remoteUrl, long songId)
    {
        try
        {
            if (songId <= 0 || string.IsNullOrEmpty(remoteUrl) || remoteUrl.StartsWith("https://mediacache/")) return;
            if (FindAudio(songId) is not null) return;
            var ext = ".mp3";
            try { var p = new Uri(remoteUrl).AbsolutePath; var e = Path.GetExtension(p); if (e.Length is >= 2 and <= 5) ext = e; } catch { }
            var tmp = Path.Combine(AudioDir, songId + ".part");
            var dst = Path.Combine(AudioDir, songId + ext);
            using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            using (var resp = await http.GetAsync(remoteUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead))
            {
                if (!resp.IsSuccessStatusCode) { LogManager.Debug("缓存音频失败(HTTP " + (int)resp.StatusCode + ") id=" + songId); return; }
                await using (var src = await resp.Content.ReadAsStreamAsync())
                await using (var fs = File.Create(tmp))
                    await src.CopyToAsync(fs);
            }
            if (new FileInfo(tmp).Length < 64 * 1024) { try { File.Delete(tmp); } catch { } return; }
            File.Move(tmp, dst, true);
            LogManager.Log("已缓存音频（可离线播放）: " + Path.GetFileName(dst) + " " + (new FileInfo(dst).Length / 1024 / 1024) + " MB");
            EnforceLimit();
        }
        catch (Exception e) { LogManager.Debug("缓存音频异常 id=" + songId + ": " + e.Message); }
    }
}
