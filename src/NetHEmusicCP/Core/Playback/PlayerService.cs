using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using netHEmusic.Core;
using netHEmusic.Core.Cache;
using netHEmusic.Core.Config;
using netHEmusic.Core.Download;
using netHEmusic.Core.Logging;
using netHEmusic.Core.Model;

namespace netHEmusic.Core.Playback;

/// <summary>
/// 播放服务：MediaPlayer + SMTC（系统媒体控件）+ 三首内存环 + 磁盘缓存预取。
/// SMTC 向 Windows 媒体飞窗（音量/媒体控制）投放 标题/歌手/专辑/封面 并响应播放控制。
/// </summary>
public sealed class PlayerService
{
    private readonly AppConfig _config;
    private readonly MediaPlayer _player = new();
    private readonly SystemMediaTransportControls _smtc;
    private List<Song> _queue = new();
    private int _index = -1;

    /// <summary>3 首内存环：prev/current/next 的音源（Uri 或本地文件）。</summary>
    private readonly Dictionary<long, string> _memoryRing = new();

    public event Action<Song?>? SongChanged;
    public event Action<bool>? PlaybackChanged;
    public event Action<TimeSpan>? PositionChanged;
    public event Action<int, int>? QueueChanged; // (index, count)

    public PlayerService(AppConfig config, CacheManager cache)
    {
        _config = config;
        _player.AudioCategory = MediaPlayerAudioCategory.Media;
        _player.Volume = config.Volume / 100.0;
        _player.PlaybackSession.PositionChanged += (s, e) => { if (s is MediaPlaybackSession mps) PositionChanged?.Invoke(mps.Position); };
        _player.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
        _player.MediaEnded += (s, e) => { LogManager.Log("播放结束，按播放模式自动切歌（" + Mode + "）"); _ = AutoNextAsync(); };
        _player.MediaFailed += (s, e) => { LogManager.Error("播放失败: " + e.ErrorMessage); _ = NextAsync(); };

        _smtc = _player.SystemMediaTransportControls;
        _smtc.IsEnabled = true;
        _smtc.IsPlayEnabled = true;
        _smtc.IsPauseEnabled = true;
        _smtc.IsNextEnabled = true;
        _smtc.IsPreviousEnabled = true;
        _smtc.DisplayUpdater.Type = MediaPlaybackType.Music;
        _smtc.ButtonPressed += OnSmtcButton;
    }

    private void OnPlaybackStateChanged(MediaPlaybackSession sender, object args)
    {
        bool playing = sender.PlaybackState == MediaPlaybackState.Playing;
        PlaybackChanged?.Invoke(playing);
        try { _smtc.PlaybackStatus = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused; } catch { }
    }

    private void OnSmtcButton(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        switch (args.Button)
        {
            case SystemMediaTransportControlsButton.Play: Play(); break;
            case SystemMediaTransportControlsButton.Pause: Pause(); break;
            case SystemMediaTransportControlsButton.Next: _ = NextAsync(); break;
            case SystemMediaTransportControlsButton.Previous: _ = PrevAsync(); break;
        }
    }

    public bool Playing => _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
    public Song? Current => _index >= 0 && _index < _queue.Count ? _queue[_index] : null;
    public int Index => _index;
    public int Count => _queue.Count;
    public List<Song> Queue => _queue;

    /// <summary>设置播放队列并从 index 开始。预热 3 首内存环。</summary>
    public async Task LoadQueueAsync(List<Song> songs, int startIndex = 0)
    {
        _queue = songs ?? new();
        _index = _queue.Count > 0 ? Math.Clamp(startIndex, 0, _queue.Count - 1) : -1;
        QueueChanged?.Invoke(_index, _queue.Count);
        if (_index >= 0)
        {
            await PlayCurrentAsync();
            _ = PrefetchRingAsync();
        }
    }

    /// <summary>恢复上次保存的播放队列（只装载、不自动播放），供软件重启后维持播放列表。</summary>
    public void RestoreQueue(List<Song> songs, int index)
    {
        _queue = songs ?? new();
        _index = _queue.Count > 0 ? Math.Clamp(index, 0, _queue.Count - 1) : -1;
        QueueChanged?.Invoke(_index, _queue.Count);
    }

    public async Task PlayAsync() => await PlayCurrentAsync();
    public void Play() => _player.Play();
    public void Pause() => _player.Pause();
    public async Task ToggleAsync() { if (Playing) Pause(); else await PlayCurrentAsync(); }

    public async Task NextAsync() => await StepAsync(1);
    public async Task PrevAsync() => await StepAsync(-1);

    /// <summary>播放模式：order 顺序 / list 列表循环 / single 单曲循环 / random 随机。</summary>
    public string Mode => (_config.Get("Player", "mode", "order") ?? "order").ToLowerInvariant();

    private readonly Random _rand = new();

    private async Task StepAsync(int dir)
    {
        if (_queue.Count == 0 || _index < 0) return;
        _index = (_index + dir + _queue.Count) % _queue.Count;
        await PlayCurrentAsync();
        _ = PrefetchRingAsync();
    }

