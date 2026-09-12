using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using netHEmusic.Core.Logging;

namespace netHEmusic.Core.Config;

/// <summary>
/// 配置管理：%APPDATA%\netHEmusic\config.ini + DPAPI 加密的登录 cookie.txt。
/// 登录 cookie 为敏感数据，经 Windows DPAPI（ProtectedData）加密落盘，禁止明文。
/// </summary>
public sealed class AppConfig
{
    public const string AppName = "netHEmusic";

    private readonly string _dir;
    private readonly string _iniPath;
    private readonly string _cookiePath;
    // 读写 config.ini 的串行锁：避免多处同时 Set 时“读-改-写”互相覆盖（曾导致 [Version] 段被写丢）
    private readonly object _ioLock = new();

    public AppConfig()
    {
        _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
        Directory.CreateDirectory(_dir);
        _iniPath = Path.Combine(_dir, "config.ini");
        _cookiePath = Path.Combine(_dir, "cookie.txt");
        if (!File.Exists(_iniPath)) CreateDefaultIni();
    }

    public string DataDir => _dir;
    public string IniPath => _iniPath;

    // ---------- INI 读写 ----------
    private sealed class IniDoc
    {
        public readonly List<string> SectionOrder = new();
        public readonly Dictionary<string, Dictionary<string, string>> Data = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, List<string>> SectionLines = new(StringComparer.OrdinalIgnoreCase);
    }

