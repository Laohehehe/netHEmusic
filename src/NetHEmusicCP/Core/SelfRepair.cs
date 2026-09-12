using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using netHEmusic.Core.Config;
using netHEmusic.Core.Logging;

namespace netHEmusic.Core;

/// <summary>
/// 自修复：记录崩溃/异常，异常进入“修复报告”，可触发清理损坏缓存、重置配置等自愈步骤。
/// </summary>
public sealed class SelfRepair
{
    private readonly AppConfig _config;
    private readonly List<string> _incidents = new();

    public SelfRepair(AppConfig config) => _config = config;

    /// <summary>报告一次异常（供自修复评估）。</summary>
    public void Report(Exception ex)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} :: {ex.GetType().Name} :: {ex.Message}";
        lock (_incidents) { _incidents.Add(line); if (_incidents.Count > 50) _incidents.RemoveAt(0); }
        LogManager.Error("自修复捕获异常: " + ex);
        WriteIncident(line);
    }

    private void WriteIncident(string line)
    {
        try
        {
            var path = Path.Combine(_config.DataDir, "repair_log.txt");
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch { }
    }

    /// <summary>读取最近修复报告。</summary>
    public List<string> RecentIncidents()
    {
        try
        {
            var path = Path.Combine(_config.DataDir, "repair_log.txt");
            if (!File.Exists(path)) return new();
            return System.Linq.Enumerable.Reverse(File.ReadAllLines(path)).Take(20).ToList();
        }
        catch { return new(); }
    }

    /// <summary>清空损坏的 .part 临时文件与损坏缓存。</summary>
    public int CleanupTempParts()
    {
        int n = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(_config.CacheDir, "*.part", SearchOption.AllDirectories))
            { try { File.Delete(f); n++; } catch { } }
        }
        catch { }
        LogManager.Log("自修复清理 .part 临时文件 " + n + " 个");
        return n;
    }

    public bool NeedsRepair => RecentIncidents().Any();
}
