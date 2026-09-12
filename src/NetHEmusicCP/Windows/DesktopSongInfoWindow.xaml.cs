using System;
using Windows.Graphics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using netHEmusic.Core.Native;

namespace netHEmusic.Windows;

public sealed partial class DesktopSongInfoWindow : Window
{
    public DesktopSongInfoWindow()
    {
        InitializeComponent();
        Title = "歌曲信息";
        try { ExtendsContentIntoTitleBar = true; SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop(); } catch { }
        try { AppWindow.Resize(new SizeInt32(340, 128)); AppWindow.Move(new PointInt32(200, 360)); } catch { }
        Activated += (s, e) => { var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this); ClickThroughHelper.MakeTopmostClickThrough(hwnd, true); };
    }

    public void SetSong(string title, string artist, string picUrl)
    {
        SongTitle.Text = title;
        Artist.Text = artist;
        if (!string.IsNullOrEmpty(picUrl)) { try { Cover.Source = new BitmapImage(new Uri(picUrl)); } catch { } }
    }

    public void SetLyric(string lrc)
    {
        // 预留：可显示歌曲信息+歌词摘要
    }
}
