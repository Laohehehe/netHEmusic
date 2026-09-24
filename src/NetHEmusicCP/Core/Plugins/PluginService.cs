using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using netHEmusic.Core.Config;
using netHEmusic.Core.Logging;

namespace netHEmusic.Core.Plugins;

/// <summary>
/// 插件服务：扫描 %APPDATA%\netHEmusic\plugins 下的插件目录，
/// 读 manifest.json，把启用状态与代码下发给网页端执行。
///
/// 插件规范参考 BetterNCM（同一套 manifest 习惯），但权限是白名单制：
/// manifest 里没声明的能力，网页端一律拒绝调用。
/// </summary>
public sealed class PluginService
{
    /// <summary>所有可用的权限标识。插件在 manifest 的 permissions 里声明。</summary>
    public static readonly string[] AllPermissions =
    {
        "ui",           // 注入 CSS / 操作 DOM / 注册页面
        "events",       // 监听播放/切歌/歌词等事件
        "settings",     // 往设置页加自己的设置项
        "filesystem",   // 读写插件自己的数据目录
        "network",      // 发起网络请求（走宿主代理，绕过 CORS）
        "storage"       // 简单的键值存储（其实是 filesystem 的便捷封装）
    };

    private readonly AppConfig _config;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(25) };

    public PluginService(AppConfig config)
    {
        _config = config;
        try { _http.DefaultRequestHeaders.UserAgent.ParseAdd("netHEmusic-plugin-host"); } catch { }
    }

    /// <summary>插件根目录（不存在则创建）。</summary>
    public string PluginsDir
    {
        get
        {
            var d = Path.Combine(_config.DataDir, "plugins");
            try { Directory.CreateDirectory(d); } catch { }
            return d;
        }
    }

    /// <summary>插件市场清单地址（可改）。</summary>
    public string MarketUrl => (_config.Get("Plugins", "market_url", "") ?? "").Trim();

    // ---------------- 扫描 ----------------

    public List<object> List()
    {
        var list = new List<object>();
        try
        {
            foreach (var dir in Directory.GetDirectories(PluginsDir))
            {
                var mf = Path.Combine(dir, "manifest.json");
                if (!File.Exists(mf)) continue;
                var id = Path.GetFileName(dir);
                try
                {
                    var node = JsonNode.Parse(File.ReadAllText(mf)) as JsonObject ?? new JsonObject();
                    var injects = new List<string>();
                    if (node["injects"] is JsonObject inj)
                        foreach (var kv in inj)
                            if (kv.Value is JsonArray arr)
                                foreach (var it in arr)
                                    if (it is JsonObject o && o["file"] is JsonValue fv)
                                        injects.Add(fv.ToString());
                    var perms = new List<string>();
                    if (node["permissions"] is JsonArray pa)
                        foreach (var p in pa) perms.Add(p?.ToString() ?? "");
                    list.Add(new
                    {
                        id,
                        name = Str(node, "name", id),
                        version = Str(node, "version", "0.0.0"),
                        author = Str(node, "author", ""),
                        description = Str(node, "description", ""),
                        homepage = Str(node, "homepage", ""),
                        permissions = perms.Where(p => p.Length > 0).ToList(),
                        injects,
                        enabled = IsEnabled(id),
                        dir
                    });
                }
                catch (Exception e)
                {
                    list.Add(new { id, name = id, version = "", author = "", description = "manifest.json 解析失败: " + e.Message, permissions = new List<string>(), injects = new List<string>(), enabled = false, dir, broken = true });
                    LogManager.Warn("[插件] " + id + " manifest 解析失败: " + e.Message);
                }
            }
        }
        catch (Exception e) { LogManager.Error("[插件] 扫描失败: " + e.Message); }
        return list.OrderBy(o => o.GetType().GetProperty("id")?.GetValue(o)?.ToString()).ToList();
    }

    private static string Str(JsonObject o, string k, string def) => o[k] is JsonValue v ? (v.ToString() ?? def) : def;

    /// <summary>读取某个插件要注入的代码（按 injects 顺序拼接）。</summary>
    public string? ReadCode(string id, out string error)
    {
        error = "";
        try
        {
            if (!SafeId(id)) { error = "非法插件 id"; return null; }
            var dir = Path.Combine(PluginsDir, id);
            var mf = Path.Combine(dir, "manifest.json");
            if (!File.Exists(mf)) { error = "插件不存在"; return null; }
            var node = JsonNode.Parse(File.ReadAllText(mf)) as JsonObject ?? new JsonObject();
            var sb = new StringBuilder();
            if (node["injects"] is JsonObject inj)
                foreach (var kv in inj)
                    if (kv.Value is JsonArray arr)
                        foreach (var it in arr)
                        {
                            if (it is not JsonObject o || o["file"] is not JsonValue fv) continue;
                            var rel = fv.ToString().TrimStart('.', '/', '\\');
                            var full = Path.GetFullPath(Path.Combine(dir, rel));
                            if (!full.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)) { LogManager.Warn("[插件] " + id + " 注入路径越界: " + rel); continue; }
                            if (!File.Exists(full)) { LogManager.Warn("[插件] " + id + " 缺少注入文件: " + rel); continue; }
                            sb.Append("\n/* ==== ").Append(id).Append(" :: ").Append(rel).Append(" ==== */\n");
                            sb.Append(File.ReadAllText(full)).Append('\n');
                        }
            return sb.ToString();
        }
        catch (Exception e) { error = e.Message; return null; }
    }

    public static bool SafeId(string id) =>
        !string.IsNullOrWhiteSpace(id) && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !id.Contains("..") && !id.Contains('/') && !id.Contains('\\');

    // ---------------- 启用状态 ----------------

    public bool IsEnabled(string id) =>
        !_config.Get("Plugins", "disabled", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Contains(id, StringComparer.OrdinalIgnoreCase);

    public void SetEnabled(string id, bool on)
    {
        var set = _config.Get("Plugins", "disabled", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        set.RemoveAll(x => x.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (!on) set.Add(id);
        _config.Set("Plugins", "disabled", string.Join(",", set));
        LogManager.Log("[插件] " + id + " → " + (on ? "启用" : "停用"));
    }

    // ---------------- 插件私有数据目录 ----------------

    public string DataDirOf(string id)
    {
        var d = Path.Combine(_config.DataDir, "plugin-data", id);
        try { Directory.CreateDirectory(d); } catch { }
        return d;
    }

    public string ReadData(string id, string name, out string error)
    {
        error = "";
        try
        {
            var f = DataPath(id, name);
            return File.Exists(f) ? File.ReadAllText(f) : "";
        }
        catch (Exception e) { error = e.Message; return ""; }
    }

    public bool WriteData(string id, string name, string content, out string error)
    {
        error = "";
        try { File.WriteAllText(DataPath(id, name), content ?? ""); return true; }
        catch (Exception e) { error = e.Message; return false; }
    }

    private string DataPath(string id, string name)
    {
        if (!SafeId(id)) throw new Exception("非法插件 id");
        var safe = (name ?? "").Trim().Replace('\\', '/').TrimStart('/');
        if (safe.Length == 0 || safe.Contains("..")) throw new Exception("非法文件名");
        var full = Path.GetFullPath(Path.Combine(DataDirOf(id), safe));
        if (!full.StartsWith(Path.GetFullPath(DataDirOf(id)), StringComparison.OrdinalIgnoreCase)) throw new Exception("路径越界");
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        return full;
    }

    // ---------------- 网络代理（绕 CORS）----------------

    public async Task<(bool ok, string body, string error)> HttpAsync(string method, string url, string? body)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url) || !(url.StartsWith("http://") || url.StartsWith("https://")))
                return (false, "", "只允许 http/https");
            using var req = new HttpRequestMessage(new HttpMethod(method?.ToUpperInvariant() ?? "GET"), url);
            if (!string.IsNullOrEmpty(body)) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            return (resp.IsSuccessStatusCode, text, resp.IsSuccessStatusCode ? "" : ("HTTP " + (int)resp.StatusCode));
        }
        catch (Exception e) { return (false, "", e.Message); }
    }

    // ---------------- 插件市场 ----------------

    /// <summary>拉取市场清单（一个 JSON，格式见 plugins/market.json）。</summary>
    public async Task<(bool ok, string json, string error)> MarketAsync()
    {
        var url = MarketUrl;
        if (string.IsNullOrWhiteSpace(url)) return (false, "", "未配置插件市场地址（config.ini 的 [Plugins] market_url）");

        var links = new List<string> { url };
        // raw.githubusercontent.com 在部分网络下不通，和自动更新一样走代理镜像兜底
        if (url.Contains("raw.githubusercontent.com"))
        {
            var rest = url.Substring(url.IndexOf("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase));
            foreach (var m in new[] { "https://gh-proxy.com/https://", "https://ghfast.top/https://" })
                links.Add(m + rest);
        }

        string lastErr = "";
        foreach (var link in links)
        {
            var (ok, body, err) = await HttpAsync("GET", link, null);
            if (ok && !string.IsNullOrWhiteSpace(body))
            {
                if (link != url) LogManager.Log("[插件] 市场清单走了镜像: " + link);
                return (true, body, "");
            }
            lastErr = err;
        }
        return (false, "", lastErr.Length > 0 ? lastErr : "拉取市场清单失败");
    }

    /// <summary>从市场安装插件：下载 zip 解压到 plugins\&lt;id&gt;。url 必须是 http(s)。</summary>
    public async Task<(bool ok, string error)> InstallAsync(string id, string url)
    {
        try
        {
            if (!SafeId(id)) return (false, "非法插件 id");
            if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://")) return (false, "只允许 https 下载");
            var target = Path.Combine(PluginsDir, id);
            var tmp = Path.Combine(Path.GetTempPath(), "nethe-plugin-" + id + "-" + Guid.NewGuid().ToString("N") + ".zip");
            var bytes = await _http.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(tmp, bytes);
            var staging = Path.Combine(PluginsDir, "." + id + ".tmp");
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
            Directory.CreateDirectory(staging);
            ZipFile.ExtractToDirectory(tmp, staging, true);
            // 允许 zip 里再套一层目录
            var root = staging;
            if (!File.Exists(Path.Combine(root, "manifest.json")))
            {
                var subs = Directory.GetDirectories(staging);
                if (subs.Length == 1 && File.Exists(Path.Combine(subs[0], "manifest.json"))) root = subs[0];
            }
            if (!File.Exists(Path.Combine(root, "manifest.json"))) { Directory.Delete(staging, true); return (false, "压缩包里没有 manifest.json"); }
            try { if (Directory.Exists(target)) Directory.Delete(target, true); } catch { }
            Directory.Move(root, target);
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
            try { File.Delete(tmp); } catch { }
            LogManager.Log("[插件] 已安装: " + id);
            return (true, "");
        }
        catch (Exception e) { LogManager.Error("[插件] 安装失败 " + id + ": " + e.Message); return (false, e.Message); }
    }

    /// <summary>卸载（把插件目录移到回收站更稳妥，这里先移到 plugins\.trash）。</summary>
    public (bool ok, string error) Uninstall(string id)
    {
        try
        {
            if (!SafeId(id)) return (false, "非法插件 id");
            var dir = Path.Combine(PluginsDir, id);
            if (!Directory.Exists(dir)) return (false, "插件不存在");
            var trash = Path.Combine(PluginsDir, ".trash");
            Directory.CreateDirectory(trash);
            var dest = Path.Combine(trash, id + "-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
            Directory.Move(dir, dest);
            SetEnabled(id, false);
            LogManager.Log("[插件] 已卸载: " + id + " → " + dest);
            return (true, "");
        }
        catch (Exception e) { return (false, e.Message); }
    }
}
