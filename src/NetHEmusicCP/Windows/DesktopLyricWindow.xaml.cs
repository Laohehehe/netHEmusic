using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using Windows.UI.Text;
using WinRT;
using netHEmusic.Core;
using netHEmusic.Core.Logging;
using netHEmusic.Core.Native;

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
    private bool _hover;
    private PointInt32 _dragGap;
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

        _main = new StrokeText(MainHost, 34, Microsoft.UI.Text.FontWeights.SemiBold,
                               Colors.White, Color.FromArgb(255, 0, 0, 0), 1.7);
        _sub = new StrokeText(SubHost, 20, Microsoft.UI.Text.FontWeights.Normal,
                              Color.FromArgb(255, 255, 255, 255), Color.FromArgb(255, 0, 0, 0), 1.5);
        _baseFont = _main.FontFamily;

        Root.PointerEntered += (s, e) => { _hover = true; if (!_topmost) EditFrame.Visibility = Visibility.Visible; };
        Root.PointerExited += (s, e) => { _hover = false; if (!_topmost && !_dragging) EditFrame.Visibility = Visibility.Collapsed; };
        Root.PointerPressed += OnDragStart;
        Root.PointerMoved += OnDragMove;
        Root.PointerReleased += OnDragEnd;
        Root.PointerCaptureLost += (s, e) => { if (_dragging) { _dragging = false; SavePosition(); } };
        Activated += (s, e) => ApplyTopmost();

        RestorePosition();
        ApplySettings();

        // 拖到缩放不同的另一块屏幕上时，重新贴合一次（否则窗口尺寸又会和文字对不上）
        try
        {
            Root.Loaded += (s, e) =>
            {
                try
                {
                    Root.XamlRoot?.Changed += (_, _) => { try { FitToContent(true); } catch { } };
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
            _main.SetStyle(mainSize, bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, fill, stroke, Math.Max(1.2, mainSize / 20.0));
            _sub.SetStyle(subSize, Microsoft.UI.Text.FontWeights.Normal, fill, stroke, Math.Max(1.0, subSize / 14.0));
            _main.Opacity = opacity;
            _sub.Opacity = opacity;
            SubHost.Visibility = showSub ? Visibility.Visible : Visibility.Collapsed;

            FitToContent(true);
            LogManager.Debug("桌面歌词外观: font=" + (font.Length > 0 ? font : "(默认)") + " main=" + mainSize + " sub=" + subSize +
                             " color=" + fill + " stroke=" + stroke + " opacity=" + opacity + " bold=" + bold + " sub=" + showSub);
        }
        catch (Exception e) { LogManager.Debug("桌面歌词应用设置失败: " + e.Message); }
    }

    private static double ParseNum(string raw, double def)
        => double.TryParse((raw ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

    private static Color ParseColor(string raw, Color def)
    {
        try
        {
            var s = (raw ?? "").Trim().TrimStart('#');
            if (s.Length == 6) s = "FF" + s;
            if (s.Length != 8) return def;
            return Color.FromArgb(byte.Parse(s.Substring(0, 2), NumberStyles.HexNumber),
                                  byte.Parse(s.Substring(2, 2), NumberStyles.HexNumber),
                                  byte.Parse(s.Substring(4, 2), NumberStyles.HexNumber),
                                  byte.Parse(s.Substring(6, 2), NumberStyles.HexNumber));
        }
        catch { return def; }
    }

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
        FitToContent(true);
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
            FitToContent(false);
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
        FitToContent(false);
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

    private void FitToContent(bool force)
    {
        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var work = area.WorkArea;                                 // 物理像素
            var k = RasterScale();                                    // XAML 量出来的是 DIP，AppWindow 收的是物理像素

            double maxDip = Math.Max(360, work.Width / k * 0.72);     // 可用宽度换算成 DIP
            Lyric.MaxWidth = maxDip;
            Lyric.Measure(new Size(maxDip, double.PositiveInfinity));
            var d = Lyric.DesiredSize;                                // DIP
            int w = (int)Math.Ceiling(Math.Min(d.Width, maxDip) * k) + 6;
            int h = (int)Math.Ceiling(d.Height * k) + 4;
            w = Math.Clamp(w, 160, (int)(work.Width * 0.72));
            h = Math.Clamp(h, 40, (int)(work.Height * 0.4));

            var cur = AppWindow.Size;
            if (!force && Math.Abs(cur.Width - w) < 12 && Math.Abs(cur.Height - h) < 6) return;

            var pos = AppWindow.Position;
            int nx = pos.X + (cur.Width - w) / 2;
            int ny = pos.Y + (cur.Height - h) / 2;
            nx = Math.Clamp(nx, work.X, Math.Max(work.X, work.X + work.Width - w));
            ny = Math.Clamp(ny, work.Y, Math.Max(work.Y, work.Y + work.Height - h));
            AppWindow.MoveAndResize(new RectInt32(nx, ny, w, h));
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
                var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
                var work = area.WorkArea;
                // 屏幕数量/分辨率变了就当作失效，回到默认位置
                if (x > work.X - 200 && x < work.X + work.Width && y > work.Y - 100 && y < work.Y + work.Height)
                {
                    AppWindow.Move(new PointInt32(x, y));
                    return;
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
                _hover = false;
                EditFrame.Visibility = Visibility.Collapsed;
                ClickThroughHelper.MakeTopmostClickThrough(hwnd, true);
            }
            else
            {
                // 解锁后也不常显边框，只在鼠标移上来时提示一下"这里能拖"
                EditFrame.Visibility = _hover ? Visibility.Visible : Visibility.Collapsed;
                try { ClickThroughHelper.SetClickThrough(hwnd, false); } catch { }
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
        if (!_topmost && !_hover) EditFrame.Visibility = Visibility.Collapsed;
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
                          Color fill, Color stroke, double strokeWidth)
        {
            foreach (var _ in Ring)
            {
                var t = NewBlock(fontSize, weight, stroke);
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

        public void SetStyle(double size, FontWeight weight, Color fill, Color stroke, double strokeWidth)
        {
            for (var i = 0; i < _strokes.Count; i++)
            {
                var t = _strokes[i];
                t.FontSize = size;
                t.FontWeight = weight;
                t.Foreground = new SolidColorBrush(stroke);
                t.RenderTransform = new TranslateTransform { X = Ring[i].X * strokeWidth, Y = Ring[i].Y * strokeWidth };
            }
            _front.FontSize = size;
            _front.FontWeight = weight;
            _front.Foreground = new SolidColorBrush(fill);
        }

        private static TextBlock NewBlock(double size, FontWeight weight, Color c) => new()
        {
            FontSize = size,
            FontWeight = weight,
            Foreground = new SolidColorBrush(c),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            IsTextScaleFactorEnabled = false
        };
    }
}
