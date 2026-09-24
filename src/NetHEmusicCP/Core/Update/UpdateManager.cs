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
    /// <summary>这个结果是从哪个源拿到的：GitHub / Gitee。</summary>
    public string Source { get; set; } = "";
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

    /// <summary>一个更新源。GitHub 和 Gitee 的接口不一样，这里包一层。</summary>
    private sealed class ReleaseSource
    {
        public string Name = "";
        public bool IsGitee;
    }

    /// <summary>两个源的尝试顺序：国内优先 Gitee，海外优先 GitHub；写死了 source 就按写的来。</summary>
    private List<ReleaseSource> SourcesInOrder()
    {
        var gh = new ReleaseSource { Name = "GitHub", IsGitee = false };
        var gt = new ReleaseSource { Name = "Gitee", IsGitee = true };
        switch (_config.UpdateSource)
        {
            case "github": return new List<ReleaseSource> { gh, gt };
            case "gitee": return new List<ReleaseSource> { gt, gh };
            default: return GeoHint.InChina() ? new List<ReleaseSource> { gt, gh } : new List<ReleaseSource> { gh, gt };
        }
    }

    /// <summary>问一个源要最新版本；失败返回 null（不抛）。</summary>
    private async Task<UpdateCheckResult?> CheckSourceAsync(ReleaseSource s, CancellationToken ct)
    {
        try
        {
            if (!s.IsGitee)
            {
                var url = $"https://api.github.com/repos/{Repo}/releases/latest";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                req.Headers.UserAgent.ParseAdd("netHEmusic/" + Version);
                using var resp = await _http.SendAsync(req, ct);
                resp.EnsureSuccessStatusCode();
                var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct)).RootElement;
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
                return new UpdateCheckResult { Latest = tag, Body = body, DownloadUrl = url2, Sha256 = sha, AssetName = asset, Source = s.Name };
            }

            // Gitee：/releases/latest 给 tag；附件要另开一个接口列（它没有 assets 字段），
            // 下载直链是 https://gitee.com/{owner}/{repo}/releases/download/{tag}/{文件名}
            var repo = _config.UpdateGiteeRepo;
            using (var req = new HttpRequestMessage(HttpMethod.Get, $"https://gitee.com/api/v5/repos/{repo}/releases/latest"))
            {
                req.Headers.UserAgent.ParseAdd("netHEmusic/" + Version);
                using var resp = await _http.SendAsync(req, ct);
                resp.EnsureSuccessStatusCode();
                var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct)).RootElement;
                var tag = doc.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                var body = doc.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
                var id = doc.TryGetProperty("id", out var i) && i.TryGetInt64(out var iv) ? iv : 0;
                var asset = "";
                if (id > 0)
                {
                    try
                    {
                        using var areq = new HttpRequestMessage(HttpMethod.Get, $"https://gitee.com/api/v5/repos/{repo}/releases/{id}/attach_files");
                        areq.Headers.UserAgent.ParseAdd("netHEmusic/" + Version);
                        using var aresp = await _http.SendAsync(areq, ct);
                        if (aresp.IsSuccessStatusCode)
                        {
                            var arr = JsonDocument.Parse(await aresp.Content.ReadAsStringAsync(ct)).RootElement;
                            foreach (var a in arr.EnumerateArray())
                            {
                                var title = a.TryGetProperty("title", out var tt) ? tt.GetString() ?? "" : "";
                                if (title.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && title.Contains(AssetPrefix)) { asset = title; break; }
                            }
                        }
                    }
                    catch (Exception e) { LogManager.Debug("Gitee 附件列表取不到: " + e.Message); }
                }
                if (asset.Length == 0) asset = $"{AssetPrefix}_{tag}.exe";   // 列不出来就按命名约定拼
                var dl = tag.Length > 0 ? $"https://gitee.com/{repo}/releases/download/{tag}/{asset}" : "";
                // Gitee 不给 sha256（GitHub 的 digest 字段它没有），所以这里 Sha256 留空，下载时跳过校验
                return new UpdateCheckResult { Latest = tag, Body = body, DownloadUrl = dl, AssetName = asset, Source = s.Name };
            }
        }
        catch (Exception e)
        {
            LogManager.Debug($"[{s.Name}] 检查更新失败: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// 检查更新：两个源都问一遍，谁给出的版本号新就用谁。
    /// 之所以不「问到第一个就返回」，是因为 Gitee 的 Release 是手动传的，可能落后于 GitHub，
    /// 只认首选源的话国内用户会一直看不到新版本。多一个请求换准确，值。
    /// </summary>
    public async Task<UpdateCheckResult> Check()
    {
        var r = new UpdateCheckResult { Current = Version };
        UpdateCheckResult? best = null;
        foreach (var s in SourcesInOrder())
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var one = await CheckSourceAsync(s, cts.Token);
            if (one is null) continue;
            if (best is null || CompareVersion(one.Latest, best.Latest) > 0) best = one;
        }
        if (best is null)
        {
            LogManager.Error("检查更新失败: GitHub 和 Gitee 都没取到版本");
            r.Error = "无法连接更新服务器（GitHub / Gitee 都试过了）";
            return r;
        }
        r.Latest = best.Latest; r.Body = best.Body; r.DownloadUrl = best.DownloadUrl;
        r.Sha256 = best.Sha256; r.AssetName = best.AssetName; r.Source = best.Source;
        r.HasUpdate = CompareVersion(best.Latest, Version) > 0;
        r.Error = "";
        LogManager.Log($"更新检查: 来源={best.Source} 最新={r.Latest} 当前={Version} 有更新={r.HasUpdate}（{GeoHint.Describe()}）");
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
        // Gitee 那边也问一下，国内拉不到 GitHub 时公告还能出得来
        if (v.Length > 0) urls.Add($"https://gitee.com/api/v5/repos/{_config.UpdateGiteeRepo}/releases/tags/{v}");
        urls.Add($"https://gitee.com/api/v5/repos/{_config.UpdateGiteeRepo}/releases/latest");

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
        // Gitee 直链不套镜像：那几个镜像都是给 GitHub 用的代理，套上去反而打不开
        if (url.Contains("gitee.com", StringComparison.OrdinalIgnoreCase)) { yield return url; yield break; }
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
