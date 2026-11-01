using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using Windows.UI.Text;
using WinRT;
using netHEmusic.Core;
using netHEmusic.Core.Logging;
using netHEmusic.Platform;

namespace netHEmusic.Windows;

/// <summary>
/// 桌面歌词：整窗透明的悬浮窗，桌面上只有歌词本身（文字带描边，浅色壁纸也能看清）。
/// 两行规则：有翻译的歌 → 上行原文、下行译文；没有翻译 → 上行当前句、下行下一句。
/// 字体/字号/颜色/透明度全都能在设置里改，改完即时生效（设置键 dl_*，见 ApplySettings）。
/// 锁定时鼠标穿透（点不到、不抢焦点）；解锁后可拖动，位置记进 config。
/// </summary>
public sealed partial class DesktopLyricWindow : Window
{
    private List<(TimeSpan t, string text)> _lines = new();
    private List<(TimeSpan t, string text)> _trans = new();
    private int _shownIdx = int.MinValue;
    private string _shownMain = "\u0000", _shownSub = "\u0000";
    private TimeSpan _lastPos = TimeSpan.Zero;

    private bool _topmost = true;
    private bool _dragging;
    private PointInt32 _dragGap;
    private double _strokeW = 1.0;      // 描边粗细（设置键 dl_stroke_width，0 = 不描边）
    private readonly StrokeText _main;
    private readonly StrokeText _sub;
    private readonly FontFamily _baseFont;

