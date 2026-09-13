using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace netHEmusic.Core.Native;

/// <summary>
/// 枚举本机已安装的字体族（GDI EnumFontFamiliesEx），给设置里的字体下拉用。
/// 结果缓存在进程内，字体不会在运行期变。
/// </summary>
internal static class SystemFonts
{
    private const byte DEFAULT_CHARSET = 1;
    private const int LF_FACESIZE = 32;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LOGFONT
    {
        public int lfHeight, lfWidth, lfEscapement, lfOrientation, lfWeight;
        public byte lfItalic, lfUnderline, lfStrikeOut, lfCharSet, lfOutPrecision, lfClipPrecision, lfQuality, lfPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = LF_FACESIZE)]
        public string lfFaceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ENUMLOGFONTEX
    {
        public LOGFONT elfLogFont;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = LF_FACESIZE)]
        public string elfFullName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = LF_FACESIZE)]
        public string elfStyle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = LF_FACESIZE)]
        public string elfScript;
    }

    private delegate int EnumFontFamExProc(ref ENUMLOGFONTEX lpelfe, IntPtr lpntme, uint fontType, IntPtr lParam);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int EnumFontFamiliesEx(IntPtr hdc, ref LOGFONT lpLogfont, EnumFontFamExProc lpEnumFontFamExProc, IntPtr lParam, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    private static List<string>? _cache;

    /// <summary>本机字体族名（去重、按名称排序、去掉 @ 开头的竖排字体）。</summary>
    public static List<string> Families()
    {
        if (_cache is not null) return _cache;
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var hdc = GetDC(IntPtr.Zero);
        try
        {
            var lf = new LOGFONT { lfCharSet = DEFAULT_CHARSET, lfFaceName = "" };
            EnumFontFamExProc cb = (ref ENUMLOGFONTEX e, IntPtr n, uint t, IntPtr p) =>
            {
                var name = e.elfLogFont.lfFaceName;
                if (!string.IsNullOrWhiteSpace(name) && !name.StartsWith("@", StringComparison.Ordinal))
                    set.Add(name.Trim());
                return 1;   // 非 0 = 继续枚举
            };
            EnumFontFamiliesEx(hdc, ref lf, cb, IntPtr.Zero, 0);
            GC.KeepAlive(cb);
        }
        catch { }
        finally { if (hdc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hdc); }
        _cache = new List<string>(set);
        return _cache;
    }
}
