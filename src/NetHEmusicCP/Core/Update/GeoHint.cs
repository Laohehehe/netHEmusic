using System;
using System.Globalization;

namespace netHEmusic.Core.Update;

/// <summary>
/// 粗略判断「这台机器是不是在中国大陆」，用来决定更新优先走 GitHub 还是 Gitee。
///
/// 只看本机设置，不发网络请求：
///   - Windows 时区 ID 是 "China Standard Time"（覆盖北京 / 香港 / 乌鲁木齐）
///   - 或者系统区域设置是 CN
///
/// 判断错了也不要紧：两个源都会试一遍，谁给出的版本新就用谁，另一个当兜底。
/// </summary>
public static class GeoHint
{
    private static bool? _inChina;

    public static bool InChina()
    {
        if (_inChina.HasValue) return _inChina.Value;
        var v = false;
        try
        {
            var tz = TimeZoneInfo.Local.Id ?? "";
            if (tz.Equals("China Standard Time", StringComparison.OrdinalIgnoreCase)) v = true;
            else
            {
                var iso = RegionInfo.CurrentRegion.TwoLetterISORegionName ?? "";
                if (iso.Equals("CN", StringComparison.OrdinalIgnoreCase)) v = true;
            }
        }
        catch { }
        _inChina = v;
        return v;
    }

    /// <summary>给日志/界面看的一句话说明。</summary>
    public static string Describe()
    {
        try { return InChina() ? "国内 → 优先 Gitee" : "海外 → 优先 GitHub"; }
        catch { return "未知"; }
    }
}
