using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using netHEmusic.Core.Logging;

namespace netHEmusic.Core.Config;

/// <summary>
/// 轻量 JSON 状态文件读写（player.json / downloads.json）。
/// 带锁 + 先写 .tmp 再替换，避免崩在写一半；任何异常都不往上抛，坏了就当默认值用。
/// </summary>
internal static class JsonFile
{
    private static readonly object Lock = new();
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping   // 中文/日文歌名不要被转成 \uXXXX
    };

    public static T Read<T>(string path, Func<T> fallback) where T : class
    {
        try
        {
            lock (Lock)
            {
                if (!File.Exists(path)) return fallback();
                var json = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return fallback();
                return JsonSerializer.Deserialize<T>(json, Opts) ?? fallback();
            }
        }
        catch (Exception e)
        {
            LogManager.Debug("读 " + Path.GetFileName(path) + " 失败: " + e.Message);
            return fallback();
        }
    }

    public static void Write<T>(string path, T value)
    {
        try
        {
            lock (Lock)
            {
                var json = JsonSerializer.Serialize(value, Opts);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                File.Move(tmp, path, true);
            }
        }
        catch (Exception e) { LogManager.Error("写 " + Path.GetFileName(path) + " 失败: " + e.Message); }
    }
}
