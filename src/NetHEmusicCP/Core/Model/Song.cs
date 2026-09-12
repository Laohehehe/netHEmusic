using System.Text.Json.Serialization;

namespace netHEmusic.Core.Model;

/// <summary>歌曲信息（兼容搜索结果 artists/ar 与 歌单 ar/al）。</summary>
public class Song
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Title { get; set; } = "";
    [JsonPropertyName("artists")] public List<Artist>? Artists { get; set; }
    [JsonPropertyName("ar")] public List<Artist>? Ar { get; set; }
    [JsonPropertyName("album")] public Album? Album { get; set; }
    [JsonPropertyName("al")] public Album? Al { get; set; }
    [JsonPropertyName("duration")] public long DurationMs { get; set; }
    [JsonPropertyName("dt")] public long DtMs { get; set; }
    [JsonPropertyName("fee")] public int Fee { get; set; }
    [JsonPropertyName("pic")] public string? Pic { get; set; }
    [JsonPropertyName("privilege")] public Privilege? Privilege { get; set; }

    // 便捷
    [JsonIgnore] public string ArtistsName => string.Join("/", (Artists ?? Ar ?? new()).ConvertAll(a => a.Name));
    [JsonIgnore] public string AlbumName => (Album?.Name) ?? (Al?.Name) ?? "";
    [JsonIgnore] public long Duration => DurationMs > 0 ? DurationMs : DtMs;
    [JsonIgnore] public string PicUrl => Pic ?? Album?.PicUrl ?? Al?.PicUrl ?? "";
    [JsonIgnore] public string DisplayName => string.IsNullOrEmpty(ArtistsName) ? Title : $"{ArtistsName} - {Title}";
}

public class Artist { [JsonPropertyName("name")] public string Name { get; set; } = ""; }
public class Album
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("picUrl")] public string? PicUrl { get; set; }
}
public class Privilege { [JsonPropertyName("fee")] public int Fee { get; set; } [JsonPropertyName("pl")] public int Pl { get; set; } }

/// <summary>歌单/歌单条目最小模型。</summary>
public class PlaylistInfo
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("trackCount")] public int TrackCount { get; set; }
    [JsonPropertyName("coverImgUrl")] public string? CoverImgUrl { get; set; }
}
