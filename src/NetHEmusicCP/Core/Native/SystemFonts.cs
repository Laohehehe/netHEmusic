using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using netHEmusic.Core.Logging;

namespace netHEmusic.Core.Native;

/// <summary>
/// 枚举本机已安装的字体族（GDI EnumFontFamiliesEx），给设置里的字体下拉用。
/// 结果缓存在进程内，字体不会在运行期变。
/// </summary>
internal static class SystemFonts
{
    private const byte DEFAULT_CHARSET = 1;
    private const int LF_FACESIZE = 32;
    private const int LF_FULLFACESIZE = 64;   // ENUMLOGFONTEX.elfFullName 用的是这个，不是 LF_FACESIZE

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
        // ⚠️ 尺寸必须和 Windows SDK 完全一致，否则 GDI 回调写越界 → 踩爆栈 cookie → 0xC0000409「栈缓冲区溢出」。
        //    ENUMLOGFONTEXW = LOGFONTW(92) + elfFullName[LF_FULLFACESIZE=64](128)
        //                     + elfStyle[LF_FACESIZE=32](64) + elfScript[LF_FACESIZE=32](64) = 348 字节
        //    曾经把 elfFullName 误写成 SizeConst=32（少 64 字节），更新时必崩。
        public LOGFONT elfLogFont;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = LF_FULLFACESIZE)]
        public string elfFullName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = LF_FACESIZE)]
        public string elfStyle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = LF_FACESIZE)]
        public string elfScript;
    }

    /// <summary>结构体布局自检：和 SDK 声明对不上就根本别去调 GDI（会写越界）。</summary>
    private static bool LayoutOk()
    {
        try
        {
            var lf = Marshal.SizeOf<LOGFONT>();
            var ex = Marshal.SizeOf<ENUMLOGFONTEX>();
            var ok = lf == 92 && ex == 348;
            if (!ok) LogManager.Warn("[字体] 结构体尺寸异常 LOGFONT=" + lf + "(应为92) ENUMLOGFONTEX=" + ex + "(应为348)，已跳过系统字体枚举");
            return ok;
        }
        catch { return false; }
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
        if (!LayoutOk()) { _cache = new List<string>(); return _cache; }
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
