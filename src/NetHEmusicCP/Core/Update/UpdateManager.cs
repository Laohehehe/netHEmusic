using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using netHEmusic.Core.Config;
using netHEmusic.Core.Logging;

namespace netHEmusic.Core.Update;

/// <summary>更新流程检查结果。</summary>
public class UpdateCheckResult
{
    public string Current { get; set; } = "";
    public string Latest { get; set; } = "";
    public bool HasUpdate { get; set; }
    public string DownloadUrl { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string AssetName { get; set; } = "";
    public string Body { get; set; } = "";
    public string Error { get; set; } = "";
}

/// <summary>下载状态（供进度轮询）。</summary>
public class UpdateState
{
    public string Status { get; set; } = "idle"; // idle|downloading|done|error
    public double Progress { get; set; }
    public string Version { get; set; } = "";
    public string Dest { get; set; } = "";
    public string Error { get; set; } = "";
    public string Asset { get; set; } = "";
}

/// <summary>
/// 自动更新：GitHub Release 检查 → 镜像多线程下载 → SHA-256 校验 → 启动安装包。
/// 与旧 updater.py 等价；更新对象为 Inno Setup 安装包（netHEmusic_Setup_*.exe）。
/// </summary>
public sealed class UpdateManager
{
    private readonly AppConfig _config;
    private readonly HttpClient _http;
    public const string AssetPrefix = "netHEmusic_Setup";
    private readonly object _lock = new();
    private readonly UpdateState _state = new();

    public UpdateManager(AppConfig config)
    {
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("netHEmusic/" + Version);
    }

    public string Version => "26.9.24.11";
    private string Repo => _config.UpdateRepo;
    public UpdateState State { get { lock (_lock) return _state; } }

    public async Task<UpdateCheckResult> Check()
    {
        var r = new UpdateCheckResult { Current = Version };
        try
        {
            var url = $"https://api.github.com/repos/{Repo}/releases/latest";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            req.Headers.UserAgent.ParseAdd("netHEmusic/" + Version);
            using var resp = await _http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
            var tag = doc.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var body = doc.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            string url2 = "", sha = "", asset = "";
            if (doc.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && name.Contains(AssetPrefix))
                    {
                        url2 = a.TryGetProperty("browser_download_url", out var bu) ? bu.GetString() ?? "" : "";
                        var digest = a.TryGetProperty("digest", out var dg) ? dg.GetString() ?? "" : "";
                        if (digest.StartsWith("sha256:")) sha = digest[7..].ToLowerInvariant();
                        asset = name;
                        break;
                    }
                }
            r.Latest = tag; r.Body = body; r.DownloadUrl = url2; r.Sha256 = sha; r.AssetName = asset;
            r.HasUpdate = CompareVersion(tag, Version) > 0;
            r.Error = "";
        }
        catch (Exception e)
        {
            LogManager.Error("检查更新失败: " + e.Message);
            r.Error = "无法连接更新服务器: " + e.Message;
        }
        return r;
    }

