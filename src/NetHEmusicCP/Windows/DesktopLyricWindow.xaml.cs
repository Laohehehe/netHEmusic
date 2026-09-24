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
        try { ClickThroughHelper.EnsureLayered(WinRT.Interop.WindowNative.GetWindowHandle(this)); } catch { }   // 透明的前提

        _main = new StrokeText(MainHost, 34, Microsoft.UI.Text.FontWeights.SemiBold,
                               Colors.White, Color.FromArgb(255, 0, 0, 0), 1.7);
        _sub = new StrokeText(SubHost, 20, Microsoft.UI.Text.FontWeights.Normal,
                              Color.FromArgb(255, 255, 255, 255), Color.FromArgb(255, 0, 0, 0), 1.5);
        _baseFont = _main.FontFamily;

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

            FitToContent();
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
            int pad = (int)Math.Ceiling(k) * 2 + 8;                   // 描边 + DIP→px 取整的富余量
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

        public void SetStyle(double size, FontWeight weight, Color fill, Color stroke, double strokeWidth)
        {
            var lh = LineHeightOf(size);
            for (var i = 0; i < _strokes.Count; i++)
            {
                var t = _strokes[i];
                t.FontSize = size;
                t.LineHeight = lh;
                t.FontWeight = weight;
                t.Foreground = new SolidColorBrush(stroke);
                t.RenderTransform = new TranslateTransform { X = Ring[i].X * strokeWidth, Y = Ring[i].Y * strokeWidth };
            }
            _front.FontSize = size;
            _front.LineHeight = lh;
            _front.FontWeight = weight;
            _front.Foreground = new SolidColorBrush(fill);
        }

        // 行高按字号算死（1.28 倍）：不能交给字体自己报 —— 有些装饰字体（如"萝莉体"）
        // 行高度量偏小，长句换行后两行会叠在一起，看着就是"下句被当前句遮挡"
        private static double LineHeightOf(double size) => Math.Ceiling(size * 1.28);

        private static TextBlock NewBlock(double size, FontWeight weight, Color c) => new()
        {
            FontSize = size,
            FontWeight = weight,
            Foreground = new SolidColorBrush(c),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            IsTextScaleFactorEnabled = false,
            LineHeight = LineHeightOf(size),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };
    }
}
