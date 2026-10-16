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
    /// <summary>给托盘右键菜单用的状态读取与动作入口。</summary>
    public TrayActions TrayActions() => new()
    {
        Prev = () => { try { _ = AppServices.Player.PrevAsync(); } catch { } },
        Next = () => { try { _ = AppServices.Player.NextAsync(); } catch { } },
        TogglePlay = () => { try { _ = AppServices.Player.ToggleAsync(); } catch { } },
        IsPlaying = () => { try { return AppServices.Player.Playing; } catch { return false; } },
        PlayMode = () => AppServices.Config.PlayMode,
        // 播放模式是前端的概念（顺序/列表/单曲/随机），这里改完配置再通知前端切一次
        SetPlayMode = v => { AppServices.Config.PlayMode = v; AppServices.Player.OnModeChanged(v); PostToWeb(new { type = "playMode", value = v }); },
        IsLyricOn = () => AppServices.Config.Get("App", "desktop_lyric", "false").Equals("true", StringComparison.OrdinalIgnoreCase),
        SetLyric = on => SetDesktopLyric(on)
    };
}