    private IniDoc Load()
    {
        var doc = new IniDoc();
        string sec = "";
        if (!File.Exists(_iniPath)) return doc;
        foreach (var raw in File.ReadAllLines(_iniPath))
        {
            var line = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) continue;
            var t = line.Trim();
            if (t.StartsWith("[") && t.EndsWith("]"))
            {
                sec = t.Trim('[', ']');
                if (!doc.Data.ContainsKey(sec)) { doc.Data[sec] = new(StringComparer.OrdinalIgnoreCase); doc.SectionOrder.Add(sec); }
            }
            else
            {
                var idx = line.IndexOf('=');
                if (idx > 0 && !string.IsNullOrEmpty(sec))
                {
                    var k = line[..idx].Trim();
                    var v = line[(idx + 1)..].Trim();
                    if (!doc.Data[sec].ContainsKey(k)) doc.Data[sec][k] = v;
                }
            }
        }
        return doc;
    }

    private void Save(IniDoc doc)
    {
        var sb = new StringBuilder();
        foreach (var sec in doc.SectionOrder)
        {
            sb.Append('[').Append(sec).AppendLine("]");
            var lines = doc.Data[sec];
            foreach (var kv in lines) sb.Append(kv.Key).Append('=').AppendLine(kv.Value);
            sb.AppendLine();
        }
        try { File.WriteAllText(_iniPath, sb.ToString(), new UTF8Encoding(false)); }
        catch (Exception e) { LogManager.Error("写 config.ini 失败: " + e); }
    }

    public string Get(string section, string key, string def = "")
    {
        try { lock (_ioLock) { var d = Load(); return d.Data.TryGetValue(section, out var m) && m.TryGetValue(key, out var v) ? v : def; } }
        catch { return def; }
    }

    /// <summary>取某分段的全部键值（供前端读取自定义设置，新增设置项无需改 C#）。</summary>
    public Dictionary<string, string> GetSection(string section)
    {
        try
        {
            lock (_ioLock)
            {
                var d = Load();
                return d.Data.TryGetValue(section, out var m)
                    ? new Dictionary<string, string>(m, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
    }

    public void Set(string section, string key, object? value)
    {
        try
        {
            lock (_ioLock)
            {
                var d = Load();
                if (!d.Data.ContainsKey(section)) { d.Data[section] = new(StringComparer.OrdinalIgnoreCase); d.SectionOrder.Add(section); }
                d.Data[section][key] = (value?.ToString() ?? "");
                Save(d);
            }
        }
        catch (Exception e) { LogManager.Error("写 config.ini 失败: " + e); }
    }

    private void CreateDefaultIni()
    {
        var d = new IniDoc();
        d.Data["App"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["first_run"] = "true", ["theme"] = "dark", ["language"] = "zh_cn",
            ["material_you"] = "false", ["scheme"] = "dark-blue", ["mica"] = "true",
            ["desktop_lyric_topmost"] = "true", ["desktop_lyric_vertical"] = "false",
            ["desktop_song_info"] = "true", ["volume"] = "100"
        };
        d.SectionOrder.Add("App");
        d.Data["Download"] = new(StringComparer.OrdinalIgnoreCase) { ["dir"] = "", ["quality"] = "high", ["concurrent"] = "3" };
        d.SectionOrder.Add("Download");
        d.Data["Cache"] = new(StringComparer.OrdinalIgnoreCase) { ["limit_mb"] = "1024", ["mem_limit_mb"] = "160", ["dir"] = "" };
        d.SectionOrder.Add("Cache");
        d.Data["Player"] = new(StringComparer.OrdinalIgnoreCase) { ["volume"] = "100", ["playlist"] = "[]", ["playback"] = "" };
        d.SectionOrder.Add("Player");
        d.Data["Update"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["repo"] = "Laohehehe/NET163download",
            ["mirrors"] = "gh-proxy.com;ghm.078465.xyz;ghfast.top",
            ["current_version"] = "26.9.12.22"
        };
        d.SectionOrder.Add("Update");
        d.Data["Network"] = new(StringComparer.OrdinalIgnoreCase) { ["proxy"] = "" };
        d.SectionOrder.Add("Network");
        // 记录“上一次已公告/已运行”的版本号：比当前版本旧就弹更新公告
        d.Data["Version"] = new(StringComparer.OrdinalIgnoreCase) { ["version"] = "0.0.0.0" };
        d.SectionOrder.Add("Version");
        Save(d);
    }

    // ---------- 便捷属性 ----------
    /// <summary>config.ini [Version] version：上次已公告过的版本（缺失视为 0.0.0.0）。</summary>
    public string VersionSeen { get => Get("Version", "version", "0.0.0.0"); set => Set("Version", "version", value); }

    public bool FirstRun { get => Get("App", "first_run", "true").Equals("true", StringComparison.OrdinalIgnoreCase); set => Set("App", "first_run", value ? "true" : "false"); }
    public string Theme { get => Get("App", "theme", "dark") is var t && (t == "light" || t == "dark") ? t : "dark"; set => Set("App", "theme", value); }
    public string Language { get => Get("App", "language", "zh_cn"); set => Set("App", "language", value); }
    public bool MaterialYou { get => Get("App", "material_you", "false").Equals("true", StringComparison.OrdinalIgnoreCase); set => Set("App", "material_you", value ? "true" : "false"); }
    public string Scheme { get => Get("App", "scheme", "dark-blue"); set => Set("App", "scheme", value); }
    public bool Mica { get => Get("App", "mica", "true").Equals("true", StringComparison.OrdinalIgnoreCase); set => Set("App", "mica", value ? "true" : "false"); }
    public bool DesktopLyricTopmost { get => Get("App", "desktop_lyric_topmost", "true").Equals("true", StringComparison.OrdinalIgnoreCase); set => Set("App", "desktop_lyric_topmost", value ? "true" : "false"); }
    public bool DesktopLyricVertical { get => Get("App", "desktop_lyric_vertical", "false").Equals("true", StringComparison.OrdinalIgnoreCase); set => Set("App", "desktop_lyric_vertical", value ? "true" : "false"); }
    public bool DesktopSongInfo { get => Get("App", "desktop_song_info", "true").Equals("true", StringComparison.OrdinalIgnoreCase); set => Set("App", "desktop_song_info", value ? "true" : "false"); }

    public string DownloadDir
    {
        get
        {
            var d = Get("Download", "dir", "");
            if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) return d;
            // 默认：系统“音乐”文件夹下新建 netHemusicDL
            var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            if (string.IsNullOrEmpty(music)) music = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var def = Path.Combine(music, "netHemusicDL");
            try { Directory.CreateDirectory(def); } catch { }
            return def;
        }
        set { try { Directory.CreateDirectory(value); } catch { } Set("Download", "dir", value); }
    }

    public string Quality { get => Get("Download", "quality", "high"); set => Set("Download", "quality", value); }
    public int ConcurrentDownloads { get { int.TryParse(Get("Download", "concurrent", "3"), out var v); return Math.Max(1, v); } set => Set("Download", "concurrent", value); }

    public string CacheDir
    {
        get
        {
            var d = Get("Cache", "dir", "");
            if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) return d;
            var def = Path.Combine(_dir, "temp");
            try { Directory.CreateDirectory(def); } catch { }
            return def;
        }
        set { try { Directory.CreateDirectory(value); } catch { } Set("Cache", "dir", value); }
    }
    public long CacheLimitMb { get { long.TryParse(Get("Cache", "limit_mb", "1024"), out var v); return Math.Max(1, v); } set => Set("Cache", "limit_mb", value); }
    public long MemLimitMb { get { long.TryParse(Get("Cache", "mem_limit_mb", "160"), out var v); return Math.Max(64, v); } set => Set("Cache", "mem_limit_mb", value); }

    public int Volume { get { int.TryParse(Get("Player", "volume", "100"), out var v); return Math.Clamp(v, 0, 100); } set => Set("Player", "volume", Math.Clamp(value, 0, 100)); }

    public string UpdateRepo => Get("Update", "repo", "Laohehehe/NET163download");
    public string[] UpdateMirrors => (Get("Update", "mirrors", "gh-proxy.com;ghm.078465.xyz;ghfast.top") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public string Proxy => Get("Network", "proxy", "");

    // ---------- 播放状态 ----------
    public string GetPlaylist() => Get("Player", "playlist", "[]");
    public void SavePlaylist(string json) => Set("Player", "playlist", json);
    public string GetPlayback() => Get("Player", "playback", "");
    public void SavePlayback(string json) => Set("Player", "playback", json);

    // ---------- Cookie（DPAPI 加密） ----------
    private static readonly byte[] DpapiPrefix = Encoding.ASCII.GetBytes("DPAPI1:");

    public void SaveCookie(string cookie)
    {
        try
        {
            if (string.IsNullOrEmpty(cookie)) { ClearCookie(); return; }
            var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(cookie), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_cookiePath, Combine(DpapiPrefix, enc));
            LogManager.Log("登录 cookie 已加密保存");
        }
        catch (Exception e) { LogManager.Error("保存 cookie 失败: " + e); }
    }

    public string LoadCookie()
    {
        try
        {
            if (!File.Exists(_cookiePath)) return "";
            var raw = File.ReadAllBytes(_cookiePath);
            if (raw.Length > DpapiPrefix.Length && raw.AsSpan(0, DpapiPrefix.Length).SequenceEqual(DpapiPrefix))
            {
                var payload = raw[DpapiPrefix.Length..];
                try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(payload, null, DataProtectionScope.CurrentUser)); }
                catch { return ""; } // 换用户/机器 -> 视为未登录
            }
            // 旧版明文：自动迁移为加密
            var text = Encoding.UTF8.GetString(raw).Trim();
            if (!string.IsNullOrEmpty(text)) { try { SaveCookie(text); } catch { } }
            return text;
        }
        catch { return ""; }
    }

    public void ClearCookie()
    {
        try { if (File.Exists(_cookiePath)) File.Delete(_cookiePath); } catch { }
    }

    private static byte[] Combine(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, r, 0, a.Length);
        Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
        return r;
    }
}