    /// <summary>一首播完后按当前模式决定下一首。</summary>
    private async Task AutoNextAsync()
    {
        if (_queue.Count == 0 || _index < 0) return;
        switch (Mode)
        {
            case "single":                                  // 单曲循环：重播当前
                await PlayAtAsync(_index);
                return;
            case "random":                                  // 随机：随机挑一首（避免连续同一首）
                if (_queue.Count == 1) { await PlayAtAsync(0); return; }
                int next;
                do { next = _rand.Next(_queue.Count); } while (next == _index);
                await PlayAtAsync(next);
                return;
            case "order":                                   // 顺序：到最后一首就停
                if (_index >= _queue.Count - 1) { LogManager.Log("顺序播放已到最后一首"); Pause(); return; }
                await StepAsync(1);
                return;
            default:                                        // 列表循环
                await StepAsync(1);
                return;
        }
    }

    public async Task PlayAtAsync(int index)
    {
        if (index < 0 || index >= _queue.Count) return;
        _index = index;
        await PlayCurrentAsync();
        _ = PrefetchRingAsync();
    }

    /// <summary>播放索引对应的歌曲。源优先内存环缓存文件，其次即时直链流。</summary>
    private async Task PlayCurrentAsync()
    {
        var s = Current;
        if (s is null) return;
        SongChanged?.Invoke(s);

        try
        {
            Uri? uri = null;
            if (_memoryRing.TryGetValue(s.Id, out var cached) && !string.IsNullOrEmpty(cached) && Uri.TryCreate(cached, UriKind.Absolute, out var cu)) uri = cu;
            if (uri is null)
            {
                var sm = await AppServices.Netease.SongUrl(s.Id.ToString(), DownloadManager.BitRate(_config.Quality));
                var u = sm.Values.FirstOrDefault(v => !string.IsNullOrEmpty(v));
                if (string.IsNullOrEmpty(u)) { LogManager.Warn("无播放地址 id=" + s.Id); return; }
                uri = new Uri(u);
            }

            var src = MediaSource.CreateFromUri(uri);
            var item = new MediaPlaybackItem(src);
            var props = item.GetDisplayProperties();
            props.Type = MediaPlaybackType.Music;
            props.MusicProperties.Title = s.Title;
            props.MusicProperties.Artist = s.ArtistsName;
            props.MusicProperties.AlbumTitle = s.AlbumName;
            if (!string.IsNullOrEmpty(s.PicUrl)) { try { props.Thumbnail = RandomAccessStreamReference.CreateFromUri(new Uri(s.PicUrl)); } catch { } }
            item.ApplyDisplayProperties(props);

            _player.Source = item;
            _player.Play();
            LogManager.Log("开始播放: " + s.DisplayName);
            UpdateSmtc(s);
        }
        catch (Exception e) { LogManager.Error("播放失败: " + e.Message); }
    }

    private void UpdateSmtc(Song s)
    {
        try
        {
            _smtc.DisplayUpdater.Type = MediaPlaybackType.Music;
            _smtc.DisplayUpdater.MusicProperties.Title = s.Title;
            _smtc.DisplayUpdater.MusicProperties.Artist = s.ArtistsName;
            _smtc.DisplayUpdater.MusicProperties.AlbumTitle = s.AlbumName;
            if (!string.IsNullOrEmpty(s.PicUrl)) { try { _smtc.DisplayUpdater.Thumbnail = RandomAccessStreamReference.CreateFromUri(new Uri(s.PicUrl)); } catch { } }
            _smtc.DisplayUpdater.Update();
        }
        catch { }
    }

    public void SetVolume(int v)
    {
        _config.Volume = v;
        _player.Volume = v / 100.0;
    }
    public int GetVolume() => _config.Volume;
    public TimeSpan Position => _player.PlaybackSession.Position;
    public void Seek(TimeSpan t) { try { _player.PlaybackSession.Position = t; } catch { } }

    /// <summary>把当前索引相邻的三首（上/当前/下）解析出播放直链并填入内存环；其它歌曲由磁盘缓存按需加载。</summary>
    private async Task PrefetchRingAsync()
    {
        if (_queue.Count == 0 || _index < 0) return;
        var ids = new List<long>();
        if (_index - 1 >= 0) ids.Add(_queue[_index - 1].Id);
        ids.Add(_queue[_index].Id);
        if (_index + 1 < _queue.Count) ids.Add(_queue[_index + 1].Id);

        foreach (var id in ids)
        {
            if (_memoryRing.ContainsKey(id)) continue;
            try
            {
                var sm = await AppServices.Netease.SongUrl(id.ToString(), DownloadManager.BitRate(_config.Quality));
                var u = sm.Values.FirstOrDefault(v => !string.IsNullOrEmpty(v));
                _memoryRing[id] = u ?? "";
                LogManager.Debug("已解析播放地址 id=" + id);
            }
            catch { _memoryRing[id] = ""; }
        }
    }
}
