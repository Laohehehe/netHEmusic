using System;
using System.Collections.Generic;
using netHEmusic.Core.Logging;

namespace netHEmusic.Core.Elements;

/// <summary>
/// 元素注册表：以“元素类别.位置.子位置”命名（gui.main.index / button.main.headbar.search ...）。
/// 提供元素键名解析、选中元素窗口的命中提示。
/// </summary>
public sealed class ElementRegistry
{
    private static readonly HashSet<string> KnownPrefixes = new(StringComparer.Ordinal)
    {
        "gui", "button", "input", "text", "img", "browser", "dock", "lsidebar", "headbar",
        "list", "select", "toggle", "progress", "icon", "menu", "dialog", "window", "item"
    };

    /// <summary>规范化元素键名：去掉空白、保证小写类别。</summary>
    public static string Normalize(string raw)
    {
        var s = (raw ?? "").Trim();
        return s;
    }

    /// <summary>判断键名是否合法元素键（形如 a.b.c[.d]）。</summary>
    public static bool IsValid(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        var parts = key.Split('.');
        return parts.Length >= 3 && KnownPrefixes.Contains(parts[0].ToLowerInvariant());
    }

    /// <summary>把元素键名写入日志（用于选定元素窗口/调试）。</summary>
    public static void Trace(string key) => LogManager.Debug("元素命中: " + key);

    public static string Describe(string key) => IsValid(key) ? key : "—（非元素）";
}
