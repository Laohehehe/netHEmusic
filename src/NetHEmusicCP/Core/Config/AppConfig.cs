using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using netHEmusic.Core.Logging;
using netHEmusic.Core.Model;
using netHEmusic.Core.Security;

namespace netHEmusic.Core.Config;

/// <summary>
/// 配置管理，分三个文件（各管一类数据，互不牵连）：
///   config.ini    程序/界面设置（[App] [Download] [Cache] [Player] [Update] [Network] [Plugins] [Version]）
///   player.json   播放器状态：播放队列、当前下标、续播位置、播放模式、淡化
///   downloads.json 下载历史 + 未完成队列
/// 登录 cookie 单独放 cookie.txt（RSA + AES / DPAPI 加密，禁止明文）。
/// </summary>
public sealed class AppConfig
{
    public const string AppName = "netHEmusic";

    private readonly string _dir;
    private readonly string _iniPath;
    private readonly string _cookiePath;
    private readonly string _playerPath;
    // 读写 config.ini 的串行锁：避免多处同时 Set 时“读-改-写”互相覆盖（曾导致 [Version] 段被写丢）
    private readonly object _ioLock = new();
    private PlayerDoc? _player;

    public AppConfig()
    {
        _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
        Directory.CreateDirectory(_dir);
        _iniPath = Path.Combine(_dir, "config.ini");
        _cookiePath = Path.Combine(_dir, "cookie.txt");
        _playerPath = Path.Combine(_dir, "player.json");
        // 初始化敏感数据保护（RSA 密钥对，私钥经 DPAPI 二次保护）
        SecureStore.Init(Path.Combine(_dir, "keys"));
        if (!File.Exists(_iniPath)) CreateDefaultIni();
        MigratePlayerFile();   // 老配置把播放列表塞在 config.ini 里 → 搬到 player.json（只做一次）
    }

    public string DataDir => _dir;
    public string IniPath => _iniPath;
    public string PlayerPath => _playerPath;

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
        d.Data["Player"] = new(StringComparer.OrdinalIgnoreCase) { ["volume"] = "100" };
        d.SectionOrder.Add("Player");
        d.Data["Update"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["repo"] = "Laohehehe/netHEmusic",
            // Gitee 镜像仓库（国内走这个）：GitHub 的 Release 附件不会自动同步过去，发版时要手动传一份
            ["gitee_repo"] = "laohehehe/netHEmusic",
            // 更新源偏好：auto = 按地区自动挑（国内优先 Gitee）；也可以写死 github / gitee
            ["source"] = "auto",
            ["mirrors"] = "gh-proxy.com;ghm.078465.xyz;ghfast.top",
            ["current_version"] = "26.9.24.31"
        };
        d.SectionOrder.Add("Update");
        d.Data["Network"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["proxy"] = "",
            // 网易云 API 服务地址（NeteaseCloudMusicApi 部署实例），接口文档见 {api_base}/docs/
            // 真实地址不在源码里：编译期由 ApiSecrets 注入（见 csproj / build.local.props）
            ["api_base"] = ""
        };
        d.SectionOrder.Add("Network");
        // 插件：市场清单地址（公开仓库，可自行改为别处）；停用的插件 id 用逗号分隔
        d.Data["Plugins"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["market_url"] = "https://raw.githubusercontent.com/netHEmusic/netHEmusic-plugins/main/plugins.json",
            ["disabled"] = ""
        };
        d.SectionOrder.Add("Plugins");
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
    /// <summary>音乐命名格式：title-artist(默认) / artist-title / title</summary>
    public string MusicNameFormat { get => Get("App", "ui_name_format", "title-artist"); set => Set("App", "ui_name_format", value); }
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
    /// <summary>网易云 API 服务地址（NeteaseCloudMusicApi 实例，文档 {base}/docs/）。</summary>
    public string ApiBase
    {
        get { var v = (Get("Network", "api_base", "") ?? "").Trim(); return string.IsNullOrEmpty(v) ? ApiSecrets.Base : v.TrimEnd('/'); }
        set => Set("Network", "api_base", (value ?? "").Trim());
    }

