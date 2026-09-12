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
}