    /// <summary>
    /// 取某个版本的 Release 正文（用于「软件已更新」公告）。
    /// 先按 tag 精确取，取不到就退回最新 Release；两者都失败则返回错误信息。
    /// </summary>
    public async Task<(string Tag, string Body, string Error)> FetchReleaseNotesAsync(string version)
    {
        var urls = new List<string>();
        var v = (version ?? "").Trim();
        if (v.Length > 0) urls.Add($"https://api.github.com/repos/{Repo}/releases/tags/{v}");
        urls.Add($"https://api.github.com/repos/{Repo}/releases/latest");

        foreach (var url in urls)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                req.Headers.UserAgent.ParseAdd("netHEmusic/" + Version);
                using var resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) continue;
                var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
                var tag = doc.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                var body = doc.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(body)) return (tag, body, "");
            }
            catch (Exception e) { LogManager.Debug("取 Release 正文失败(" + url + "): " + e.Message); }
        }
        return ("", "", "无法连接更新服务器");
    }

    private static int CompareVersion(string a, string b) => VersionTuple(a).CompareTo(VersionTuple(b));

    private static (int, int, int, int) VersionTuple(string v)
    {
        try
        {
            var p = (v ?? "").TrimStart('v', 'V').Split('.');
            int a = p.Length > 0 && int.TryParse(p[0], out var x) ? x : 0;
            int b = p.Length > 1 && int.TryParse(p[1], out var y) ? y : 0;
            int c = p.Length > 2 && int.TryParse(p[2], out var z) ? z : 0;
            int d = p.Length > 3 && int.TryParse(p[3], out var w) ? w : 0;
            return (a, b, c, d);
        }
        catch { return (0, 0, 0, 0); }
    }

    private IEnumerable<string> BuildDownloadUrls(string url)
    {
        foreach (var m in _config.UpdateMirrors) yield return $"https://{m}/{url}";
        yield return url;
    }

    public bool StartDownload(string version, string url, string sha, string asset)
    {
        lock (_lock)
        {
            if (_state.Status == "downloading") return false;
            _state.Status = "downloading"; _state.Progress = 0; _state.Version = version; _state.Error = ""; _state.Asset = asset;
        }
        _ = Task.Run(() => DownloadTask(version, url, sha, asset));
        return true;
    }

    private async Task DownloadTask(string version, string url, string sha, string asset)
    {
        var dest = Path.Combine(Path.GetTempPath(), "netHEmusic_Setup_" + version + ".exe");
        lock (_lock) { _state.Dest = dest; }
        string lastErr = "";
        foreach (var link in BuildDownloadUrls(url))
        {
            try
            {
                if (File.Exists(dest)) File.Delete(dest);
                await DownloadSingle(link, dest, p => { lock (_lock) _state.Progress = p; });
                await VerifySha256(dest, sha, url);
                lock (_lock) { _state.Status = "done"; _state.Progress = 1.0; }
                LogManager.Log("更新下载完成: " + dest);
                return;
            }
            catch (Exception e)
            {
                lastErr = e.Message; LogManager.Warn("下载失败 (" + link + "): " + e.Message);
                try { if (File.Exists(dest)) File.Delete(dest); } catch { }
            }
        }
        lock (_lock) { _state.Status = "error"; _state.Error = lastErr; }
        LogManager.Error("更新下载失败: " + lastErr);
    }

    private async Task DownloadSingle(string url, string dest, Action<double> cb)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;
        using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
        long done = 0; var buf = new byte[1024 * 512];
        await using var s = await resp.Content.ReadAsStreamAsync();
        int n;
        while ((n = await s.ReadAsync(buf)) > 0)
        {
            await fs.WriteAsync(buf.AsMemory(0, n));
            done += n;
            if (total > 0) cb((double)done / total);
        }
        cb(1.0);
    }

    private async Task VerifySha256(string dest, string expected, string downloadUrl)
    {
        if (string.IsNullOrEmpty(expected)) { LogManager.Debug("无 SHA-256 校验信息，跳过完整性校验"); return; }
        using var sha256 = SHA256.Create();
        string actual;
        using (var fs = File.OpenRead(dest)) actual = Convert.ToHexString(await sha256.ComputeHashAsync(fs)).ToLowerInvariant();
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new Exception($"SHA-256 校验失败: 期望 {expected}, 实际 {actual}");
        LogManager.Log("SHA-256 完整性校验通过");
    }

    /// <summary>启动已下载的安装包（Inno Setup GUI 安装向导）。</summary>
    public bool ApplyUpdate(out string err)
    {
        err = "";
        UpdateState st;
        lock (_lock) st = _state;
        if (st.Status != "done" || string.IsNullOrEmpty(st.Dest) || !File.Exists(st.Dest)) { err = "安装包未就绪"; return false; }
        try
        {
            ResetFromExplorer(st.Dest);
            LogManager.Log("启动安装包: " + st.Dest);
            return true;
        }
        catch (Exception e) { err = e.Message; LogManager.Error("启动安装包失败: " + e); return false; }
    }

    private static void ResetFromExplorer(string dest)
    {
        // 使用默认打开方式启动（GUI 安装向导）
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dest) { UseShellExecute = true });
    }

    /// <summary>退出前回调（当前无需特殊清理占用）。</summary>
    public void CloseInstallIfRunning() { }
}
