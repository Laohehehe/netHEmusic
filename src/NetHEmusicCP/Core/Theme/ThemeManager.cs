using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using netHEmusic.Core.Config;
using netHEmusic.Core.Logging;

namespace netHEmusic.Core.Theme;

/// <summary>
/// 主题管理器：Fluent 深浅色 + Material You 配色方案（对齐 material-you-theme-netease 的 scheme-presets）。
/// 每个方案提供 primary/secondary/bg/bgDarken；native 用 accent/bg，Web UI 用同名 CSS 变量。
/// </summary>
public sealed class ThemeManager
{
    private readonly AppConfig _config;

    // name -> (mode, primary, secondary, bg, bgDarken)
    private static readonly Dictionary<string, string[]> Schemes = new()
    {
        ["dark-blue"]      = new[]{"dark","#bde6fb","#bde6fb","#1e2529","#171d20"},
        ["dark-gray"]      = new[]{"dark","#ffffff","#ffffff","#202020","#191919"},
        ["dark-green"]     = new[]{"dark","#b7f1de","#b7f1de","#1a2421","#151c19"},
        ["dark-orange"]    = new[]{"dark","#ffc8b6","#ffc8b6","#271e1b","#1e1715"},
        ["dark-purple"]    = new[]{"dark","#d8c4f1","#d8c4f1","#221f26","#1a181e"},
        ["dark-red"]       = new[]{"dark","#fdb4b4","#fdb4b4","#271b1b","#1e1515"},
        ["dark-pink"]      = new[]{"dark","#ffd9e4","#ffd9e4","#362929","#211a1a"},
        ["dark-rose-pine"] = new[]{"dark","#ebbcba","#e0def4","#232136","#393552"},
        ["light-blue"]     = new[]{"light","#22c5fd","#123354","#f5f7fa","#ffffff"},
        ["light-gray"]     = new[]{"light","#61717c","#29292a","#f7f7f7","#ffffff"},
        ["light-green"]    = new[]{"light","#2ae18e","#19483e","#f6f9f9","#e5eceb"},
        ["light-orange"]   = new[]{"light","#ff8265","#563b25","#faf8f7","#ffffff"},
        ["light-purple"]   = new[]{"light","#9f74e7","#402b4d","#f9f7f9","#ffffff"},
        ["light-red"]      = new[]{"light","#ff5966","#572920","#faf7f6","#ffffff"},
        ["light-pink"]     = new[]{"light","#ff82ab","#630a27","#faf7f6","#ffffff"},
        ["light-rose-pine"]= new[]{"light","#d7827e","#575279","#f2e9e1","#faf4ed"},
        ["tokyo-night"]    = new[]{"dark","#b5b9d6","#b5b9d6","#242638","#1c1d2b"},
        ["one-dark-blue"]  = new[]{"dark","#71bdf2","#abb2bf","#282c34","#21252b"},
        ["one-dark-green"] = new[]{"dark","#a7cb8b","#abb2bf","#282c34","#21252b"},
        ["one-dark-cyan"]  = new[]{"dark","#65c1cd","#abb2bf","#282c34","#21252b"},
        ["one-dark-red"]   = new[]{"dark","#e78287","#abb2bf","#282c34","#21252b"},
        ["one-dark-pink"]  = new[]{"dark","#ff79c6","#abb2bf","#282c34","#21252b"},
        ["one-dark-yellow"]= new[]{"dark","#daaa78","#abb2bf","#282c34","#21252b"},
        ["one-dark-purple"]= new[]{"dark","#d190e3","#abb2bf","#282c34","#21252b"},
        ["osu-pink"]       = new[]{"dark","#ff66ab","#f0dbe4","#2a2226","#1c1719"},
        ["osu-purple"]     = new[]{"dark","#8c66ff","#e0dbf0","#24222a","#18171c"},
        ["osu-blue"]       = new[]{"dark","#66ccff","#dbe9f0","#22282a","#171a1c"},
        ["osu-green"]      = new[]{"dark","#73ff66","#ddf0db","#232a22","#171c17"},
        ["osu-orange"]     = new[]{"dark","#ff9966","#f0e2db","#2a2522","#1c1917"},
        ["osu-yellow"]     = new[]{"dark","#ffd966","#f0ebdb","#2a2822","#1c1b17"},
        ["cyberpunk"]      = new[]{"dark","#fcec0c","#fcec0c","#136377","#084a5a"},
        ["matrix"]         = new[]{"dark","#00ff41","#00ff41","#060208","#001600"},
        ["dracula-mint"]   = new[]{"dark","#2fdeb6","#e2e2e4","#292d3e","#212432"},
        ["cerulean"]       = new[]{"light","#428db9","#212121","#f3f8fb","#dfeef3"},
        ["discord"]        = new[]{"dark","#5865f2","#ffffff","#36393f","#2f3136"},
        ["wechat"]         = new[]{"light","#07c160","#222222","#f5f5f5","#dadada"},
        ["tim"]            = new[]{"light","#1d6eff","#222222","#f4f6f8","#ffffff"},
        ["pure-black"]     = new[]{"dark","#f0f0f0","#f0f0f0","#000000","#141414"},
        ["netease-default"]= new[]{"dark","#e23535","#e23535","#151719","#1f2226"},
        ["dynamic-auto"]   = new[]{"dark","#2563eb","#2563eb","#151719","#1f2226"},
    };

