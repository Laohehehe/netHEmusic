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

    /// <summary>每首歌已重试过几次（直链失效时用；换一首歌或重新播到它就清零，避免无限重试）。</summary>
    private readonly Dictionary<long, int> _retryCount = new();
    private long _lastHandedOffSongId = -1;
    /// <summary>实际交给前端的那条直链（失败取证用；可能是新解析的，不一定在内存环里）。</summary>
    private string _lastHandedUrl = "";
    private long _lastHandedUrlSongId = -1;
    /// <summary>本地缓存音频播不出来的歌（别再走本地文件，否则联网时也播不了）。</summary>
    private readonly HashSet<long> _badLocal = new();

    public event Action<Song?>? SongChanged;
    public event Action<bool>? PlaybackChanged;
    public event Action<TimeSpan>? PositionChanged;
    public event Action<int, int>? QueueChanged; // (index, count)

    /// <summary>前端播放模式：音频交给网页 <audio> 播（可拿 Web Audio 频谱）。</summary>
    public bool FrontendAudio => !string.Equals(_config.Get("Player", "frontend_audio", "true"), "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>请求前端播放某条直链。</summary>
    public event Action<FrontendAudioLoad>? FrontendLoad;
    /// <summary>给前端播放器下命令（play/pause/seek/volume/stop）。</summary>
    public event Action<string, long>? FrontendCommand;

    private volatile bool _frontendPlaying;
    private int _frontendLoadedIndex = -1;      // 已经下发给网页播放器的曲目下标（-1 = 网页还没有音频）

    /// <summary>网页重载后调用：网页里的播放器已经清空。</summary>
    public void ResetFrontendAudio() { _frontendLoadedIndex = -1; _frontendPlaying = false; }

    // ---- 记住上一次播放进度：启动时登记，只在随后第一次加载到同一首时生效一次 ----
    private long _resumeSongId = -1;
    private long _resumeMs;
    private long _pendingSeekMs;               // 原生播放模式：媒体打开后再跳

    /// <summary>登记"上次听到哪"。仅用于下一次真正加载该曲目时续播。</summary>
    public void ArmResume(long songId, long positionMs)
    {
        _resumeSongId = songId;
        _resumeMs = positionMs;
    }

    /// <summary>取出并消费续播位置：只认登记的曲目、只生效一次（无论是否命中都清空）。</summary>
    private long TakeResumeMs(long songId)
    {
        var id = _resumeSongId;
        var ms = _resumeMs;
        if (id < 0 || ms < 1000) return 0;      // 没登记 / 位置太靠前，不值得续播
        _resumeSongId = -1;
        _resumeMs = 0;
        return id == songId ? ms : 0;
    }

    /// <summary>网页侧上报的播放状态。</summary>
    public void SetFrontendState(bool playing, long posMs, long durMs)
    {
        _frontendPlaying = playing;
        try { _smtc.PlaybackStatus = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused; } catch { }
        PlaybackChanged?.Invoke(playing);
    }

    public PlayerService(AppConfig config, CacheManager cache)
    {
        _config = config;
        _player.AudioCategory = MediaPlayerAudioCategory.Media;
        _player.Volume = config.Volume / 100.0;
        _player.PlaybackSession.PositionChanged += (s, e) => { if (s is MediaPlaybackSession mps) PositionChanged?.Invoke(mps.Position); };
        _player.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
        _player.MediaEnded += (s, e) => { LogManager.Log("播放结束，按播放模式自动切歌（" + Mode + "）"); _ = AutoNextAsync(); };
        _player.MediaOpened += (s, e) => { var ms = _pendingSeekMs; if (ms > 0) { _pendingSeekMs = 0; try { _player.PlaybackSession.Position = TimeSpan.FromMilliseconds(ms); } catch { } } };
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

    public bool Playing => FrontendAudio ? _frontendPlaying : (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing);
    public Song? Current => _index >= 0 && _index < _queue.Count ? _queue[_index] : null;
    public int Index => _index;
    public int Count => _queue.Count;
    public List<Song> Queue => _queue;

    /// <summary>前端上报的离线状态（navigator.onLine）。离线时不预取、不缓存、不重试——否则每次播放失败都会
    /// 触发「重新解析直链 → 再失败」的循环，CPU 和请求数会爆炸。</summary>
    public static bool Offline { get; set; }

    /// <summary>设置播放队列并从 index 开始。预热 3 首内存环。</summary>
    public async Task LoadQueueAsync(List<Song> songs, int startIndex = 0)
    {
        var (list, idx, removed) = Dedupe(songs, startIndex);
        if (removed > 0) LogManager.Log($"播放队列自动去重：{songs?.Count ?? 0} → {list.Count}（清掉 {removed} 条重复）");
        _queue = list;
        _index = _queue.Count > 0 ? Math.Clamp(idx, 0, _queue.Count - 1) : -1;
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
        var (list, idx, removed) = Dedupe(songs, index);
        if (removed > 0) LogManager.Log($"播放队列自动去重：{songs?.Count ?? 0} → {list.Count}（清掉 {removed} 条重复）");
        _queue = list;
        _index = _queue.Count > 0 ? Math.Clamp(idx, 0, _queue.Count - 1) : -1;
        QueueChanged?.Invoke(_index, _queue.Count);
    }

    /// <summary>按 id 去重（保留第一次出现），并把「当前播放的那首」重新对准到去重后的新下标。
    /// 老版本的追加逻辑没去重，队列里会积下同一批歌被重复追加多份的记录（user 的 player.json 里就有 178 条）。</summary>
    public static (List<Song> list, int index, int removed) Dedupe(List<Song>? songs, int index)
    {
        var src = songs ?? new List<Song>();
        var outp = new List<Song>(src.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? want = (index >= 0 && index < src.Count) ? KeyOf(src[index]) : null;
        foreach (var s in src) { if (s is null) continue; if (seen.Add(KeyOf(s))) outp.Add(s); }
        int ni = -1;
        if (want is not null) { for (int i = 0; i < outp.Count; i++) if (KeyOf(outp[i]) == want) { ni = i; break; } }
        if (ni < 0 && outp.Count > 0) ni = Math.Clamp(index, 0, outp.Count - 1);
        return (outp, ni, src.Count - outp.Count);
    }

    private static string KeyOf(Song s)
    {
        if (s.Id > 0) return "i" + s.Id;
        var a = s.Artists is { Count: > 0 } ? string.Join("/", s.Artists.Select(x => x.Name))
              : (s.Ar is { Count: > 0 } ? string.Join("/", s.Ar.Select(x => x.Name)) : "");
        return "n" + (s.Title ?? "") + "\u0001" + a;
    }

    /// <summary>开始播放当前曲目（若已加载过媒体则从当前位置继续）。</summary>
    public async Task PlayAsync() { if (ResumeIfLoaded()) return; await PlayCurrentAsync(); }

    public void Play()
    {
        if (FrontendAudio)
        {
            if (_index >= 0 && _frontendLoadedIndex == _index) FrontendCommand?.Invoke("play", 0);
            else _ = PlayCurrentAsync();      // 网页里没有这首的音频（刚启动/刚重载）→ 重新解析直链下发
            return;
        }
        if (!ResumeIfLoaded()) _player.Play();
    }

    public void Pause()
    {
        if (FrontendAudio) { FrontendCommand?.Invoke("pause", 0); return; }
        _player.Pause();
    }

    /// <summary>播放/暂停切换：暂停后继续播放【不能重建媒体源】，否则进度会归零。</summary>
    public async Task ToggleAsync()
    {
        // 前端模式：自己不发命令就什么都不会发生（进度由网页持有）
        if (FrontendAudio)
        {
            if (Playing) { FrontendCommand?.Invoke("pause", 0); return; }
            if (_index >= 0 && _frontendLoadedIndex == _index) FrontendCommand?.Invoke("play", 0);
            else await PlayCurrentAsync();    // 重启软件后网页是空的，必须先重新加载直链
            return;
        }
        if (Playing) { Pause(); return; }
        if (ResumeIfLoaded()) return;
        await PlayCurrentAsync();
    }

    /// <summary>已经加载过媒体就直接从当前位置继续（播完了则从头开始）。返回是否已处理。</summary>
    private bool ResumeIfLoaded()
    {
        try
        {
            if (_player.Source is null) return false;
            var s = _player.PlaybackSession;
            if (s is null) return false;
            if (s.NaturalDuration > TimeSpan.Zero && s.Position >= s.NaturalDuration - TimeSpan.FromMilliseconds(400))
                s.Position = TimeSpan.Zero;          // 已经播完 → 从头再来
            _player.Play();
            return true;
        }
        catch (Exception e) { LogManager.Debug("继续播放失败，改为重新加载: " + e.Message); return false; }
    }

    public async Task NextAsync() => await StepAsync(1);
    public async Task PrevAsync() => await StepAsync(-1);

    /// <summary>播放模式：order 顺序 / list 列表循环 / single 单曲循环 / random 随机。</summary>
    public string Mode => (_config.PlayMode ?? "order").ToLowerInvariant();

    private readonly Random _rand = new();

    /// <summary>切换播放模式时的队列处理：切到 random = 把整份列表打乱一次（伪随机：之后按打乱后的顺序依次放，
    /// 一轮里每首只放一遍）；切回其它模式 = 恢复打乱前的原始顺序。原始顺序存进 player.json，重启也不丢。</summary>
    public void OnModeChanged(string mode)
    {
        try
        {
            mode = (mode ?? "").ToLowerInvariant();
            bool wantShuffle = mode == "random";
            bool shuffled = _config.QueueOriginal is { Count: > 0 };
            if (wantShuffle == shuffled) return;                    // 已经是目标状态，别重复打乱
            var cur = Current;
            if (wantShuffle)
            {
                if (_queue.Count < 2) return;
                _config.QueueOriginal = new List<Song>(_queue);
                for (int i = _queue.Count - 1; i > 0; i--) { int j = _rand.Next(i + 1); (_queue[i], _queue[j]) = (_queue[j], _queue[i]); }
                LogManager.Log("已打乱播放列表（" + _queue.Count + " 首）");
            }
            else
            {
                var orig = _config.QueueOriginal ?? new List<Song>();
                var keys = new HashSet<string>(orig.ConvertAll(KeyOf));
                foreach (var s in _queue) if (s is not null && keys.Add(KeyOf(s))) orig.Add(s);   // 打乱期间新加的歌接在后面，不丢
                _queue = orig;
                _config.QueueOriginal = null;
                LogManager.Log("已恢复打乱前的播放顺序（" + _queue.Count + " 首）");
            }
            _index = cur is null ? (_queue.Count > 0 ? 0 : -1) : Math.Max(0, _queue.FindIndex(x => x.Id == cur.Id));
            _config.SaveQueue(_queue);
            _config.PlaylistIndex = Math.Max(0, _index);
            QueueChanged?.Invoke(_index, _queue.Count);
        }
        catch (Exception e) { LogManager.Debug("切换随机播放失败: " + e.Message); }
    }

    private async Task StepAsync(int dir)
    {
        if (_queue.Count == 0 || _index < 0) return;
        // 随机模式下队列已经在切换时打乱好了，这里按打乱后的顺序往下走（= 伪随机，一轮每首放一遍）
        if (Mode == "single" && dir > 0)
        {
            // 单曲循环：手动“下一首”仍然换歌，但保持单曲循环设置
            _index = (_index + 1) % _queue.Count;
        }
        else
        {
            _index = (_index + dir + _queue.Count) % _queue.Count;
        }
        await PlayCurrentAsync();
        _ = PrefetchRingAsync();
    }

    /// <summary>一首播完后按当前模式决定下一首（前端播放结束时由网页触发）。</summary>
    public async Task AutoNextAsync()
    {
        if (_queue.Count == 0 || _index < 0) return;
        switch (Mode)
        {
            case "single":                                  // 单曲循环：重播当前
                await PlayAtAsync(_index);
                return;
            case "random":                                  // 随机：队列在切模式时已打乱，按打乱后的顺序循环即可
                await StepAsync(1);
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

    /// <summary>清空播放队列。</summary>
    public void ClearQueue()
    {
        try { _player.Pause(); } catch { }
        _queue.Clear();
        _index = -1;
        QueueChanged?.Invoke(_index, 0);
    }

    /// <summary>从队列里移除一首（正在播放的那首只调整下标，不打断当前播放）。</summary>
    public void RemoveAt(int index)
    {
        if (index < 0 || index >= _queue.Count) return;
        bool wasCurrent = index == _index;
        _queue.RemoveAt(index);
        if (_queue.Count == 0) { try { _player.Pause(); } catch { } _index = -1; }
        else if (index < _index) _index--;
        else if (wasCurrent && _index >= _queue.Count) _index = _queue.Count - 1;
        QueueChanged?.Invoke(_index, _queue.Count);
    }

    /// <summary>把队列里的第 index 首挪到“下一首播放”的位置（即当前曲目之后）。</summary>
    public void MoveToNext(int index)
    {
        if (index < 0 || index >= _queue.Count || _index < 0 || index == _index) return;
        var s = _queue[index];
        _queue.RemoveAt(index);
        if (index < _index) _index--;                     // 移走的是前面的歌，当前下标左移
        int at = Math.Min(_index + 1, _queue.Count);       // 插到当前之后
        _queue.Insert(at, s);
        QueueChanged?.Invoke(_index, _queue.Count);
        LogManager.Log("已把《" + s.Title + "》设为下一首播放");
    }

    public async Task PlayAtAsync(int index)
    {
        if (index < 0 || index >= _queue.Count) return;
        _index = index;
        await PlayCurrentAsync();
        _ = PrefetchRingAsync();
    }

    /// <summary>前端播放任务载荷（曲目 / 直链 / 队列下标 / 续播位置毫秒）。</summary>
    public sealed record FrontendAudioLoad(Model.Song Song, string Url, int Index, long StartMs = 0);

    /// <summary>播放索引对应的歌曲。源优先内存环缓存文件，其次即时直链流。</summary>
    private async Task PlayCurrentAsync()
    {
        var s = Current;
        if (s is null) return;
        SongChanged?.Invoke(s);

        try
        {
            // 解析播放直链（内存环缓存优先，其次即时请求）。
            // ⚠ 网易直链有时效（/song/url 返回的 expi，实测约 20 分钟），缓存命中不等于还能用，
            //   所以播放失败（前端 code 4）时会清掉缓存重试一次，见 RetryCurrentAsync。
            string? url = null;
            var fromCache = false;
            if (_memoryRing.TryGetValue(s.Id, out var cached) && !string.IsNullOrEmpty(cached)) { url = cached; fromCache = true; }
            if (string.IsNullOrEmpty(url))
            {
                var sm = await AppServices.Netease.SongUrl(s.Id.ToString(), DownloadManager.BitRate(_config.Quality));
                url = sm.Values.FirstOrDefault(v => !string.IsNullOrEmpty(v));
            }
            if (string.IsNullOrEmpty(url)) { LogManager.Warn("无播放地址 id=" + s.Id); return; }

            var startMs = TakeResumeMs(s.Id);   // 续播位置（仅启动后第一次、且就是这首）

            // 换到别的歌 = 新一轮播放 → 这首歌的重试额度清零（同一首连续失败时不会无限重试）
            if (_lastHandedOffSongId != s.Id) { _retryCount.Remove(s.Id); _lastHandedOffSongId = s.Id; }

            // 前端播放模式：直链交给网页，由 <audio> + Web Audio 播放（可拿真实频谱）
            if (FrontendAudio)
            {
                // 离线播放：本地已缓存就直接播本地文件（由 C# 拦截 https://mediacache/* 回文件），
                // 否则用在线直链并在后台缓存一份。_badLocal 里的歌说明本地文件播不了（文件坏了/通道没起来），
                // 就不要再走本地，免得联网时也一起播不出来。
                var local = _badLocal.Contains(s.Id) ? null : AppServices.Cache.FindAudio(s.Id);
                if (local is not null)
                {
                    url = AppServices.Cache.AudioVirtualUrl(s.Id, System.IO.Path.GetExtension(local));
                    LogManager.Log("离线缓存命中，本地播放: " + s.DisplayName);
                }
                else
                {
                    if (!Offline) _ = AppServices.Cache.CacheAudioAsync(url, s.Id);   // 离线时别再发起下载
                }
                UpdateSmtc(s);
                _frontendLoadedIndex = _index;
                _lastHandedUrl = url; _lastHandedUrlSongId = s.Id;   // 失败取证要探这一条
                FrontendLoad?.Invoke(new FrontendAudioLoad(s, url, _index, startMs));
                LogManager.Log("交给前端播放: " + s.DisplayName + (fromCache ? "（直链来自缓存）" : "（直链新解析）") + (startMs > 0 ? "（续播 " + (startMs / 1000) + "s）" : ""));
                return;
            }

            var uri = new Uri(url);
            var src = MediaSource.CreateFromUri(uri);
            var item = new MediaPlaybackItem(src);
            var props = item.GetDisplayProperties();
            props.Type = MediaPlaybackType.Music;
            props.MusicProperties.Title = s.Title;
            props.MusicProperties.Artist = s.ArtistsName;
            props.MusicProperties.AlbumTitle = s.AlbumName;
            if (!string.IsNullOrEmpty(s.PicUrl)) { try { props.Thumbnail = RandomAccessStreamReference.CreateFromUri(new Uri(s.PicUrl)); } catch { } }
            item.ApplyDisplayProperties(props);

            if (startMs > 0) _pendingSeekMs = startMs;   // 媒体打开后再跳（此刻还没 open）
            _player.Source = item;
            _player.Play();
            LogManager.Log("开始播放: " + s.DisplayName + (startMs > 0 ? "（续播 " + (startMs / 1000) + "s）" : ""));
            UpdateSmtc(s);
        }
        catch (Exception e) { LogManager.Error("播放失败: " + e.Message); }
    }

    private void UpdateSmtc(Song s)
    {
        try
        {
            _smtc.DisplayUpdater.Type = MediaPlaybackType.Music;
            try { _smtc.DisplayUpdater.AppMediaId = "netHEmusic"; } catch { }
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
        if (FrontendAudio) FrontendCommand?.Invoke("volume", v);
    }
    public int GetVolume() => _config.Volume;
    public TimeSpan Position => _player.PlaybackSession.Position;

    /// <summary>当前音频的真实时长（媒体会话给的值最准，前端进度条用）。</summary>
    public TimeSpan Duration
    {
        get
        {
            try { var d = _player.PlaybackSession.NaturalDuration; if (d > TimeSpan.Zero) return d; } catch { }
            var c = Current;
            return c is null ? TimeSpan.Zero : TimeSpan.FromMilliseconds(c.Duration);
        }
    }

    /// <summary>
    /// 前端报「源不支持」（错误码 4，多半是直链过期）时调一次：
    /// 丢掉这首歌的缓存直链 → 重新解析 → 重放同一首。每首歌只重试一次，
    /// 返回 false 表示别再试了（调用方去切歌，避免无限循环）。
    /// </summary>
    public async Task<bool> RetryCurrentAsync()
    {
        // 离线：重试必然再次失败（解析直链要联网），直接停下，避免请求风暴与 CPU 飙升
        if (Offline) { LogManager.Log("离线状态：不再重试当前曲目（已暂停）"); Pause(); return false; }
        var s = Current;
        if (s is null) return false;
        _retryCount.TryGetValue(s.Id, out var n);
        if (n >= 1) return false;
        _retryCount[s.Id] = n + 1;
        // 取证：探"实际交给前端的那条直链"（可能是新解析的、也可能来自内存环），看 CDN 当时回了什么
        var bad = (_lastHandedUrlSongId == s.Id && !string.IsNullOrEmpty(_lastHandedUrl))
            ? _lastHandedUrl
            : (_memoryRing.TryGetValue(s.Id, out var cachedUrl) ? cachedUrl : "");
        if (!string.IsNullOrEmpty(bad)) _ = ProbeUrlAsync(bad);
        // 本地缓存文件播不了（通道没起来 / 文件损坏）→ 标记这首歌以后别再走本地，并立刻用在线直链重试一次
        if (bad.StartsWith("https://mediacache/", StringComparison.OrdinalIgnoreCase))
        {
            _badLocal.Add(s.Id);
            _retryCount[s.Id] = 0;                       // 本地失败不占「直链失效」的重试额度
            LogManager.Warn("本地缓存音频播不了，改回在线直链: " + s.DisplayName);
        }
        _memoryRing.Remove(s.Id);                    // 关键：可能已经过期/失效的直链必须丢掉
        LogManager.Log("[重试] 直链失效，清掉缓存重新解析: " + s.DisplayName);
        await PlayCurrentAsync();                    // 同一首重放，不推进队列
        return true;
    }

    /// <summary>
    /// 诊断用：探测一条"播放失败的直链"实际返回什么（HTTP 状态 / Content-Type / 前几个字节）。
    /// 正常音频应该是 206 + audio/mpeg|flac + ID3/fLaC 之类的魔数；若是 200 + text/html 就说明
    /// CDN 给的是错误页（防盗链/限流），而不是音频。
    /// </summary>
    private static async Task ProbeUrlAsync(string url)
    {
        try
        {
            var host = "";
            try { host = new Uri(url).Host; } catch { }
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 255);
            using var resp = await http.SendAsync(req, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
            var buf = new byte[256];
            int n = 0;
            try { using var st = await resp.Content.ReadAsStreamAsync(); n = await st.ReadAsync(buf, 0, buf.Length); } catch { }
            var magic = new System.Text.StringBuilder();
            for (int i = 0; i < n && i < 8; i++) magic.Append(buf[i] >= 32 && buf[i] < 127 ? (char)buf[i] : '.');
            LogManager.Warn("[诊断] 失败直链探测 host=" + host + " → HTTP " + (int)resp.StatusCode
                + " type=" + resp.Content.Headers.ContentType + " 首字节=" + magic);
        }
        catch (Exception e) { LogManager.Warn("[诊断] 失败直链探测异常: " + e.Message); }
    }

    public void Seek(TimeSpan t)
    {
        if (FrontendAudio) { FrontendCommand?.Invoke("seek", (long)t.TotalMilliseconds); return; }
        try { _player.PlaybackSession.Position = t; } catch { }
    }

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