    public long CacheLimitMb { get { long.TryParse(Get("Cache", "limit_mb", "1024"), out var v); return Math.Max(1, v); } set => Set("Cache", "limit_mb", value); }
    public long MemLimitMb { get { long.TryParse(Get("Cache", "mem_limit_mb", "160"), out var v); return Math.Max(64, v); } set => Set("Cache", "mem_limit_mb", value); }

    public int Volume { get { int.TryParse(Get("Player", "volume", "100"), out var v); return Math.Clamp(v, 0, 100); } set => Set("Player", "volume", Math.Clamp(value, 0, 100)); }

    public string UpdateRepo => Get("Update", "repo", "Laohehehe/netHEmusic");

    /// <summary>Gitee 镜像仓库（owner/repo）。国内更新走它，GitHub 拉不到时也靠它兜底。</summary>
    public string UpdateGiteeRepo => (Get("Update", "gitee_repo", "laohehehe/netHEmusic") ?? "laohehehe/netHEmusic").Trim();

    /// <summary>更新源偏好：auto（按地区自动挑）/ github / gitee。</summary>
    public string UpdateSource => (Get("Update", "source", "auto") ?? "auto").Trim().ToLowerInvariant();
    public string[] UpdateMirrors => (Get("Update", "mirrors", "gh-proxy.com;ghm.078465.xyz;ghfast.top") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public string Proxy => Get("Network", "proxy", "");

    // ---------- 播放器状态（player.json） ----------
    // 播放列表动辄几百首，塞在 config.ini 里会让"改任意一个设置 = 重写整份配置"，所以单独放 JSON。
    private sealed class PlayerDoc
    {
        public List<Song> Queue { get; set; } = new();
        public int Index { get; set; }
        public long LastSongId { get; set; } = -1;
        public long LastPos { get; set; }
        public string Mode { get; set; } = "order";
        public string Crossfade { get; set; } = "0";
        public string Playback { get; set; } = "";
    }

    private PlayerDoc PlayerState()
    {
        lock (_ioLock) { return _player ??= JsonFile.Read(_playerPath, () => new PlayerDoc()); }
    }

    private void SavePlayer() { lock (_ioLock) { if (_player is not null) JsonFile.Write(_playerPath, _player); } }

    /// <summary>把老 config.ini [Player] 里的播放列表 / 续播 / 模式搬到 player.json，然后从 ini 里删掉这些键。</summary>
    private void MigratePlayerFile()
    {
        try
        {
            if (File.Exists(_playerPath)) return;
            var d = Load();
            if (!d.Data.TryGetValue("Player", out var p)) return;

            var doc = new PlayerDoc();
            var raw = p.TryGetValue("playlist", out var pl) ? pl : "";
            if (!string.IsNullOrWhiteSpace(raw) && raw.Trim() != "[]")
            {
                try { doc.Queue = System.Text.Json.JsonSerializer.Deserialize<List<Song>>(raw) ?? new List<Song>(); }
                catch (Exception e) { LogManager.Warn("老播放列表解析失败，已丢弃: " + e.Message); }
            }
            if (int.TryParse(p.TryGetValue("playlist_index", out var ix) ? ix : "", out var idx)) doc.Index = Math.Max(0, idx);
            if (long.TryParse(p.TryGetValue("last_song_id", out var ls) ? ls : "", out var lid)) doc.LastSongId = lid;
            if (long.TryParse(p.TryGetValue("last_pos", out var lp) ? lp : "", out var lpos)) doc.LastPos = lpos;
            if (p.TryGetValue("mode", out var md) && !string.IsNullOrWhiteSpace(md)) doc.Mode = md;
            if (p.TryGetValue("crossfade", out var cf) && !string.IsNullOrWhiteSpace(cf)) doc.Crossfade = cf;
            if (p.TryGetValue("playback", out var pb)) doc.Playback = pb;

            JsonFile.Write(_playerPath, doc);
            RemoveKeys("Player", new[] { "playlist", "playlist_index", "last_song_id", "last_pos", "playback", "mode", "crossfade" });
            LogManager.Log("配置迁移: [Player] 播放列表(" + doc.Queue.Count + " 首)/续播/模式 → player.json");
        }
        catch (Exception e) { LogManager.Error("player.json 迁移失败: " + e.Message); }
    }

    /// <summary>从 ini 里删掉若干键（用于迁移后清掉旧的大 JSON）。</summary>
    private void RemoveKeys(string section, string[] keys)
    {
        try
        {
            lock (_ioLock)
            {
                var d = Load();
                if (!d.Data.TryGetValue(section, out var m)) return;
                var any = false;
                foreach (var k in keys) any |= m.Remove(k);
                if (any) Save(d);
            }
        }
        catch (Exception e) { LogManager.Error("清理 config.ini 失败: " + e.Message); }
    }

    /// <summary>上次播放列表播到的下标（重启后恢复用）。</summary>
    public int PlaylistIndex { get => PlayerState().Index; set { PlayerState().Index = Math.Max(0, value); SavePlayer(); } }

    /// <summary>上次听到的曲目 id（-1 = 没记录）。用于下次启动续播。</summary>
    public long LastSongId { get => PlayerState().LastSongId; set { PlayerState().LastSongId = value; SavePlayer(); } }

    /// <summary>上次听到的位置（毫秒）。用于下次启动续播。</summary>
    public long LastPosition { get => PlayerState().LastPos; set { PlayerState().LastPos = Math.Max(0, value); SavePlayer(); } }

    /// <summary>播放模式：order / list / single / random。</summary>
    public string PlayMode { get => PlayerState().Mode; set { PlayerState().Mode = value; SavePlayer(); } }

    /// <summary>启停淡化（交叉淡化）秒数。</summary>
    public string Crossfade { get => PlayerState().Crossfade; set { PlayerState().Crossfade = value; SavePlayer(); } }

    public List<Song> LoadQueue() => PlayerState().Queue;
    public void SaveQueue(IReadOnlyList<Song> queue) { PlayerState().Queue = queue.ToList(); SavePlayer(); }
    public string GetPlayback() => PlayerState().Playback;
    public void SavePlayback(string json) { PlayerState().Playback = json; SavePlayer(); }

    // ---------- 登录凭据（非对称 + 混合加密；旧版 DPAPI 自动迁移） ----------
    private static readonly byte[] DpapiPrefix = Encoding.ASCII.GetBytes("DPAPI1:");

    /// <summary>登录 token/cookie 加密落盘：RSA-OAEP 包裹 AES-256-GCM 密钥（见 SecureStore）。</summary>
    public void SaveCookie(string cookie)
    {
        try
        {
            if (string.IsNullOrEmpty(cookie)) { ClearCookie(); return; }
            var blob = SecureStore.Protect(cookie);
            if (!string.IsNullOrEmpty(blob))
            {
                File.WriteAllText(_cookiePath, blob, new UTF8Encoding(false));
                LogManager.Log("登录凭据已用非对称加密保存（RSA-2048 + AES-256-GCM）");
                return;
            }
            // 兜底：非对称不可用时退回 DPAPI
            var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(cookie), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_cookiePath, Combine(DpapiPrefix, enc));
            LogManager.Log("登录凭据已用 DPAPI 保存（非对称不可用，已回退）");
        }
        catch (Exception e) { LogManager.Error("保存 cookie 失败: " + e); }
    }

    public string LoadCookie()
    {
        try
        {
            if (!File.Exists(_cookiePath)) return "";
            var raw = File.ReadAllBytes(_cookiePath);

            // 1) 新格式：NMSEC1:（非对称）
            var text = Encoding.UTF8.GetString(raw).Trim();
            if (text.StartsWith("NMSEC1:", StringComparison.Ordinal))
                return SecureStore.Unprotect(text);

            // 2) 旧格式：DPAPI1: → 解开后自动迁移到新格式
            if (raw.Length > DpapiPrefix.Length && raw.AsSpan(0, DpapiPrefix.Length).SequenceEqual(DpapiPrefix))
            {
                try
                {
                    var legacy = Encoding.UTF8.GetString(ProtectedData.Unprotect(raw[DpapiPrefix.Length..], null, DataProtectionScope.CurrentUser));
                    if (!string.IsNullOrEmpty(legacy)) { try { SaveCookie(legacy); } catch { } }
                    return legacy;
                }
                catch { return ""; } // 换用户/机器 -> 视为未登录
            }

            // 3) 更旧的明文格式 → 迁移
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