    public event Action? ThemeChanged;
    public string CurrentScheme => _config.Scheme;
    public string CurrentTheme => _config.Theme;
    public bool IsDark => _config.Theme != "light";

    public ThemeManager(AppConfig config) => _config = config;

    public (string mode, string primary, string secondary, string bg, string bgDarken) GetScheme(string? name = null)
    {
        name ??= _config.Scheme;
        if (Schemes.TryGetValue(name, out var v)) return (v[0], v[1], v[2], v[3], v[4]);
        return Schemes["dark-blue"] is var d ? (d[0], d[1], d[2], d[3], d[4]) : default;
    }

    public Color AccentColor(bool dark) => HexToColor(GetScheme().primary);
    public Color SurfaceColor(bool dark) => HexToColor(GetScheme().bg);
    public Color SurfaceDarken(bool dark) => HexToColor(GetScheme().bgDarken);
    public Color TextColor(bool dark) => HexToColor(GetScheme().secondary);

    public static Color HexToColor(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6) hex = "FF" + hex;
        byte a = Convert.ToByte(hex.Substring(0, 2), 16);
        byte r = Convert.ToByte(hex.Substring(2, 2), 16);
        byte g = Convert.ToByte(hex.Substring(4, 2), 16);
        byte b = Convert.ToByte(hex.Substring(6, 2), 16);
        return Color.FromArgb(a, r, g, b);
    }

    public static (byte, byte, byte) HexToRgb(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6) hex = "FF" + hex;
        return (Convert.ToByte(hex.Substring(2, 2), 16), Convert.ToByte(hex.Substring(4, 2), 16), Convert.ToByte(hex.Substring(6, 2), 16));
    }

    public string[] SchemeNames() => new List<string>(Schemes.Keys).ToArray();

    public void Apply(FrameworkElement root)
    {
        if (root is null) return;
        var theme = _config.Theme == "light" ? ElementTheme.Light : ElementTheme.Dark;
        root.RequestedTheme = theme;
        bool dark = theme == ElementTheme.Dark;
        var accent = AccentColor(dark);

        SetResource(root, "AccentFillColorDefaultBrush", new SolidColorBrush(accent));
        SetResource(root, "AccentFillColorSecondaryBrush", new SolidColorBrush(WithAlpha(accent, 0.75)));
        SetResource(root, "AccentFillColorTertiaryBrush", new SolidColorBrush(WithAlpha(accent, 0.5)));
        SetResource(root, "AppBackgroundBrush", new SolidColorBrush(SurfaceColor(dark)));
        SetResource(root, "AppSurfaceDarkenBrush", new SolidColorBrush(SurfaceDarken(dark)));
        SetResource(root, "AppTextBrush", new SolidColorBrush(TextColor(dark)));

        LogManager.Log("应用主题: " + _config.Theme + " scheme=" + _config.Scheme + " accent=#" + accent.ToString());
        ThemeChanged?.Invoke();
    }

    private static Color WithAlpha(Color c, double a) => Color.FromArgb((byte)(a * 255), c.R, c.G, c.B);

    private static void SetResource(FrameworkElement root, string key, object value)
    {
        try
        {
            if (root.Resources.ContainsKey(key)) root.Resources[key] = value; else root.Resources[key] = value;
            if (Application.Current?.Resources is { } appRes)
            {
                if (appRes.ContainsKey(key)) appRes[key] = value; else appRes[key] = value;
            }
        }
        catch { }
    }

    public void SetTheme(string theme) { if (theme is "light" or "dark") { _config.Theme = theme; LogManager.Log("主题切换: " + theme); } }
    public void SetScheme(string scheme) { _config.Scheme = scheme; LogManager.Log("方案切换: " + scheme); }
    public void ToggleTheme() { _config.Theme = _config.Theme == "light" ? "dark" : "light"; LogManager.Log("主题切换: " + _config.Theme); }

    /// <summary>给 Web UI 生成一套 Material You CSS 变量声明（用于注入样式）。</summary>
    public static string ToCssVars(string mode, string primary, string secondary, string bg, string bgDarken)
    {
        var pr = HexToRgb(primary); var se = HexToRgb(secondary); var bgc = HexToRgb(bg); var bd = HexToRgb(bgDarken);
        var grey = mode == "light" ? "0,0,0" : "255,255,255";
        return $"--md-accent-color:{primary};--md-accent-color-rgb:{pr.Item1},{pr.Item2},{pr.Item3};" +
               $"--md-accent-color-secondary:{secondary};--md-accent-color-secondary-rgb:{se.Item1},{se.Item2},{se.Item3};" +
               $"--md-accent-color-bg:{bg};--md-accent-color-bg-rgb:{bgc.Item1},{bgc.Item2},{bgc.Item3};" +
               $"--md-accent-color-bg-darken:{bgDarken};--md-accent-color-bg-darken-rgb:{bd.Item1},{bd.Item2},{bd.Item3};" +
               $"--md-accent-color-grey-base:rgb({grey});--md-accent-color-grey-base-rgb:{grey};";
    }
}
