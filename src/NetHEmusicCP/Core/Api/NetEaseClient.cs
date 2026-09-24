using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using netHEmusic.Core.Logging;
using netHEmusic.Core.Model;

namespace netHEmusic.Core.Api;

/// <summary>网易云 API 客户端（与旧 backend/api/client.py 等价）。
/// 默认地址在编译期注入（netHEmusic.Core.ApiSecrets），源码与仓库里不含任何地址。</summary>
public sealed class NetEaseClient
{
    public const string DefaultBase = ApiSecrets.Base;
    private readonly HttpClient _http;
    private string _base;

    public NetEaseClient(string baseUrl = DefaultBase, string cookie = "", int timeout = 20)
    {
        _base = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeout) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        Cookie = cookie;
    }

    public string Cookie { get; private set; } = "";

    public void SetCookie(string cookie)
    {
        Cookie = CleanCookie(cookie ?? "");
        _http.DefaultRequestHeaders.Remove("Cookie");
        if (!string.IsNullOrEmpty(Cookie)) _http.DefaultRequestHeaders.Add("Cookie", Cookie);
    }

    /// <summary>清洗 cookie：只保留 name=value，去掉 Set-Cookie 属性（Max-Age/Expires/Path...）。</summary>
    private static string CleanCookie(string cookie)
    {
        var parts = new List<string>();
        foreach (var p in cookie.Split(';'))
        {
            var s = p.Trim();
            if (string.IsNullOrEmpty(s) || !s.Contains('=')) continue;
            var key = s.Split('=', 2)[0].Trim().ToLowerInvariant();
            if (key is "max-age" or "expires" or "path" or "domain" or "secure" or "httponly" or "samesite" or "priority") continue;
            parts.Add(s);
        }
        return string.Join("; ", parts);
    }

    private async Task<JsonElement?> Get(string path, Dictionary<string, string?>? ps = null)
    {
        var url = _base + path + (ps is { Count: > 0 } ? "?" + string.Join("&", ps.Where(kv => kv.Value != null).Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value!))) : "");
        try
        {
            var resp = await _http.GetAsync(url);
            resp.EnsureSuccessStatusCode();
            var text = await resp.Content.ReadAsStringAsync();
            return JsonDocument.Parse(text).RootElement;
        }
        catch (Exception e) { LogManager.Error("API GET 失败 " + path + " : " + e.Message); return null; }
    }

    private async Task<JsonElement?> Post(string path, Dictionary<string, string?>? ps = null)
    {
        var url = _base + path + (ps is { Count: > 0 } ? "?" + string.Join("&", ps.Where(kv => kv.Value != null).Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value!))) : "");
        try
        {
            var resp = await _http.PostAsync(url, null);
            resp.EnsureSuccessStatusCode();
            var text = await resp.Content.ReadAsStringAsync();
            return JsonDocument.Parse(text).RootElement;
        }
        catch (Exception e) { LogManager.Error("API POST 失败 " + path + " : " + e.Message); return null; }
    }

    // ---------- 搜索 ----------
    public async Task<List<Song>> Search(string keywords, int type = 1, int limit = 30)
    {
        var js = await Get("/cloudsearch", new Dictionary<string, string?> { ["keywords"] = keywords, ["type"] = type.ToString(), ["limit"] = limit.ToString() });
        if (js is null) return new();
        return js.Value.TryGetProperty("result", out var r) && r.TryGetProperty("songs", out var songs)
            ? songs.EnumerateArray().Select(s => s.Deserialize<Song>() ?? new Song()).ToList() : new();
    }

    public async Task<string> JsonSearch(string keywords, int limit = 30, int offset = 0)
    {
        var js = await Get("/cloudsearch", new Dictionary<string, string?> { ["keywords"] = keywords, ["type"] = "1", ["limit"] = limit.ToString(), ["offset"] = offset.ToString() });
        return js?.GetRawText() ?? "{}";
    }

    // ---------- 歌曲 ----------
    public async Task<List<Song>> SongDetail(string ids)
    {
        var js = await Get("/song/detail", new Dictionary<string, string?> { ["ids"] = ids });
        return js is not null && js.Value.TryGetProperty("songs", out var songs)
            ? songs.EnumerateArray().Select(s => s.Deserialize<Song>() ?? new Song()).ToList() : new();
    }

    public async Task<Dictionary<string, string>> SongUrl(string ids, int br = 320000)
    {
        var js = await Get("/song/url", new Dictionary<string, string?> { ["id"] = ids, ["br"] = br.ToString() });
        var result = new Dictionary<string, string>();
        if (js is not null && js.Value.TryGetProperty("data", out var data))
            foreach (var it in data.EnumerateArray())
                if (it.TryGetProperty("id", out var id) && it.TryGetProperty("url", out var u)) result[id.GetRawText()] = u.GetString() ?? "";
        return result;
    }

    public async Task<string?> DownloadUrl(long id, int br = 320000)
    {
        var js = await Get("/song/download/url", new Dictionary<string, string?> { ["id"] = id.ToString(), ["br"] = br.ToString() });
        return js is not null && js.Value.TryGetProperty("data", out var data) && data.TryGetProperty("url", out var u) ? u.GetString() : null;
    }

    /// <summary>通用原始 JSON GET（供前端任意网易云 API 透传）。</summary>
    public async Task<string> GetRaw(string path, Dictionary<string, string?>? ps = null)
        => (await Get(path, ps))?.GetRawText() ?? "{}";

    /// <summary>通用原始 JSON POST。</summary>
    public async Task<string> PostRaw(string path, Dictionary<string, string?>? ps = null)
        => (await Post(path, ps))?.GetRawText() ?? "{}";

    public async Task<string> JsonLyric(long id)
    {
        var js = await Get("/lyric/new", new Dictionary<string, string?> { ["id"] = id.ToString() });
        return js?.GetRawText() ?? "{}";
    }

    /// <summary>解析歌词 JSON 为 LRC 行。</summary>
    public (string lrc, string tlyric, string romalrc, string yrc) ParseLyric(string json)
    {
        string lrc = "", tl = "", roma = "", yrc = "";
        try
        {
            var doc = JsonDocument.Parse(json).RootElement;
            if (doc.TryGetProperty("lrc", out var l) && l.TryGetProperty("lyric", out var ly)) lrc = ly.GetString() ?? "";
            if (doc.TryGetProperty("tlyric", out var t) && t.TryGetProperty("lyric", out var ty)) tl = ty.GetString() ?? "";
            if (doc.TryGetProperty("romalrc", out var m) && m.TryGetProperty("lyric", out var my)) roma = my.GetString() ?? "";
            if (doc.TryGetProperty("yrc", out var y) && y.TryGetProperty("lyric", out var yl)) yrc = yl.GetString() ?? "";   // 逐字歌词
        }
        catch { }
        return (lrc, tl, roma, yrc);
    }

    // ---------- 歌单 ----------
    public async Task<PlaylistInfo?> PlaylistDetail(long id)
    {
        var js = await Get("/playlist/detail", new Dictionary<string, string?> { ["id"] = id.ToString() });
        return js is not null && js.Value.TryGetProperty("playlist", out var p) ? p.Deserialize<PlaylistInfo>() : null;
    }

    public async Task<List<Song>> PlaylistTracks(long id, int limit = 200, int offset = 0)
    {
        var js = await Get("/playlist/track/all", new Dictionary<string, string?> { ["id"] = id.ToString(), ["limit"] = limit.ToString(), ["offset"] = offset.ToString() });
        return js is not null && js.Value.TryGetProperty("songs", out var songs)
            ? songs.EnumerateArray().Select(s => s.Deserialize<Song>() ?? new Song()).ToList() : new();
    }

    public async Task<List<PlaylistInfo>> TopPlaylist(string cat, int limit = 20)
    {
        var js = await Get("/top/playlist", new Dictionary<string, string?> { ["cat"] = cat, ["limit"] = limit.ToString() });
        return js is not null && js.Value.TryGetProperty("playlists", out var ps)
            ? ps.EnumerateArray().Select(p => p.Deserialize<PlaylistInfo>() ?? new PlaylistInfo()).ToList() : new();
    }

    public async Task<List<Song>> RecommendSongs()
    {
        var js = await Get("/recommend/songs");
        return js is not null && js.Value.TryGetProperty("data", out var data) && data.TryGetProperty("dailySongs", out var songs)
            ? songs.EnumerateArray().Select(s => s.Deserialize<Song>() ?? new Song()).ToList() : new();
    }

    public async Task<List<PlaylistInfo>> UserPlaylist(long uid, int limit = 30)
    {
        var js = await Get("/user/playlist", new Dictionary<string, string?> { ["uid"] = uid.ToString(), ["limit"] = limit.ToString() });
        return js is not null && js.Value.TryGetProperty("playlist", out var ps)
            ? ps.EnumerateArray().Select(p => p.Deserialize<PlaylistInfo>() ?? new PlaylistInfo()).ToList() : new();
    }

    // ---------- 登录 ----------
    public async Task<string> LoginQrKey()
    {
        var js = await Get("/login/qr/key");
        return js is not null && js.Value.TryGetProperty("data", out var d) && d.TryGetProperty("unikey", out var k) ? k.GetString() ?? "" : "";
    }

    public async Task<string> LoginQrCreate(string key, bool qrimg = true)
    {
        var js = await Get("/login/qr/create", new Dictionary<string, string?> { ["key"] = key, ["qrimg"] = qrimg ? "true" : "false" });
        return js is not null && js.Value.TryGetProperty("data", out var d) && d.TryGetProperty("qrimg", out var q) ? q.GetString() ?? "" : "";
    }

    /// <summary>返回 (code, cookie)。code==803 登录成功。</summary>
    public async Task<(int code, string cookie)> LoginQrCheck(string key)
    {
        var js = await Get("/login/qr/check", new Dictionary<string, string?> { ["key"] = key });
        if (js is null) return (-1, "");
        int code = js.Value.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
        string cookie = js.Value.TryGetProperty("cookie", out var k) ? k.GetString() ?? "" : "";
        return (code, cookie);
    }

    public async Task<JsonElement?> LoginStatus()
    {
        var js = await Get("/login/status");
        return js is not null && js.Value.TryGetProperty("data", out var d) ? d : null;
    }

    public void Logout() { SetCookie(""); }

    private static readonly JsonSerializerOptions _opts = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
}
