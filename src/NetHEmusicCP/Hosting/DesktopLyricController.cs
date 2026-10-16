using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using netHEmusic.Core;
using netHEmusic.Shell;
using netHEmusic.Core.Api;
using netHEmusic.Core.Download;
using netHEmusic.Core.Logging;
using netHEmusic.Core.Model;
using netHEmusic.Platform;
using netHEmusic.Core.Playback;
using netHEmusic.Core.Theme;
using netHEmusic.Core.Update;
using netHEmusic.Windows;

namespace netHEmusic;

/// <summary>MainWindow 的部分实现（拆分自 MainWindow.xaml.cs，纯搬移，行为不变）。</summary>
public sealed partial class MainWindow
{
    private DesktopLyricWindow? _lyricWin;

    /// <summary>开关桌面歌词窗口（并记忆到设置）。</summary>
    private void SetDesktopLyric(bool on)
    {
        AppServices.Config.Set("App", "desktop_lyric", on ? "true" : "false");
        AppServices.RunOnUi(() =>
        {
            try
            {
                if (on)
                {
                    if (_lyricWin is null)
                    {
                        _lyricWin = new DesktopLyricWindow();
                        _lyricWin.SetTopmost(AppServices.Config.DesktopLyricTopmost);
                        _ = PushLyricToWindowAsync();
                    }
                    _lyricWin.Activate();
                    _lyricWin.AppWindow.Show();
                }
                else { _lyricWin?.AppWindow.Hide(); }
            }
            catch (Exception e) { LogManager.Error("桌面歌词窗口失败: " + e.Message); }
            PostToWeb(new { type = "desktop_lyric_state", on = on });
            LogManager.Log("桌面歌词: " + (on ? "已开启" : "已关闭"));
        });
    }

    /// <summary>把当前歌曲的歌词推给桌面歌词窗口。</summary>
    private async Task PushLyricToWindowAsync()
    {
        try
        {
            var cur = AppServices.Player.Current; if (cur is null || _lyricWin is null) return;
            var json = await AppServices.Netease.JsonLyric(cur.Id);
            var (lrc, tl, ro, yrc) = AppServices.Netease.ParseLyric(json);
            AppServices.RunOnUi(() => { try { _lyricWin?.SetLyric(lrc, tl, ro); } catch { } });
        }
        catch (Exception e) { LogManager.Debug("推送歌词失败: " + e.Message); }
    }
}