using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using netHEmusic.Core.Logging;
using netHEmusic.Core.Native;

namespace netHEmusic.Windows;

public sealed partial class DesktopLyricWindow : Window
{
    private List<(TimeSpan t, string text)> _lines = new();
    private List<(TimeSpan t, string text)> _tl = new();
    private bool _vertical;
    private bool _topmost = true;
    private bool _dragging;
    private PointInt32 _dragOffset;

    public DesktopLyricWindow()
    {
        InitializeComponent();
        Title = "桌面歌词";
        try { ExtendsContentIntoTitleBar = true; SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop(); } catch { }
        try { AppWindow.Resize(new SizeInt32(560, 130)); } catch { }
        try { AppWindow.Move(new PointInt32(200, 200)); } catch { }
        Card.PointerPressed += OnDragStart;
        Card.PointerMoved += OnDragMove;
        Card.PointerReleased += OnDragEnd;
        Activated += (s, e) => ApplyTopmost();
    }

    public void SetTopmost(bool top) { _topmost = top; ApplyTopmost(); }

    public void SetVertical(bool vertical)
    {
        _vertical = vertical;
        HorizontalLyric.Visibility = vertical ? Visibility.Collapsed : Visibility.Visible;
        VerticalLyric.Visibility = vertical ? Visibility.Visible : Visibility.Collapsed;
        var w = vertical ? 130 : 560;
        var h = vertical ? 560 : 130;
        try { AppWindow.Resize(new SizeInt32(w, h)); } catch { }
    }

    private void ApplyTopmost()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (_topmost)
            {
                Card.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
                ClickThroughHelper.MakeTopmostClickThrough(hwnd, true);
            }
            else
            {
                Card.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Color.FromArgb(255, 255, 200, 0));
                try { ClickThroughHelper.SetClickThrough(hwnd, false); } catch { }
            }
            LogManager.Debug("桌面歌词 topmost=" + _topmost);
        }
        catch (Exception e) { LogManager.Debug("桌面歌词置顶: " + e.Message); }
    }

    public void SetLyric(string lrc, string tlyric, string romalrc)
    {
        _lines = ParseLrc(lrc);
        _tl = ParseLrc(!string.IsNullOrEmpty(tlyric) ? tlyric : romalrc);
        EmptyText.Visibility = _lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateLine(TimeSpan.Zero);
    }

    public void OnPosition(TimeSpan pos) => UpdateLine(pos);

    private void UpdateLine(TimeSpan pos)
    {
        if (_lines.Count == 0) return;
        var cur = _lines.LastOrDefault(x => x.t <= pos).text;
        var tl = _tl.LastOrDefault(x => x.t <= pos).text;
        if (_vertical)
        {
            VLineText.Text = cur;
            VSubText.Text = tl;
        }
        else
        {
            LineText.Text = cur;
            SubLineText.Text = string.IsNullOrEmpty(tl) ? _lines.SkipWhile(x => x.t <= pos).FirstOrDefault().text : tl;
        }
    }

    private static List<(TimeSpan, string)> ParseLrc(string lrc)
    {
        var list = new List<(TimeSpan, string)>();
        if (string.IsNullOrEmpty(lrc)) return list;
        var re = new Regex(@"\[(\d{1,2}):(\d{2})(?:[.:](\d{1,3}))?\]");
        foreach (var raw in lrc.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            var m = re.Match(line);
            if (!m.Success) continue;
            var text = re.Replace(line, "").Trim();
            if (string.IsNullOrEmpty(text)) continue;
            int min = int.Parse(m.Groups[1].Value);
            int sec = int.Parse(m.Groups[2].Value);
            int ms = 0;
            if (m.Groups[3].Success) { var s = m.Groups[3].Value; ms = int.Parse(s) * (s.Length == 3 ? 1 : s.Length == 2 ? 10 : 100); }
            list.Add((new TimeSpan(0, 0, min, sec, ms), text));
        }
        list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return list;
    }

    private void OnDragStart(object sender, PointerRoutedEventArgs e)
    {
        if (_topmost) return;
        _dragging = true;
        var p = e.GetCurrentPoint(null).Position;
        _dragOffset = new PointInt32((int)p.X, (int)p.Y);
        try { ((UIElement)sender).CapturePointer(e.Pointer); } catch { }
    }
    private void OnDragMove(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || _topmost) return;
        try
        {
            var pos = e.GetCurrentPoint(null).Position;
            AppWindow.Move(new PointInt32((int)pos.X - _dragOffset.X, (int)pos.Y - _dragOffset.Y));
        }
        catch { }
    }
    private void OnDragEnd(object sender, PointerRoutedEventArgs e) { _dragging = false; }
}
