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
    // ---- 记住上一次播放进度（[Player] last_song_id / last_pos）----
    private long _resumeSavedSongId = -1;   // 已经写盘的曲目 id
    private long _resumeSavedPos = -1;      // 已经写盘的位置（毫秒）
    private long _lastFrontPos = 0;         // 前端最近一次上报的位置
    private long _lastFrontDur = 0;         // 前端最近一次上报的时长

    /// <summary>只更新“当前第几首”（很小，可以在每次换歌时写）。</summary>
    private void SavePlaylistIndex(int index)
    {
        try { AppServices.Config.PlaylistIndex = Math.Max(0, index); }
        catch (Exception e) { LogManager.Debug("保存播放下标失败: " + e.Message); }
    }

    /// <summary>
    /// 记住"上次听到哪"：换歌或位移超过 5 秒才写盘（audio_state 约 1 次/秒，不能每次都写）。
    /// 快播完（距结尾 3 秒内）不记，避免下次启动续播到结尾。
    /// </summary>
    private void SaveResumeProgress(long pos, long dur, bool force)
    {
        try
        {
            var s = AppServices.Player.Current;
            if (s is null || pos < 0) return;
            if (dur > 0 && pos > dur - 3000) return;
            bool songChanged = s.Id != _resumeSavedSongId;
            if (!force && !songChanged && Math.Abs(pos - _resumeSavedPos) < 5000) return;
            if (songChanged) AppServices.Config.LastSongId = s.Id;
            AppServices.Config.LastPosition = Math.Max(0, pos);
            _resumeSavedSongId = s.Id;
            _resumeSavedPos = pos;
            if (songChanged) LogManager.Debug("[续播] 已记录曲目: id=" + s.Id + " pos=" + (pos / 1000) + "s");
        }
        catch (Exception e) { LogManager.Debug("保存播放进度失败: " + e.Message); }
    }

    /// <summary>把当前播放队列写进 player.json，重启后可以恢复。</summary>
    private void SavePlaylist(int index)
    {
        try
        {
            var q = AppServices.Player.Queue;
            AppServices.Config.SaveQueue(q);
            AppServices.Config.PlaylistIndex = Math.Max(0, index);
            LogManager.Debug("播放列表已保存: " + q.Count + " 首 @ " + index);
        }
        catch (Exception e) { LogManager.Debug("保存播放列表失败: " + e.Message); }
    }
}