    public DesktopLyricWindow()
    {
        InitializeComponent();
        Title = "桌面歌词";
        try { ExtendsContentIntoTitleBar = true; } catch { }
        try { if (AppWindow.Presenter is OverlappedPresenter p) p.SetBorderAndTitleBar(false, false); } catch { }
        try { AppWindow.IsShownInSwitchers = false; } catch { }
        try { AppWindow.Resize(new SizeInt32(760, 110)); } catch { }
        MakeBackgroundTransparent();
        try { ClickThroughHelper.EnsureLayered(WinRT.Interop.WindowNative.GetWindowHandle(this)); } catch { }   // 透明的前提

        _main = new StrokeText(MainHost, 34, Microsoft.UI.Text.FontWeights.SemiBold,
                               new SolidColorBrush(Colors.White), Color.FromArgb(255, 0, 0, 0), 1.7);
        _sub = new StrokeText(SubHost, 20, Microsoft.UI.Text.FontWeights.Normal,
                              new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)), Color.FromArgb(255, 0, 0, 0), 1.5);
        _baseFont = _main.FontFamily;

        Root.PointerPressed += OnDragStart;
        Root.PointerMoved += OnDragMove;
        Root.PointerReleased += OnDragEnd;
        Root.PointerCaptureLost += (s, e) => { if (_dragging) { _dragging = false; SavePosition(); } };
        Activated += (s, e) => ApplyTopmost();

        // 窗口关掉后定时器还在跑就会碰到已释放的位图，一定要停
        Closed += (s, e) => StopAnim();

        RestorePosition();
        ApplySettings();

        // 拖到缩放不同的另一块屏幕上时，重新贴合一次（否则窗口尺寸又会和文字对不上）
        try
        {
            Root.Loaded += (s, e) =>
            {
                try
                {
                    Root.XamlRoot?.Changed += (_, _) => { try { if (Math.Abs(RasterScale() - _fittedScale) > 0.001) FitToContent(); } catch { } };
                    // 被拖到另一块缩放不同的屏幕上时，也要重新贴合一次
                    try
                    {
                        AppWindow.Changed += (_, args) =>
                        {
                            try { if (args.DidPositionChange && Math.Abs(RasterScale() - _fittedScale) > 0.001) FitToContent(); } catch { }
                        };
                    }
                    catch { }
                    // 窗口真正上屏后再贴合一次：恢复位置可能把窗口挪到了另一块缩放不同的屏幕上
                    try { FitToContent(); } catch { }
                }
                catch { }
            };
        }
        catch { }
    }

    /// <summary>
    /// 让窗口背景真正透明：不给 SystemBackdrop 的话 WinUI3 会铺一层不透明黑底，
    /// 这里塞一个 alpha=0 的 CompositionColorBrush 当背景，桌面上就只剩文字。
    /// </summary>
    private void MakeBackgroundTransparent()
    {
        try
        {
            // 注意：这个接口的 SystemBackdrop 形参是 Windows.UI.Composition 那套投影，
            // 而 ElementCompositionPreview 给的是 Microsoft.UI.Composition 那套，两者不能互转，
            // 所以这里直接用 Windows.UI.Composition 的 Compositor 造一个 alpha=0 的刷子。
            var compositor = new global::Windows.UI.Composition.Compositor();
            var brush = compositor.CreateColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0));
            var holder = this.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>();
            holder.SystemBackdrop = brush;
            LogManager.Debug("桌面歌词: 透明背景已开启");
        }
        catch (Exception e) { LogManager.Debug("桌面歌词透明背景失败: " + e.Message); }
    }

    // ---------------- 外观设置（[App] dl_*）----------------

    /// <summary>从 config 读外观设置并即时应用（设置面板改一下就调一次）。</summary>
    public void ApplySettings()
    {
        try
        {
            var cfg = AppServices.Config;
            var font = (cfg.Get("App", "dl_font", "") ?? "").Trim();
            double mainSize = Clamp(ParseNum(cfg.Get("App", "dl_main_size", ""), 34), 14, 96);
            double subSize = Clamp(ParseNum(cfg.Get("App", "dl_sub_size", ""), 20), 10, 64);
            var fill = ParseColor(cfg.Get("App", "dl_color", ""), Colors.White);
            var stroke = ParseColor(cfg.Get("App", "dl_stroke_color", ""), Color.FromArgb(255, 0, 0, 0));
            // 颜色模式：single 单色 / dual 双色 / quad 四色 / custom 自定义色标（设置页那张「歌词颜色」卡片）
            var colorMode = (cfg.Get("App", "dl_color_mode", "single") ?? "single").Trim().ToLowerInvariant();
            if (colorMode != "dual" && colorMode != "quad" && colorMode != "custom" && colorMode != "flow" && colorMode != "fluid")
                colorMode = "single";
            var colorDir = (cfg.Get("App", "dl_color_dir", "lr") ?? "lr").Trim().ToLowerInvariant();
            // 各模式支持的渐变方向：双色只有左右/上下；四色和流光多出「四角」（= 对角线四色）；自定义用角度
            if (colorMode == "dual" && colorDir != "tb") colorDir = "lr";
            else if ((colorMode == "quad" || colorMode == "flow") && colorDir != "tb" && colorDir != "diag") colorDir = "lr";
            int colorCount = (int)Clamp(ParseNum(cfg.Get("App", "dl_color_count", "4"), 4), 2, 64);
            var colors = new List<Color>();
            var positions = new List<double>();
            for (var i = 1; i <= colorCount; i++)
            {
                if (!TryParseColor(cfg.Get("App", "dl_color_" + i, ""), out var cc)) continue;   // 没配过就跳过（色标数量可变）
                colors.Add(cc);
                positions.Add(ParseNum(cfg.Get("App", "dl_color_pos_" + i, ""), -1));
            }
            if (colors.Count == 0) colors.Add(fill);
            double colorAngle = Clamp(ParseNum(cfg.Get("App", "dl_color_angle", "90"), 90), 0, 360);
            _animSpeed = Clamp(ParseNum(cfg.Get("App", "dl_flow_speed", "40"), 40), 1, 100);
            _turb = Clamp(ParseNum(cfg.Get("App", "dl_fluid_turb", "50"), 50), 0, 100);
            // 动态模式（流光 / 流体）每次重设都要先把上一次的定时器和画刷拆掉
            StopAnim();
            Brush fillBrush;
            if (colorMode == "flow" || colorMode == "fluid")
            {
                // 这两个模式拿调色板当色带用，只有一种颜色就自动补一个提亮色，不然「流」不起来
                var pal = colors.Count >= 2 ? colors : new List<Color> { colors[0], Lighten(colors[0], 0.45) };
                fillBrush = colorMode == "flow" ? StartFlow(pal, colorDir, colorAngle) : StartFluid(pal);
            }
            else
            {
                fillBrush = BuildFillBrush(colorMode, colorDir, colors, positions, colorAngle);
            }

            // 描边：单色模式用「描边颜色」；渐变模式改成「按渐变里最深的一色加深 N%」——
            // 用户要求只让「描边颜色」这一项随模式变化，描边粗细/其它设置不动。
            if (colorMode != "single")
            {
                double boost = Clamp(ParseNum(cfg.Get("App", "dl_stroke_boost", "40"), 40), 0, 95);
                var deepest = colors[0];
                foreach (var c in colors) if (Luma(c) < Luma(deepest)) deepest = c;
                stroke = Darken(deepest, boost);
            }
            // 描边粗细：原来是按字号算死的（主行 size/20 = 1.7px），字小的时候描边糊成一团，
            // 现在改成设置里自由调（0 = 完全不描边）
            double strokeW = Clamp(ParseNum(cfg.Get("App", "dl_stroke_width", ""), 1.0), 0, 6);
            _strokeW = strokeW;
            double opacity = Clamp(ParseNum(cfg.Get("App", "dl_opacity", ""), 100), 10, 100) / 100.0;
            bool bold = !(cfg.Get("App", "dl_bold", "true") ?? "true").Equals("false", StringComparison.OrdinalIgnoreCase);
            bool showSub = !(cfg.Get("App", "dl_show_sub", "true") ?? "true").Equals("false", StringComparison.OrdinalIgnoreCase);

            var ff = _baseFont;
            if (font.Length > 0)
            {
                try { ff = new FontFamily(font); } catch { ff = _baseFont; }
            }

            _main.FontFamily = ff;
            _sub.FontFamily = ff;
            _main.SetStyle(mainSize, bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, fillBrush, stroke, strokeW);
            _sub.SetStyle(subSize, Microsoft.UI.Text.FontWeights.Normal, fillBrush, stroke, strokeW);
            _main.Opacity = opacity;
            _sub.Opacity = opacity;
            SubHost.Visibility = showSub ? Visibility.Visible : Visibility.Collapsed;

            FitToContent();
            LogManager.Debug("桌面歌词外观: font=" + (font.Length > 0 ? font : "(默认)") + " main=" + mainSize + " sub=" + subSize +
                             " mode=" + colorMode + "/" + colorDir + " stops=" + colors.Count + " angle=" + colorAngle +
                             " cols=" + string.Join("|", colors) + " stroke=" + stroke + " strokeW=" + strokeW + " opacity=" + opacity + " bold=" + bold + " sub=" + showSub);
        }
        catch (Exception e) { LogManager.Debug("桌面歌词应用设置失败: " + e.Message); }
    }

    private static double ParseNum(string raw, double def)
        => double.TryParse((raw ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

    /// <summary>
    /// 按颜色模式造填充画刷。四色「四角」= 沿对角线的四色渐变（WinUI 3 的文本只能吃一个 Brush，
    /// 没有网格渐变，所以四个角各自一色的效果靠对角线四点逼近）。
    /// </summary>
    private static Brush BuildFillBrush(string mode, string dir, List<Color> cols, List<double> offs, double angle)
    {
        if (cols.Count == 0) return new SolidColorBrush(Colors.White);
        if (mode == "single" || cols.Count < 2) return new SolidColorBrush(cols[0]);

        double sx = 0, sy = 0.5, ex = 1, ey = 0.5;      // 默认左右
        if (mode == "custom")
        {
            var rad = angle * Math.PI / 180.0;           // 角度按屏幕坐标系：0° 向右、90° 向下
            double dx = Math.Cos(rad) * 0.5, dy = Math.Sin(rad) * 0.5;
            sx = 0.5 - dx; sy = 0.5 - dy; ex = 0.5 + dx; ey = 0.5 + dy;
        }
        else if (dir == "tb") { sx = 0.5; sy = 0; ex = 0.5; ey = 1; }
        else if (dir == "diag") { sx = 0; sy = 0; ex = 1; ey = 1; }

        var brush = new LinearGradientBrush { StartPoint = new Point(sx, sy), EndPoint = new Point(ex, ey) };
        var stops = new List<(double off, Color c)>();
        for (var i = 0; i < cols.Count; i++)
        {
            // 位置没配（-1）就按序号均匀铺开；配了就按百分比
            double off = offs[i] >= 0 ? offs[i] : (double)i / (cols.Count - 1);
            stops.Add((off, cols[i]));
        }
        stops.Sort((a, b) => a.off.CompareTo(b.off));     // GradientStopCollection 必须按 Offset 递增
        foreach (var (off, c) in stops)
            brush.GradientStops.Add(new GradientStop { Color = c, Offset = Clamp(off, 0, 1) });
        return brush;
    }

    private static double Luma(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;

    // ---------------- 动态颜色：流光（A） / 噪声流体（B） ----------------
    //
    // 文字只能吃一个 XAML Brush，拿不到着色器，所以两种效果都走「定时器每帧改画刷」：
    //   flow  —— 9 个固定 Offset 的色标，每帧按相位从调色板取插值色 → 色带整体滑动（无缝，相位取模）
    //   fluid —— 每帧把 192x64 的域扭曲噪声写进 WriteableBitmap，用 ImageBrush 拉伸铺到文字上

    private DispatcherQueueTimer? _animTimer;
    private string _animMode = "";
    private readonly List<GradientStop> _flowStops = new();
    private List<Color> _flowPalette = new();
    private double _flowPhase;
    private WriteableBitmap? _fluidBmp;
    private byte[]? _fluidPx;
    private IntPtr _fluidPtr;
    private int _fluidCap;
    private Color[] _fluidLut = Array.Empty<Color>();
    private double _fluidT;
    private double _animSpeed = 40;
    private double _turb = 50;

    private const int FlowStops = 9;
    private const int FluidW = 192, FluidH = 64;

    [ComImport, Guid("905a0fef-bc53-11df-8c49-001e4fc686da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }

    private void StopAnim()
    {
        _animMode = "";
        _flowStops.Clear();
        _flowPalette = new List<Color>();
        if (_animTimer != null) { try { _animTimer.Stop(); } catch { } _animTimer = null; }
        _fluidBmp = null;
        _fluidPx = null;
        _fluidPtr = IntPtr.Zero;
        _fluidLut = Array.Empty<Color>();
    }

    private void EnsureTimer()
    {
        if (_animTimer != null) { _animTimer.Start(); return; }
        _animTimer = DispatcherQueue.CreateTimer();
        _animTimer.Interval = TimeSpan.FromMilliseconds(33);
        _animTimer.Tick += (s, e) => StepAnim();
        _animTimer.Start();
    }

    private void StepAnim()
    {
        if (_animMode == "flow") StepFlow();
        else if (_animMode == "fluid") StepFluid(0.033);
        else if (_animTimer != null) _animTimer.Stop();
    }

    /// <summary>流光：色标位置固定，颜色跟着相位在调色板上滑过去（相位对 1 取模，所以循环处无跳变）。</summary>
    private void StepFlow()
    {
        var n = _flowPalette.Count;
        if (n < 2 || _flowStops.Count == 0) return;
        _flowPhase = (_flowPhase + 0.033 * (0.05 + _animSpeed / 100.0 * 0.55)) % 1.0;
        var count = _flowStops.Count;
        for (var i = 0; i < count; i++)
        {
            var u = i / (double)(count - 1) + _flowPhase;
            _flowStops[i].Color = SamplePalette(_flowPalette, u);
        }
    }

    private Brush StartFlow(List<Color> pal, string dir, double angle)
    {
        _flowPalette = pal;
        _flowPhase = 0;
        double sx = 0, sy = 0.5, ex = 1, ey = 0.5;
        if (dir == "tb") { sx = 0.5; sy = 0; ex = 0.5; ey = 1; }
        else if (dir == "diag") { sx = 0; sy = 0; ex = 1; ey = 1; }
        else if (dir == "custom")
        {
            var rad = angle * Math.PI / 180.0;
            double dx = Math.Cos(rad) * 0.5, dy = Math.Sin(rad) * 0.5;
            sx = 0.5 - dx; sy = 0.5 - dy; ex = 0.5 + dx; ey = 0.5 + dy;
        }
        var brush = new LinearGradientBrush { StartPoint = new Point(sx, sy), EndPoint = new Point(ex, ey) };
        for (var i = 0; i < FlowStops; i++)
        {
            var off = i / (double)(FlowStops - 1);
            var gs = new GradientStop { Offset = off, Color = SamplePalette(pal, off) };
            _flowStops.Add(gs);
            brush.GradientStops.Add(gs);
        }
        _animMode = "flow";
        EnsureTimer();
        return brush;
    }

    private Brush StartFluid(List<Color> pal)
    {
        _fluidLut = new Color[256];
        for (var i = 0; i < 256; i++) _fluidLut[i] = SamplePalette(pal, i / 256.0);
        _fluidPx = new byte[FluidW * FluidH * 4];
        _fluidBmp = new WriteableBitmap(FluidW, FluidH);
        _fluidPtr = FluidPointer();           // 位图的后备缓冲地址是稳定的，取一次就够
        _fluidT = 0;
        _animMode = "fluid";
        EnsureTimer();
        StepFluid(0.033);                    // 先画一帧，免得首帧是空白
        return new ImageBrush { ImageSource = _fluidBmp, Stretch = Stretch.Fill };
    }

    private unsafe IntPtr FluidPointer()
    {
        if (_fluidBmp == null) return IntPtr.Zero;
        var buf = _fluidBmp.PixelBuffer;
        var access = buf.As<IMemoryBufferByteAccess>();
        byte* p = null; uint cap = 0;
        try { access.GetBuffer(out p, out cap); }
        catch (Exception e) { LogManager.Debug("[fluid] GetBuffer 失败: " + e.Message); return IntPtr.Zero; }
        _fluidCap = (int)Math.Min(cap, (uint)(FluidW * FluidH * 4));
        return (IntPtr)p;
    }

    /// <summary>流体：两层正弦做域扭曲，再按扭曲后的坐标去调色板取色 —— 看起来像油膜一样缓慢翻滚。</summary>
    private unsafe void StepFluid(double dt)
    {
        if (_fluidBmp == null || _fluidPx == null || _fluidLut.Length == 0 || _fluidPtr == IntPtr.Zero) return;
        _fluidT += dt * (0.15 + _animSpeed / 100.0 * 1.9);
        var turb = 0.35 + _turb / 100.0 * 1.6;
        var t = _fluidT;
        var px = _fluidPx;
        var idx = 0;
        for (var y = 0; y < FluidH; y++)
        {
            var v = y / (double)FluidH;
            for (var x = 0; x < FluidW; x++)
            {
                var u = x / (double)FluidW;
                // 域扭曲：两个不同频率/方向的正弦叠加，avoid 规则条纹
                var wx = Math.Sin(v * 6.0 * turb + t * 1.1) * 0.5
                       + Math.Sin((u + v) * 4.0 * turb - t * 0.7) * 0.35;
                var wy = Math.Cos(u * 5.0 * turb + t * 0.9) * 0.5
                       + Math.Cos((u - v) * 3.0 * turb + t * 0.55) * 0.3;
                var s = (u + wx * 0.30) * 0.55 + (v + wy * 0.30) * 0.45;
                s -= Math.Floor(s);
                var c = _fluidLut[(int)(s * 255.999) & 255];
                px[idx++] = c.B; px[idx++] = c.G; px[idx++] = c.R; px[idx++] = 255;
            }
        }
        try
        {
            var len = _fluidCap > 0 ? Math.Min(_fluidCap, px.Length) : px.Length;
            Marshal.Copy(px, 0, _fluidPtr, len);
            _fluidBmp.Invalidate();
        }
        catch (Exception e) { LogManager.Debug("桌面歌词流体写入失败: " + e.Message); _animMode = ""; }
    }

    /// <summary>把调色板当循环色带去取色（u 可超出 0..1，自动环绕）。</summary>
    private static Color SamplePalette(List<Color> p, double u)
    {
        var n = p.Count;
        if (n == 0) return Colors.White;
        if (n == 1) return p[0];
        var pos = (u % 1.0 + 1.0) % 1.0 * n;
        var i0 = (int)Math.Floor(pos) % n;
        var i1 = (i0 + 1) % n;
        var f = pos - Math.Floor(pos);
        var a = p[i0]; var b = p[i1];
        return Color.FromArgb(
            (byte)Math.Round(a.A + (b.A - a.A) * f),
            (byte)Math.Round(a.R + (b.R - a.R) * f),
            (byte)Math.Round(a.G + (b.G - a.G) * f),
            (byte)Math.Round(a.B + (b.B - a.B) * f));
    }

    private static Color Lighten(Color c, double f)
    {
        byte Ch(byte v) => (byte)Math.Round(v + (255 - v) * f);
        return Color.FromArgb(c.A, Ch(c.R), Ch(c.G), Ch(c.B));
    }


    /// <summary>把颜色整体加深 pct%（每通道乘 1-pct/100），用来从渐变主色推描边色。</summary>
    private static Color Darken(Color c, double pct)
    {
        double k = 1.0 - Clamp(pct, 0, 95) / 100.0;
        return Color.FromArgb(c.A, (byte)Math.Round(c.R * k), (byte)Math.Round(c.G * k), (byte)Math.Round(c.B * k));
    }

    private static bool TryParseColor(string raw, out Color c)
    {
        c = default;
        try
        {
            var s = (raw ?? "").Trim().TrimStart('#');
            if (s.Length == 6) s = "FF" + s;
            if (s.Length != 8) return false;
            c = Color.FromArgb(byte.Parse(s.Substring(0, 2), NumberStyles.HexNumber),
                               byte.Parse(s.Substring(2, 2), NumberStyles.HexNumber),
                               byte.Parse(s.Substring(4, 2), NumberStyles.HexNumber),
                               byte.Parse(s.Substring(6, 2), NumberStyles.HexNumber));
            return true;
        }
        catch { return false; }
    }

    private static Color ParseColor(string raw, Color def)
        => TryParseColor(raw, out var c) ? c : def;

    // ---------------- 对外接口 ----------------

    public void SetTopmost(bool top) { _topmost = top; ApplyTopmost(); }

    /// <summary>切歌时喂一次歌词（普通 lrc + 翻译 lrc）。</summary>
    public void SetLyric(string lrc, string tlyric, string romalrc)
    {
        _lines = ParseLrc(lrc);
        _trans = ParseLrc(tlyric);
        _shownIdx = int.MinValue;
        LogManager.Debug("桌面歌词: 原文 " + _lines.Count + " 行 / 译文 " + _trans.Count + " 行");
        UpdateLine(_lastPos);
        FitToContent();
    }

    /// <summary>播放进度（前端模式和原生模式都会喂）。</summary>
    public void OnPosition(TimeSpan pos)
    {
        _lastPos = pos;
        UpdateLine(pos);
    }

    // ---------------- 两行内容 ----------------

    private void UpdateLine(TimeSpan pos)
    {
        if (_lines.Count == 0)
        {
            if (_shownMain.Length == 0 && _shownSub.Length == 0) return;
            _shownIdx = -1; _shownMain = ""; _shownSub = "";
            _main.Text = ""; _sub.Text = "";
            FitToContent();
            return;
        }

        int idx = -1;
        for (var i = 0; i < _lines.Count; i++) { if (_lines[i].t <= pos) idx = i; else break; }
        if (idx < 0) idx = 0;

        var mainText = _lines[idx].text;
        // 有译文 → 下行显示这一句的译文；没有译文 → 下行显示下一句
        var tr = NearestText(_trans, pos);
        string subText = !string.IsNullOrWhiteSpace(tr)
            ? tr
            : (idx + 1 < _lines.Count ? _lines[idx + 1].text : "");

        if (idx == _shownIdx && mainText == _shownMain && subText == _shownSub) return;
        _shownIdx = idx; _shownMain = mainText; _shownSub = subText;
        _main.Text = mainText;
        _sub.Text = subText;
        FitToContent();
    }

    private static string NearestText(List<(TimeSpan t, string text)> list, TimeSpan pos)
    {
        if (list.Count == 0) return "";
        string found = "";
        foreach (var (t, text) in list) { if (t <= pos) found = text; else break; }
        return found;
    }

    // ---------------- 自适应大小（窗口贴着文字，桌面看着就「只有歌词」）----------------

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>上一次自适应时用的缩放比，用来发现「窗口被拖到另一块缩放不同的屏幕上」。</summary>
    private double _fittedScale = -1;

    /// <summary>本窗口的 DIP→物理像素比例（125% 缩放 = 1.25）。</summary>
    private double RasterScale()
    {
        try
        {
            var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (dpi > 0) return dpi / 96.0;
        }
        catch { }
        try { var s = Root.XamlRoot?.RasterizationScale ?? 0; if (s > 0) return s; } catch { }
        return 1.0;
    }

    /// <summary>
    /// 把窗口贴着文字调整大小。
    /// 「换不换行」必须先量一次自然宽度再定死：量尺寸用的宽度和实际排版用的宽度只要差几个像素，
    /// 文字就会多折一行，而窗口高度是按一行算的 → 折出来的那行直接被窗口裁掉（看着就是「有的歌词显示不全」）。
    /// </summary>
    private void FitToContent()
    {
        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var work = area.WorkArea;                                 // 物理像素
            var k = RasterScale();                                    // XAML 量出来的是 DIP，AppWindow 收的是物理像素
            int pad = (int)Math.Ceiling((_strokeW * 2 + 4) * k) + 8;   // 描边（粗细可调，粗了要留够）+ DIP→px 取整的富余量
            _fittedScale = k;

            // 1) 先按「不换行」量一次，拿到这一句真正需要多宽（DIP）
            Lyric.MaxWidth = double.PositiveInfinity;
            _main.SetWrap(TextWrapping.NoWrap);
            _sub.SetWrap(TextWrapping.NoWrap);
            Lyric.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double natural = Lyric.DesiredSize.Width;

            // 2) 真的超屏才换行。「换不换行」在这里一次定死，之后不再让布局系统自己决定 ——
            //    量的时候不换行，实际排版时窗口又刚好窄几个像素 → 多折一行 → 高度不够 → 那行被裁掉，
            //    这就是「有的歌词显示得下、有的显示不全」的根因。
            double wrapDip = Math.Max(300, work.Width / k * 0.72);
            bool wrap = natural > wrapDip + 0.5;
            if (wrap)
            {
                _main.SetWrap(TextWrapping.Wrap);
                _sub.SetWrap(TextWrapping.Wrap);
            }
            double widthDip = wrap ? wrapDip : natural;
            Lyric.MaxWidth = widthDip;
            Lyric.Measure(new Size(widthDip, double.PositiveInfinity));
            var d = Lyric.DesiredSize;                                // DIP

            int w = (int)Math.Ceiling(d.Width * k) + pad;
            int h = (int)Math.Ceiling(d.Height * k) + pad;
            w = Math.Clamp(w, 160, work.Width);
            h = Math.Clamp(h, 40, (int)(work.Height * 0.6));

            var cur = AppWindow.Size;
            // 注意：这里**不能**做「尺寸差不多就不动窗口」的滞后处理。
            // 窗口只要比文字窄几个像素，文字就会多折一行，而高度是按一行算的 —— 那一行会被直接裁掉。
            if (cur.Width != w || cur.Height != h)
                LogManager.Debug("[歌词布局] 缩放=" + k.ToString("F2") + " 工作区=" + work.Width + "x" + work.Height + "@" + work.X + "," + work.Y +
                                 " 自然宽=" + natural.ToString("F0") + " 换行=" + (wrap ? "是" : "否") +
                                 " 文字DIP=" + d.Width.ToString("F0") + "x" + d.Height.ToString("F0") +
                                 " → 窗口px=" + w + "x" + h + " (原 " + cur.Width + "x" + cur.Height + ")");

            var pos = AppWindow.Position;
            int nx = pos.X + (cur.Width - w) / 2;
            int ny = pos.Y + (cur.Height - h) / 2;
            nx = Math.Clamp(nx, work.X, Math.Max(work.X, work.X + work.Width - w));
            ny = Math.Clamp(ny, work.Y, Math.Max(work.Y, work.Y + work.Height - h));
            AppWindow.MoveAndResize(new RectInt32(nx, ny, w, h));
            // 自检：布局真正跑完之后核对一次「文字有没有被窗口裁掉」。
            // 正常情况下一行都不该出现；一旦出现就说明「量出来的尺寸」和「实际排版」又对不上了。
            try
            {
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    try
                    {
                        var cs = AppWindow.ClientSize;
                        var kp = RasterScale();
                        double needW = Lyric.ActualWidth * kp, needH = Lyric.ActualHeight * kp;
                        if (needW > cs.Width + 1 || needH > cs.Height + 1)
                            LogManager.Debug("[歌词溢出] 文字需要 " + needW.ToString("F0") + "x" + needH.ToString("F0") +
                                             "px，窗口客户区只有 " + cs.Width + "x" + cs.Height + "px（缩放 " + kp.ToString("F2") + "）");
                    }
                    catch { }
                });
            }
            catch { }
        }
        catch (Exception e) { LogManager.Debug("桌面歌词自适应失败: " + e.Message); }
    }

    // ---------------- 位置 ----------------

    private void RestorePosition()
    {
        try
        {
            var rawX = AppServices.Config.Get("App", "desktop_lyric_x", "");
            var rawY = AppServices.Config.Get("App", "desktop_lyric_y", "");
            if (int.TryParse(rawX, out var x) && int.TryParse(rawY, out var y))
            {
                // 关键：不能只拿「窗口当前所在的那块屏」来判断。窗口刚建出来时永远在主屏上，
                // 副屏上保存的坐标会被当成非法，于是每次启动都跳回主屏。
                var target = DisplayArea.GetFromPoint(new PointInt32(x, y), DisplayAreaFallback.None);
                if (target != null)
                {
                    var wa = target.WorkArea;
                    if (x > wa.X - 200 && x < wa.X + wa.Width && y > wa.Y - 100 && y < wa.Y + wa.Height)
                    {
                        AppWindow.Move(new PointInt32(x, y));
                        return;
                    }
                }
            }
        }
        catch (Exception e) { LogManager.Debug("桌面歌词位置恢复失败: " + e.Message); }
        MoveToDefaultSpot();
    }

    private void MoveToDefaultSpot()
    {
        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var work = area.WorkArea;
            var s = AppWindow.Size;
            AppWindow.Move(new PointInt32(work.X + (work.Width - s.Width) / 2,
                                          work.Y + work.Height - s.Height - 150));
        }
        catch (Exception e) { LogManager.Debug("桌面歌词默认位置失败: " + e.Message); }
    }

    private void SavePosition()
    {
        try
        {
            var p = AppWindow.Position;
            AppServices.Config.Set("App", "desktop_lyric_x", p.X);
            AppServices.Config.Set("App", "desktop_lyric_y", p.Y);
            LogManager.Debug("桌面歌词位置已保存: " + p.X + "," + p.Y);
        }
        catch (Exception e) { LogManager.Debug("桌面歌词位置保存失败: " + e.Message); }
    }

    // ---------------- 置顶 / 拖动 ----------------

    private void ApplyTopmost()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (_topmost)
            {
                EditFrame.Visibility = Visibility.Collapsed;
                ClickThroughHelper.MakeTopmostClickThrough(hwnd, true);
            }
            else
            {
                EditFrame.Visibility = Visibility.Visible;   // 解锁状态常显提示框（用户要求回退成原来的样子）
                // 解锁时也必须保证 WS_EX_LAYERED：否则窗口没有 per-pixel alpha，
                // 提示框那个半透明底会被渲染成纯黑（"解锁后重启出现黑底板"就是这个原因）
                try { ClickThroughHelper.EnsureLayered(hwnd); ClickThroughHelper.SetClickThrough(hwnd, false); } catch { }
            }
        }
        catch (Exception e) { LogManager.Debug("桌面歌词置顶: " + e.Message); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    private void OnDragStart(object sender, PointerRoutedEventArgs e)
    {
        if (_topmost) return;
        if (!GetCursorPos(out var c)) return;
        var w = AppWindow.Position;
        _dragGap = new PointInt32(w.X - c.X, w.Y - c.Y);
        _dragging = true;
    }

    private void OnDragMove(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        if (!GetCursorPos(out var c)) return;
        AppWindow.Move(new PointInt32(c.X + _dragGap.X, c.Y + _dragGap.Y));
    }

    private void OnDragEnd(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        SavePosition();
        if (!_topmost) EditFrame.Visibility = Visibility.Visible;   // 解锁状态提示框常显
    }

    // ---------------- 歌词解析 ----------------

    private static readonly Regex TimeTag = new(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);

    // 歌词文件开头的「作词 : xxx」这类制作信息，桌面上不该出现
    private static readonly Regex CreditLine = new(
        @"^\s*(作词|作曲|编曲|制作人|出品|监制|混音|母带|录音|和声|配唱|吉他|贝斯|鼓|键盘|弦乐|合声|统筹|企划|策划|封面|词|曲|OP|SP|发行|母带工程师|录音师)\s*[:：]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static List<(TimeSpan, string)> ParseLrc(string lrc)
    {
        var list = new List<(TimeSpan, string)>();
        if (string.IsNullOrWhiteSpace(lrc)) return list;
        foreach (var raw in lrc.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var matches = TimeTag.Matches(raw);
            if (matches.Count == 0) continue;
            var text = TimeTag.Replace(raw, "").Trim();
            if (text.Length == 0) continue;
            if (CreditLine.IsMatch(text)) continue;
            foreach (Match m in matches)
            {
                int min = int.Parse(m.Groups[1].Value);
                int sec = int.Parse(m.Groups[2].Value);
                int ms = 0;
                if (m.Groups[3].Success)
                {
                    var s = m.Groups[3].Value;
                    ms = int.Parse(s) * (s.Length == 3 ? 1 : s.Length == 2 ? 10 : 100);
                }
                list.Add((new TimeSpan(0, 0, min, sec, ms), text));
            }
        }
        list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return list;
    }

    // ---------------- 带描边的文字（桌面上任何壁纸都能看清）----------------

    private sealed class StrokeText
    {
        private static readonly (double X, double Y)[] Ring =
        {
            (-1, 0), (1, 0), (0, -1), (0, 1),
            (-0.71, -0.71), (0.71, -0.71), (-0.71, 0.71), (0.71, 0.71)
        };

        private readonly List<TextBlock> _strokes = new();
        private readonly TextBlock _front;

        public StrokeText(Panel host, double fontSize, FontWeight weight,
                          Brush fill, Color stroke, double strokeWidth)
        {
            foreach (var _ in Ring)
            {
                var t = NewBlock(fontSize, weight, new SolidColorBrush(stroke));
                _strokes.Add(t);
                host.Children.Add(t);
            }
            _front = NewBlock(fontSize, weight, fill);
            host.Children.Add(_front);
            SetStyle(fontSize, weight, fill, stroke, strokeWidth);
        }

        public string Text
        {
            get => _front.Text;
            set { var v = value ?? ""; foreach (var t in _strokes) t.Text = v; _front.Text = v; }
        }

        public double LineHeightNow => _front.LineHeight;

        /// <summary>整组文字一起切换换行策略（换不换行必须由外面一次定死，见 FitToContent）。</summary>
        public void SetWrap(TextWrapping wrap)
        {
            foreach (var t in _strokes) t.TextWrapping = wrap;
            _front.TextWrapping = wrap;
        }

        public FontFamily FontFamily
        {
            get => _front.FontFamily;
            set { foreach (var t in _strokes) t.FontFamily = value; _front.FontFamily = value; }
        }

        public double Opacity
        {
            get => _front.Opacity;
            set { foreach (var t in _strokes) t.Opacity = value; _front.Opacity = value; }
        }

        public void SetStyle(double size, FontWeight weight, Brush fill, Color stroke, double strokeWidth)
        {
            var lh = LineHeightOf(size);
            var hasStroke = strokeWidth > 0.05;                  // 0 = 不描边：8 个描边层直接隐藏，省掉排版/合成开销
            for (var i = 0; i < _strokes.Count; i++)
            {
                var t = _strokes[i];
                t.FontSize = size;
                t.LineHeight = lh;
                t.FontWeight = weight;
                t.Foreground = new SolidColorBrush(stroke);
                t.Visibility = hasStroke ? Visibility.Visible : Visibility.Collapsed;
                t.RenderTransform = new TranslateTransform { X = Ring[i].X * strokeWidth, Y = Ring[i].Y * strokeWidth };
            }
            _front.FontSize = size;
            _front.LineHeight = lh;
            _front.FontWeight = weight;
            _front.Foreground = fill;                            // Color 或 LinearGradientBrush 都能直接吃
        }

        // 行高按字号算死（1.28 倍）：不能交给字体自己报 —— 有些装饰字体（如"萝莉体"）
        // 行高度量偏小，长句换行后两行会叠在一起，看着就是"下句被当前句遮挡"
        private static double LineHeightOf(double size) => Math.Ceiling(size * 1.28);

        private static TextBlock NewBlock(double size, FontWeight weight, Brush f) => new()
        {
            FontSize = size,
            FontWeight = weight,
            Foreground = f,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            IsTextScaleFactorEnabled = false,
            LineHeight = LineHeightOf(size),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };
    }
}
