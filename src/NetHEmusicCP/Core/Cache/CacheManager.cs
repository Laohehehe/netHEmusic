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
}
