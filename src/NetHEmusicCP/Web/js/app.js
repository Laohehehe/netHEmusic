// app.js — YesPlayMusic-style full player
(function () {
  const NE = window.NE; const $ = s => document.querySelector(s);
  const view = $('#view');
  let queue = []; let playingIndex = -1; let currentList = []; let nowPlaying = null; let appSettings = {};

  function el(tag, cls, html) { const e = document.createElement(tag); if (cls) e.className = cls; if (html !== undefined) e.innerHTML = html; return e; }
  function fmt(ms) { if (!ms || ms <= 0) return '00:00'; const s = Math.floor(ms/1000); return String(Math.floor(s/60)).padStart(2,'0')+':'+String(s%60).padStart(2,'0'); }
  function esc(s){ return String(s||'').replace(/[&<>"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c])); }
  // 兼容两种字段：网易云接口的 snake_case（name/ar/al）与 C# 队列 DTO 的 PascalCase（Title/Artist/...）
  function normSong(s) {
    if (!s) return null;
    var artists = (s.ar || s.artists || []).map(function (a) { return a.name; }).join(' / ');
    if (!artists && s.Artist) artists = s.Artist;
    var al = s.al || s.album || {};
    var pic = al.picUrl || al.pic || s.pic || s.Pic || '';
    return {
      Id: (s.id !== undefined && s.id !== null) ? s.id : (s.Id !== undefined ? s.Id : 0),
      Title: s.name || s.Title || '未知歌曲',
      Artist: artists || '未知',
      Album: al.name || s.Album || '',
      Pic: pic,
      Duration: s.dt || s.duration || s.Duration || 0
    };
  }
  // ================= 快捷键 =================
  var HK_DEFAULTS = {
    hk_play:  'Space',                 // 播放 / 暂停
    hk_next:  'Ctrl+Alt+ArrowRight',   // 下一首
    hk_prev:  'Ctrl+Alt+ArrowLeft',    // 上一首
    hk_volup: 'Ctrl+Alt+ArrowUp',      // 音量 +
    hk_voldn: 'Ctrl+Alt+ArrowDown',    // 音量 -
    hk_mute:  'M',                     // 静音
    hk_lyric: 'F',                     // 全窗口歌词页
    hk_close: 'Escape'                 // 收起歌词页
  };
  var HK_LABELS = {
    hk_play: '播放 / 暂停', hk_next: '下一首', hk_prev: '上一首',
    hk_volup: '音量 +', hk_voldn: '音量 -', hk_mute: '静音开关',
    hk_lyric: '打开/收起歌词页', hk_close: '关闭歌词页'
  };
  var hotkeys = {};
  function evKeyName(e) {
    var p = [];
    if (e.ctrlKey) p.push('Ctrl');
    if (e.altKey) p.push('Alt');
    if (e.shiftKey) p.push('Shift');
    var code = e.code || '';
    if (!code) {                                   // 某些输入源不带 code，退回到 key
      var k = e.key || '';
      if (k === ' ' || k === 'Spacebar') code = 'Space';
      else if (k.length === 1) code = k.toUpperCase();
      else code = k;
    }
    if (code.indexOf('Key') === 0) code = code.slice(3);
    else if (code.indexOf('Digit') === 0) code = code.slice(5);
    // 单独的修饰键不作为快捷键
    if (!code || code === 'Control' || code === 'Alt' || code === 'Shift' || code === 'Meta' ||
        code === 'ControlLeft' || code === 'ControlRight' || code === 'AltLeft' || code === 'AltRight' ||
        code === 'ShiftLeft' || code === 'ShiftRight') return '';
    p.push(code);
    return p.join('+');
  }
  function hotkeysFromSettings(s) {
    hotkeys = {};
    for (var k in HK_DEFAULTS) hotkeys[k] = String(cfgGet(s, k, HK_DEFAULTS[k]) || HK_DEFAULTS[k]);
  }
  function isTyping(el) {
    return !!el && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.tagName === 'SELECT' || el.isContentEditable);
  }
  function runHotkey(action) {
    switch (action) {
      case 'hk_play':  NE.post({ type: 'toggle' }); break;
      case 'hk_next':  NE.post({ type: 'next' }); break;
      case 'hk_prev':  NE.post({ type: 'prev' }); break;
      case 'hk_volup': applyVolume((Number($('#pl-volume') && $('#pl-volume').value) || 0) + 5, true); break;
      case 'hk_voldn': applyVolume((Number($('#pl-volume') && $('#pl-volume').value) || 0) - 5, true); break;
      case 'hk_mute': { var cur = Number($('#pl-volume') && $('#pl-volume').value) || 0; applyVolume(cur > 0 ? 0 : (lastVol || 60), true); break; }
      case 'hk_lyric': if (npOpen) closeNowPlaying(); else openNowPlaying(); break;
      case 'hk_close': if (npOpen) closeNowPlaying(); break;
    }
  }

  // 读取 [App] 段设置（s.app.*）：设置页与歌词页共用
  function cfgGet(s, k, def) { var v = (s && s.app) ? s.app[k] : undefined; return (v === undefined || v === null || v === '') ? def : v; }
  function cfgBool(s, k, def) { var v = cfgGet(s, k, def); return String(v) !== 'false' && v !== false; }
  function toast(t) { const el=$('#toast'); el.textContent=t; el.style.display='block'; setTimeout(()=>el.style.display='none',2000); }
  function loading() { view.innerHTML = '<div class="big-load">正在加载…</div>'; }

  function play(ns) { NE.post({ type:'play', song: ns }); setPlayer(ns); }
  function pop(el) { if(!el) return; el.classList.remove('fx-pop'); void el.offsetWidth; el.classList.add('fx-pop'); }
  function setPlaying(on) {
    var p = $('#player'); if(p) p.classList.toggle('playing', !!on);
    var pb = $('#pb-play'); if(pb) pb.innerHTML = on ? SVG.pause : SVG.play;
    npSetPlaying(on);   // 歌词页的播放/暂停按钮同步
  }
  // 只更新歌名/歌手/封面（恢复播放列表时用，不改播放状态）
  function showSongMeta(ns) {
    if(!ns) return;
    $('#pl-title').textContent = ns.Title;
    $('#pl-artist').textContent = ns.Artist;
    var c = $('#pl-cover'), wrap = $('#pl-cover-wrap');
    if(ns.Pic){ c.src = ns.Pic.replace(/\^\d+\^/,''); if(wrap) wrap.classList.add('has-cover'); }
    else { c.removeAttribute('src'); if(wrap) wrap.classList.remove('has-cover'); }
  }
  function setPlayer(ns) { if(!ns) return; showSongMeta(ns); setPlaying(true); pop($('#pb-play')); }

  // ===== 统一图标集：全部描边风格（stroke 2 / round 端点），同一功能只用同一个图标 =====
  function ico(inner) {
    return '<svg viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" '
      + 'stroke-width="2" stroke-linecap="round" stroke-linejoin="round">' + inner + '</svg>';
  }
  var SVG = {
    play:    ico('<path d="M8 5v14l11-7z"/>'),
    pause:   ico('<rect x="7" y="4.5" width="3.6" height="15" rx="1.5"/><rect x="13.4" y="4.5" width="3.6" height="15" rx="1.5"/>'),
    prev:    ico('<polyline points="19 20 9 12 19 4"/><line x1="5" x2="5" y1="19" y2="5"/>'),
    next:    ico('<polyline points="5 4 15 12 5 20"/><line x1="19" x2="19" y1="5" y2="19"/>'),
    // 列表基础图形（用户提供）→ 派生出“添加到列表”和“播放列表”
    list:    ico('<line x1="8" x2="21" y1="6" y2="6"/><line x1="8" x2="21" y1="12" y2="12"/><line x1="8" x2="21" y1="18" y2="18"/><line x1="3" x2="3.01" y1="6" y2="6"/><line x1="3" x2="3.01" y1="12" y2="12"/><line x1="3" x2="3.01" y1="18" y2="18"/>'),
    add:     ico('<line x1="3" x2="14" y1="6" y2="6"/><line x1="3" x2="14" y1="12" y2="12"/><line x1="3" x2="9" y1="18" y2="18"/><line x1="18" x2="18" y1="13.5" y2="22.5"/><line x1="13.5" x2="22.5" y1="18" y2="18"/>'),
    playlist:ico('<line x1="3" x2="14" y1="6" y2="6"/><line x1="3" x2="14" y1="12" y2="12"/><line x1="3" x2="9" y1="18" y2="18"/><path d="M16.5 14.4l6 3.6-6 3.6z"/>'),
    dl:      ico('<path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><polyline points="7 10 12 15 17 10"/><line x1="12" x2="12" y1="15" y2="3"/>'),
    star:    ico('<path d="M12 3.6l2.7 5.4 6 .9-4.3 4.2 1 6-5.4-2.8-5.4 2.8 1-6L3.3 9.9l6-.9z"/>'),
    playNext:ico('<line x1="3" x2="13" y1="6" y2="6"/><line x1="3" x2="11" y1="12" y2="12"/><line x1="3" x2="11" y1="18" y2="18"/><path d="M15 11l6 4-6 4z"/>'),
    trash:   ico('<polyline points="3 6 21 6"/><path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/><path d="M10 11v6"/><path d="M14 11v6"/><path d="M9 6V4a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2"/>'),
    share:   ico('<circle cx="18" cy="5" r="3"/><circle cx="6" cy="12" r="3"/><circle cx="18" cy="19" r="3"/><line x1="8.6" x2="15.4" y1="10.5" y2="6.5"/><line x1="8.6" x2="15.4" y1="13.5" y2="17.5"/>'),
    volOn:   ico('<polygon points="11 5 6 9 2 9 2 15 6 15 11 19 11 5"/><path d="M15.6 8.6a5 5 0 0 1 0 6.8"/><path d="M18.6 5.6a9 9 0 0 1 0 12.8"/>'),
    volMute: ico('<polygon points="11 5 6 9 2 9 2 15 6 15 11 19 11 5"/><line x1="17" y1="9" x2="23" y2="15"/><line x1="23" y1="9" x2="17" y2="15"/>'),
    // 播放模式四态
    modeOrder:  ico('<line x1="4" x2="19" y1="12" y2="12"/><polyline points="14 6 20 12 14 18"/>'),
    modeList:   ico('<polyline points="17 2 21 6 17 10"/><path d="M3 11V9a4 4 0 0 1 4-4h14"/><polyline points="7 22 3 18 7 14"/><path d="M21 13v2a4 4 0 0 1-4 4H3"/>'),
    modeSingle: ico('<polyline points="17 2 21 6 17 10"/><path d="M3 11V9a4 4 0 0 1 4-4h14"/><polyline points="7 22 3 18 7 14"/><path d="M21 13v2a4 4 0 0 1-4 4H3"/><text x="12" y="16" text-anchor="middle" font-size="9" font-weight="700" fill="currentColor" stroke="none">1</text>'),
    modeRandom: ico('<polyline points="16 3 21 3 21 8"/><line x1="4" x2="21" y1="20" y2="3"/><polyline points="21 16 21 21 16 21"/><line x1="15" x2="21" y1="15" y2="21"/><line x1="4" x2="9" y1="4" y2="9"/>')
  };
  function songRow(ns, i) {
    const r = el('div','song-row');
    r.innerHTML = '<div class="sr-idx">'+String(i+1).padStart(2,'0')+'</div>'+
      '<img class="sr-cover" loading="lazy" src="'+(ns.Pic?ns.Pic.replace(/\^\d+\^/,'')+'?param=80y80':'')+'">'+
      '<div class="sr-title">'+esc(ns.Title)+'</div><div class="sr-artist">'+esc(ns.Artist)+'</div>'+
      '<div class="sr-album">'+esc(ns.Album)+'</div><div class="sr-dur">'+fmt(ns.Duration)+'</div>'+
      '<button class="row-btn primary" data-do="play" title="播放">'+SVG.play+'</button>'+
      '<button class="row-btn" data-do="add" title="添加到播放列表">'+SVG.add+'</button>'+
      '<button class="row-btn" data-do="dl" title="下载">'+SVG.dl+'</button>';
    r.querySelector('[data-do=play]').onclick = e => { e.stopPropagation(); play(ns); };
    r.querySelector('[data-do=add]').onclick = e => { e.stopPropagation(); addPlaylist(ns); };
    r.querySelector('[data-do=dl]').onclick = e => { e.stopPropagation(); NE.post({type:'download', song:ns}); };
    r.onclick = () => play(ns);
    return r;
  }
  function addPlaylist(ns) { NE.post({ type:'queue_add', song: ns }); toast('已添加到播放列表: ' + ns.Title); }
  // 播放全部：把整个列表作为播放队列，并从第 start 首开始
  function addQueue(list, start) { if (!list || !list.length) return; NE.post({ type:'play_list', songs: list, index: start || 0 }); toast('已加入播放队列 (' + list.length + ' 首)'); }
  function playlistCard(p) {
    const c = el('div','pl-card');
    c.innerHTML = '<img loading="lazy" src="'+(p.coverImgUrl||p.picUrl||'').replace(/\^\d+\^/,'')+'?param=200y200"><div class="plc-name">'+esc(p.name||'')+'</div><div class="plc-count">'+(p.trackCount||'')+' 首</div>';
    c.onclick = () => go('playlist', { id:p.id, name:p.name });
    return c;
  }
  function renderSongs(arr, container) { currentList = arr.map(normSong); container.innerHTML=''; container.classList.add('song-list'); currentList.forEach((ns,i)=>container.appendChild(songRow(ns, i))); return currentList; }

  // 选项卡切换动画：新内容渲染完后播一次入场（淡入 + 轻微上移）
  function animateView() {
    var el = view.firstElementChild; if (!el) return;
    el.classList.remove('view-in'); void el.offsetWidth; el.classList.add('view-in');
  }
  async function go(viewName, data) {
    try {
      var task = null;
      if (viewName==='home') task = goHome();
      else if (viewName==='recommend') task = goRecommend();
      else if (viewName==='toplist') task = goToplist();
      else if (viewName==='playlist') task = (data && data.id) ? goPlaylist(data.id, data.name) : goPlaylists();
      else if (viewName==='search') task = goSearch(data);
      else if (viewName==='lyric') task = goLyric();
      else if (viewName==='account') task = goAccount();
      else if (viewName==='settings') task = goSettings();
      else if (viewName==='liked') task = goLiked();
      if (task) { await task; animateView(); }
    } catch(e) { view.innerHTML = '<div class="big-load">加载失败: '+esc(e.message)+'</div>'; animateView(); }
  }

  async function goHome() {
    loading();
    const [ r, st ] = await Promise.all([ NE.recommend(), NE.loginStatus().catch(function(){ return {}; }) ]);
    const songs = (r.data && r.data.dailySongs) || [];
    var prof = (st && st.data && st.data.profile) || (st && st.profile) || {};
    var nick = prof.nickname || '朋友';
    var h = new Date().getHours();
    var tw = h < 5 ? '凌晨' : h < 9 ? '早' : h < 12 ? '上午' : h < 14 ? '中午' : h < 18 ? '下午' : '晚上';
    const html = el('div','page');
    html.appendChild(el('h2','greet-title', esc(nick) + '，' + tw + '好！想听点什么？'));
    html.appendChild(el('h3','sec-title','每日推荐'));
    const btn = el('button','action-btn','播放全部');
    btn.onclick = ()=>{ addQueue(songs.map(normSong), 0); };
    html.appendChild(btn);
    const dl = el('div');
    renderSongs(songs, dl); html.appendChild(dl);
    view.innerHTML=''; view.appendChild(html);
  }
  async function goRecommend() {
    loading();
    const r = await NE.recommend();
    const songs = (r.data && r.data.dailySongs) || [];
    const html = el('div','page');
    html.appendChild(el('h2','page-title','每日推荐 ('+songs.length+')'));
    const btn = el('button','action-btn','播放全部');
    btn.onclick = ()=>{ addQueue(songs.map(normSong), 0); };
    html.appendChild(btn);
    const dl = el('div'); renderSongs(songs, dl); html.appendChild(dl);
    view.innerHTML=''; view.appendChild(html);
  }
  async function goToplist() {
    loading();
    const r = await NE.topList();
    const html = el('div','page');
    html.appendChild(el('h2','page-title','排行榜'));
    const grid = el('div','pl-grid');
    (r.list||[]).forEach(t => grid.appendChild(playlistCard({ id:t.id, name:t.name, coverImgUrl:t.coverImgUrl, trackCount:t.trackCount })));
    html.appendChild(grid); view.innerHTML=''; view.appendChild(html);
  }
  async function goPlaylists() {
    loading();
    const r = await NE.topPlaylist('全部', 30);
    const html = el('div','page');
    html.appendChild(el('h2','page-title','歌单'));
    const grid = el('div','pl-grid');
    (r.playlists||[]).forEach(p=>grid.appendChild(playlistCard(p)));
    html.appendChild(grid); view.innerHTML=''; view.appendChild(html);
  }
  async function goPlaylist(id, name) {
    loading();
    const [ tracks ] = await Promise.all([ NE.playlistTracks(id, 1000, 0) ]);
    const songs = tracks.songs || [];
    const html = el('div','page');
    html.appendChild(el('h2','page-title', name || '歌单'));
    const btn = el('button','action-btn','播放全部'); btn.onclick=()=>{ addQueue(songs.map(normSong), 0); };
    html.appendChild(btn);
    const dl = el('div'); renderSongs(songs, dl); html.appendChild(dl);
    view.innerHTML=''; view.appendChild(html);
  }
  async function goSearch(data) {
    loading();
    const html = el('div','page');
    html.appendChild(el('h2','page-title','搜索'));
    const box = el('div','search-row');
    const input = el('input','search-input'); input.placeholder='搜索音乐、歌手、专辑'; if(data&&data.kw) input.value=data.kw;
    const btn = el('button','action-btn','搜索');
    const doSearch = async () => {
      const kw = input.value.trim(); if(!kw) return;
      loading();
      try { const r = await NE.search(kw, 50, 0); const songs=(r.result && r.result.songs)||[]; html.innerHTML=''; html.appendChild(el('h2','page-title','“'+esc(kw)+'” ('+songs.length+')')); const dl=el('div'); renderSongs(songs,dl); html.appendChild(dl); view.innerHTML=''; view.appendChild(html); }
      catch(e){ toast('搜索失败: '+e.message); }
    };
    btn.onclick = doSearch; input.onkeydown = e => { if(e.key==='Enter') doSearch(); };
    box.appendChild(input); box.appendChild(btn); html.appendChild(box);
    try { const h = await NE.searchHot(); const hots=(h.result&&h.result.hots)||[]; const hw=el('div','hot-wrap'); hw.appendChild(el('h3','sec-title','热门搜索')); const hg=el('div','hot-grid'); hots.slice(0,20).forEach(it=>{ const t=el('span','hot-item',esc(it.first||it.keyword||'')); t.onclick=()=>{ input.value=t.textContent; doSearch(); }; hg.appendChild(t); }); hw.appendChild(hg); html.appendChild(hw); } catch(e){}
    view.innerHTML=''; view.appendChild(html);
  }
  // ================= 全窗口歌词页（点封面进入） =================
  var npOpen = false, npLines = [], npIndex = -1, npSongId = 0;
  var npDurMs = 0, npPosMs = 0, npPlaying = false;   // 由 position / playing 消息维护
  var npTr = [], npRo = [];                           // 翻译 / 罗马音（与 npLines 同时间轴）
  // 把翻译与罗马音按时间戳挂到原词行上（允许 ±500ms 误差）
  function npMergeSubLines() {
    function pick(arr, t) {
      for (var i = 0; i < arr.length; i++) if (arr[i].t === t) return arr[i].s;
      var best = null, bestD = 501;
      for (var j = 0; j < arr.length; j++) { var d = Math.abs(arr[j].t - t); if (d < bestD) { bestD = d; best = arr[j].s; } }
      return best || '';
    }
    npLines.forEach(function (l) {
      l.tr = npTr.length ? pick(npTr, l.t) : '';
      l.ro = npRo.length ? pick(npRo, l.t) : '';
      if (l.tr === l.s) l.tr = '';
    });
  }
  function npEl(id) { return document.getElementById(id); }
  function npSetPlaying(on) {
    npPlaying = !!on;
    var b = npEl('np-play'); if (b) b.innerHTML = npPlaying ? SVG.pause : SVG.play;
  }

  function parseLrc(lrc) {
    var out = [];
    (lrc || '').split('\n').forEach(function (line) {
      var m = line.match(/^\s*\[(\d{1,2}):(\d{1,2})(?:[.:](\d{1,3}))?\]\s*(.*)$/);
      if (!m) return;
      var ms = parseInt(m[1], 10) * 60000 + parseInt(m[2], 10) * 1000;
      if (m[3]) ms += parseInt((m[3] + '00').slice(0, 3), 10);
      var txt = (m[4] || '').trim();
      if (txt) out.push({ t: ms, s: txt });
    });
    out.sort(function (a, b) { return a.t - b.t; });
    return out;
  }

  function npRenderSong(ns) {
    if (!ns) return;
    var cover = npEl('np-cover'), bg = npEl('np-bg');
    var wrap = document.querySelector('#np .np-cover-wrap');
    var pic = ns.Pic ? ns.Pic.replace(/\^\d+\^/, '') : '';
    if (pic) { cover.src = pic; bg.style.backgroundImage = 'url("' + pic + '")'; }
    else { cover.removeAttribute('src'); bg.style.backgroundImage = 'none'; }
    // 有封面时隐藏占位音符（原来用 img ~ i 兄弟选择器，但 <i> 在 <img> 前面，根本没生效）
    if (wrap) wrap.classList.toggle('has-cover', !!pic);
    npEl('np-title').textContent = ns.Title || '';
    npEl('np-artist').textContent = ns.Artist || '';
    npEl('np-album').textContent = ns.Album || '';
  }

  // ---- 歌词页外观设置（辉光/阴影/描边/排列/逐字/模糊/曲线）----
  var lyStyles = {
    glow: false, shadow: true, stroke: false,
    layout: 'vertical', curve: 50, charAnim: false,
    blur: false, blurAmt: 40, ease: 'smooth',
    showTr: true, showRo: true,
    fontSize: 22
  };
  // 曲线取值参考 refined-now-playing-netease
  var LY_EASE = {
    smooth: 'cubic-bezier(.18,.77,.58,.99)',   // 平滑
    sharp:  'cubic-bezier(.45,0,.07,1)',       // 急促
    gentle: 'cubic-bezier(.25,.46,.45,.94)',   // 温和
    easeout:'cubic-bezier(.15,.6,.35,1)'       // 缓出
  };

  function npApplyLyricSettings(s) {
    lyStyles.glow = cfgBool(s, 'lyric_glow', false);
    lyStyles.shadow = cfgBool(s, 'lyric_shadow', true);
    lyStyles.stroke = cfgBool(s, 'lyric_stroke', false);
    lyStyles.layout = String(cfgGet(s, 'lyric_layout', 'vertical'));
    lyStyles.curve = Number(cfgGet(s, 'lyric_curve', 50)) || 0;
    lyStyles.charAnim = cfgBool(s, 'lyric_char_anim', false);
    lyStyles.blur = cfgBool(s, 'lyric_blur', false);
    lyStyles.blurAmt = Number(cfgGet(s, 'lyric_blur_amount', 40)) || 0;
    lyStyles.ease = String(cfgGet(s, 'lyric_ease', 'smooth'));
    lyStyles.showTr = cfgBool(s, 'lyric_show_translation', true);
    lyStyles.showRo = cfgBool(s, 'lyric_show_romaji', true);
    lyStyles.fontSize = Math.max(12, Math.min(64, Number(cfgGet(s, 'lyric_font_size', 22)) || 22));
    npApplyStyleFromState();
    if (npLines.length) npRenderLyric(npLines);   // 逐字动画开关变了要重建 span
  }

  function npApplyStyleFromState() {
    var np = npEl('np'); if (!np) return;
    var s = lyStyles;
    np.classList.toggle('ly-glow', !!s.glow && !s.shadow);
    np.classList.toggle('ly-shadow', !!s.shadow && !s.glow);
    np.classList.toggle('ly-stroke', !!s.stroke);
    np.classList.toggle('ly-blur', !!s.blur);
    np.classList.toggle('ly-curved', s.layout === 'curved');
    np.classList.toggle('ly-char', !!s.charAnim);
    np.classList.toggle('ly-tr', !!s.showTr);
    np.classList.toggle('ly-ro', !!s.showRo);
    np.style.setProperty('--ly-blur', (s.blurAmt / 100 * 5).toFixed(2) + 'px');
    np.style.setProperty('--ly-ease', LY_EASE[s.ease] || LY_EASE.smooth);
    np.style.setProperty('--ly-font-size', s.fontSize + 'px');   // 字号（行高与排版会跟着重算）
    npLayout();
  }

  // ===== 歌词排版引擎（照 refined-now-playing-netease 的思路重写）=====
  // 每行绝对定位；按与当前行的“行距 offset”分别算 缩放/模糊/透明度 与位置，
  // 旋转时把各行放到以左侧远处为圆心的大圆弧上（而不是简单地按行号叠加角度）。
  function lyScale(off) {
    var t = Math.max(1 - Math.abs(off) * 0.2, 0);
    return t * t * t * 0.3 + 0.7;          // 0→1.0  ±1→0.854  ±2→0.765  ±5 以外→0.7
  }
  function lyBlurPx(off) {
    if (!lyStyles.blur) return 0;
    var a = Math.abs(off); if (a === 0) return 0;
    var maxB = Math.max(0.5, lyStyles.blurAmt / 100 * 7);
    // 平滑递增（原来是 0.5+|off| 起步，第一行就 1.5px，和当前行之间有明显分界）
    return Math.min(Math.pow(a, 0.8) * 0.8, maxB);
  }
  function lyOpacity(off) {
    var a = Math.abs(off);
    if (a <= 1) return 1;                  // 相邻行保持清晰
    return Math.max(1 - 0.4 * (a - 1), 0);
  }
  // 圆弧排列：圆心在左侧，半径随曲率变化
  function lyArc(yOffset, height, curvature) {
    var origin = [-120 + (curvature - 25), -(yOffset + height / 2)];
    var len = Math.sqrt(origin[0] * origin[0] + origin[1] * origin[1]) || 1;
    var rot = Math.min(yOffset / Math.max(1, window.innerHeight) * -curvature, 90);
    var deg = rot + Math.atan2(origin[1], origin[0]) * 180 / Math.PI;
    var maxShift = 90;                     // 限制横向漂移，别把远行推到屏幕外
    var left = Math.cos(deg * Math.PI / 180) * len - origin[0];
    if (left < -maxShift) left = -maxShift;
    return {
      rotate: rot,
      extraTop: Math.sin(deg * Math.PI / 180) * len - origin[1],
      left: left
    };
  }

  var lyPrevIndex = 0;
  // 关键点：行与行之间的“滚动”交给容器的 translateY（可过渡），
  // 每行自己的 top 用【未缩放】的自然行高算一次即可（静态），
  // 缩放/模糊/透明/旋转/弧线位移全部放在 transform 里 —— 这样换行是连续滑动而不是瞬移。
  function npLayout() {
    var wrap = npEl('np-lyric'), box = npEl('np-lyric-inner');
    if (!wrap || !box) return;
    var nodes = box.children; if (!nodes.length) return;
    var cur = (npIndex < 0) ? 0 : Math.min(npIndex, nodes.length - 1);
    var curved = lyStyles.layout === 'curved';
    var curvature = Math.max(1, lyStyles.curve) * 0.9;
    var fs = parseFloat(getComputedStyle(nodes[cur]).fontSize) || 18;
    var space = fs * 1.25;
    var h = [], s = [], b = [], o = [], base = [];
    for (var i = 0; i < nodes.length; i++) h[i] = nodes[i].offsetHeight || fs * 1.5;
    // 静态基准位置（从未缩放行高累加）
    base[0] = 0;
    for (var i = 1; i < nodes.length; i++) base[i] = base[i - 1] + h[i - 1] + space;
    for (var i = 0; i < nodes.length; i++) {
      var off = i - cur;
      s[i] = off === 0 ? 1 : lyScale(off);
      b[i] = off === 0 ? 0 : lyBlurPx(off);
      o[i] = off === 0 ? 1 : lyOpacity(off);
    }
    var centerY = wrap.clientHeight * 0.5;
    var off3, extraTop, left, deg, rel;
    for (var i = 0; i < nodes.length; i++) {
      var n = nodes[i]; var off2 = i - cur;
      n.style.top = base[i].toFixed(1) + 'px';
      extraTop = 0; left = 0; deg = 0;
      if (curved) {
        var yOff = base[cur] - base[i];
        var arc = lyArc(yOff, h[i] * s[i], curvature);
        deg = arc.rotate; extraTop = arc.extraTop; left = arc.left;
        rel = Math.abs(yOff * 2 / Math.max(1, window.innerHeight));
        o[i] = Math.min(o[i], Math.max(1 - Math.pow(rel, 1.15) * 1.2, 0));
      }
      n.style.transform = 'translateY(' + extraTop.toFixed(1) + 'px) translateX(' + left.toFixed(1) + 'px) scale(' + s[i].toFixed(3) + ')' + (curved ? (' rotate(' + deg.toFixed(2) + 'deg)') : '');
      n.style.filter = b[i] > 0.05 ? 'blur(' + b[i].toFixed(2) + 'px)' : '';
      n.style.opacity = o[i].toFixed(3);
      n.style.zIndex = String(900 - Math.abs(off2));
      // 错落：离当前行越远，动得越晚一点（换行时形成波浪感）
      var dly = off2 === 0 ? 0 : Math.min(Math.abs(off2), 4) * 22 + 16;
      n.style.transitionDelay = dly + 'ms';
      n.classList.toggle('on', i === cur && npIndex >= 0);
    }
    // 容器整体滑动，使当前行居中（这一层是有过渡的，换行不再瞬移）
    box.style.transform = 'translateY(' + (centerY - base[cur] - h[cur] / 2).toFixed(1) + 'px)';
    lyPrevIndex = cur;
  }

  function npRenderLyric(lines) {
    var box = npEl('np-lyric-inner'); if (!box) return;
    box.innerHTML = '';
    if (!lines.length) { box.innerHTML = '<div class="np-line on" style="top:45%">暂无歌词</div>'; return; }
    var chars = !!lyStyles.charAnim;
    lines.forEach(function (l) {
      var div = el('div', 'np-line');
      var main = el('div', 'np-orig');
      if (chars) {
        var txt = l.s, frag = '';
        for (var i = 0; i < txt.length; i++) frag += '<span class="ly-ch" style="--i:' + i + '">' + esc(txt.charAt(i)) + '</span>';
        main.innerHTML = frag;
      } else {
        main.textContent = l.s;
      }
      div.appendChild(main);
      if (l.ro) div.appendChild(el('div', 'np-sub np-ro', esc(l.ro)));
      if (l.tr) div.appendChild(el('div', 'np-sub np-tr', esc(l.tr)));
      box.appendChild(div);
    });
    npIndex = -1;
    npLayout();
  }

  function npSync(pos) {
    if (!npOpen || !npLines.length) return;
    var i = -1;
    for (var k = 0; k < npLines.length; k++) { if (npLines[k].t <= pos) i = k; else break; }
    if (i === npIndex) return;
    npIndex = i;
    npLayout();
  }
  async function openNowPlaying() {
    var np = npEl('np'); if (!np) return;
    var wasOpen = npOpen;
    npOpen = true;
    np.classList.add('show'); np.setAttribute('aria-hidden', 'false');
    if (!wasOpen) requestAnimationFrame(npCoverFlip);
    var ns = nowPlaying || queue[playingIndex];
    if (!ns) { npEl('np-title').textContent = '未在播放'; npEl('np-artist').textContent = '—'; npEl('np-album').textContent = ''; npRenderLyric([]); return; }
    npRenderSong(ns);
    if (npSongId !== ns.Id) {
      npSongId = ns.Id; npLines = []; npIndex = -1;
      npRenderLyric([]); npEl('np-lyric-inner').innerHTML = '<div class="np-line">歌词加载中…</div>';
      try {
        var r = await NE.lyric(ns.Id);
        // 原词 / 翻译(tlyric) / 罗马音(romalrc) 三轨合并
        npTr = parseLrc((r && r.tlyric && r.tlyric.lyric) || '');
        npRo = parseLrc((r && r.romalrc && r.romalrc.lyric) || '');
        if (!npOpen || npSongId !== ns.Id) return;
        npLines = parseLrc((r && r.lrc && r.lrc.lyric) || '');
        npMergeSubLines();
        npRenderLyric(npLines);
        npIndex = -1;
      } catch (e) { npRenderLyric([]); }
    }
  }
  // 封面“飞入”：从 dock 小封面 FLIP 到大封面，让歌词页像是从播放条长出来的
  function npCoverFlip() {
    var wrap = document.querySelector('#np .np-cover-wrap');
    var from = document.getElementById('pl-cover-wrap');
    if (!wrap || !from) return;
    var a = from.getBoundingClientRect(), b = wrap.getBoundingClientRect();
    if (!a.width || !b.width) return;
    var sx = a.width / b.width, sy = a.height / b.height;
    var s = Math.max(.12, Math.min(sx, sy));
    var dx = (a.left + a.width / 2) - (b.left + b.width / 2);
    var dy = (a.top + a.height / 2) - (b.top + b.height / 2);
    wrap.style.transition = 'none';
    wrap.style.transform = 'translate(' + dx + 'px,' + dy + 'px) scale(' + s + ')';
    wrap.style.opacity = '.85';
    void wrap.offsetWidth;                       // 强制回流，保证起始帧生效
    requestAnimationFrame(function () {
      wrap.style.transition = 'transform .52s cubic-bezier(.2,.8,.25,1), opacity .3s ease';
      wrap.style.transform = 'none';
      wrap.style.opacity = '1';
    });
  }

  window.addEventListener('resize', function () { if (npOpen) npLayout(); });

  // 全局快捷键
  var hkRecording = null;                 // { key, btn, cancelBtn } 录制中
  document.addEventListener('keydown', function (e) {
    if (hkRecording) {
      e.preventDefault(); e.stopPropagation();
      if (e.key === 'Escape') { stopRecord(false); return; }
      var name = evKeyName(e);
      if (!name || name === 'Ctrl' || name === 'Alt' || name === 'Shift') return;   // 只按了修饰键，等真正的键
      stopRecord(true, name);
      return;
    }
    if (isTyping(e.target)) return;
    var name = evKeyName(e);
    if (!name) return;
    for (var action in hotkeys) {
      if (hotkeys[action] && hotkeys[action] === name) { e.preventDefault(); runHotkey(action); return; }
    }
  }, true);
  function stopRecord(commit, name) {
    if (!hkRecording) return;
    var rec = hkRecording; hkRecording = null;
    rec.btn.classList.remove('rec');
    if (commit && name) {
      hotkeys[rec.key] = name;
      NE.setSetting(rec.key, name);
      rec.btn.textContent = name;
    } else {
      rec.btn.textContent = hotkeys[rec.key] || HK_DEFAULTS[rec.key];
    }
  }

  // ---- 歌词页内的“歌词显示设置”面板（可视化实时调整）----
  function npBuildSettings() {
    var box = npEl('np-settings-body'); if (!box) return;
    box.innerHTML = '';
    var s = appSettings || {};
    function persist(k, v) { NE.setSetting(k, v); npApplyStyleFromState(); if (npLines.length) npRenderLyric(npLines); }
    function row(label) {
      var r = el('div', 'nps-row'); r.appendChild(el('label', 'nps-label', label)); box.appendChild(r); return r;
    }
    function addSwitch(label, key, field, def) {
      var r = row(label);
      var t = el('div', 'nps-switch' + (lyStyles[field] ? ' on' : ''));
      r.appendChild(t);
      t.onclick = function () {
        var on = !t.classList.contains('on'); t.classList.toggle('on', on);
        lyStyles[field] = on;
        if (field === 'glow' && on) { lyStyles.shadow = false; NE.setSetting('lyric_shadow', 'false'); }
        if (field === 'shadow' && on) { lyStyles.glow = false; NE.setSetting('lyric_glow', 'false'); }
        persist(key, on ? 'true' : 'false');
        npBuildSettings();                       // 互斥项要刷新另一个开关的状态
      };
    }
    function addRange(label, key, field, lo, hi) {
      var r = row(label);
      var val = el('span', 'nps-val', String(lyStyles[field]));
      var i = el('input'); i.type = 'range'; i.min = lo; i.max = hi; i.value = lyStyles[field];
      i.oninput = function () { val.textContent = i.value; lyStyles[field] = Number(i.value); npApplyStyleFromState(); };
      i.onchange = function () { persist(key, i.value); };
      r.appendChild(i); r.appendChild(val);
    }
    function addSelect(label, key, field, opts) {
      var r = row(label);
      var box = el('div', 'cust-select nps-cust');
      var cur = opts[0];
      opts.forEach(function (o) { if (o.v === lyStyles[field]) cur = o; });
      var v = el('div', 'cs-value');
      v.innerHTML = '<span>' + esc(cur.t) + '</span><i class="ic">&#xE70D;</i>';
      var list = el('div', 'cs-list');
      opts.forEach(function (o) {
        var it = el('div', 'cs-item' + (o.v === lyStyles[field] ? ' sel' : ''), esc(o.t));
        it.onclick = function (e) {
          e.stopPropagation();
          box.classList.remove('open');
          lyStyles[field] = o.v;
          v.querySelector('span').textContent = o.t;
          Array.prototype.forEach.call(list.children, function (x) { x.classList.remove('sel'); });
          it.classList.add('sel');
          persist(key, o.v);
        };
        list.appendChild(it);
      });
      v.onclick = function (e) {
        e.stopPropagation();
        var wasOpen = box.classList.contains('open');
        document.querySelectorAll('.cust-select.open').forEach(function (x) { x.classList.remove('open'); });
        if (wasOpen) return;
        // 下方空间不足就向上弹，避免被面板边缘裁掉
        var bodyEl = npEl('np-settings-body');
        var bb = box.getBoundingClientRect();
        var limit = bodyEl ? bodyEl.getBoundingClientRect().bottom : window.innerHeight;
        var need = Math.min(list.children.length, 6) * 34 + 16;
        box.classList.toggle('drop-up', (limit - bb.bottom) < need);
        box.classList.add('open');
      };
      document.addEventListener('click', function () { box.classList.remove('open'); });
      box.appendChild(v); box.appendChild(list); r.appendChild(box);
    }
    addSwitch('字体辉光', 'lyric_glow', 'glow');
    addSwitch('字体阴影', 'lyric_shadow', 'shadow');
    addSwitch('字体描边', 'lyric_stroke', 'stroke');
    addSelect('歌词排列', 'lyric_layout', 'layout', [{ v: 'vertical', t: '竖向' }, { v: 'curved', t: '旋转弧形' }]);
    addRange('排列曲率', 'lyric_curve', 'curve', 0, 100);
    addRange('字体大小', 'lyric_font_size', 'fontSize', 14, 56);
    addSwitch('显示翻译', 'lyric_show_translation', 'showTr');
    addSwitch('显示罗马音', 'lyric_show_romaji', 'showRo');
    addSwitch('逐字动画', 'lyric_char_anim', 'charAnim');
    addSwitch('非当前行模糊', 'lyric_blur', 'blur');
    addRange('模糊程度', 'lyric_blur_amount', 'blurAmt', 0, 100);
    addSelect('动画曲线', 'lyric_ease', 'ease', [
      { v: 'smooth', t: '平滑' }, { v: 'sharp', t: '急促' }, { v: 'gentle', t: '温和' }, { v: 'easeout', t: '缓出' }
    ]);
  }
  function npToggleSettings(force) {
    var p = npEl('np-settings'); if (!p) return;
    var on = (force === undefined) ? !p.classList.contains('show') : !!force;
    if (on) npBuildSettings();
    p.classList.toggle('show', on);
    p.setAttribute('aria-hidden', on ? 'false' : 'true');
    var g = npEl('np-gear'); if (g) g.classList.toggle('on', on);
  }

  function closeNowPlaying() {
    var np = npEl('np'); if (!np) return;
    npToggleSettings(false);
    var wrap = document.querySelector('#np .np-cover-wrap');
    var from = document.getElementById('pl-cover-wrap');
    if (wrap && from) {                          // 收起时反向飞回 dock
      var a = from.getBoundingClientRect(), b = wrap.getBoundingClientRect();
      if (a.width && b.width) {
        var s = Math.max(.12, Math.min(a.width / b.width, a.height / b.height));
        var dx = (a.left + a.width / 2) - (b.left + b.width / 2);
        var dy = (a.top + a.height / 2) - (b.top + b.height / 2);
        wrap.style.transition = 'transform .42s cubic-bezier(.4,0,.6,1), opacity .3s ease';
        wrap.style.transformOrigin = 'center';
        wrap.style.transform = 'translate(' + dx + 'px,' + dy + 'px) scale(' + s + ')';
        wrap.style.opacity = '.6';
      }
    }
    npOpen = false;
    np.classList.remove('show');
    np.setAttribute('aria-hidden', 'true');
    setTimeout(function () {
      if (npOpen) return;
      var w = document.querySelector('#np .np-cover-wrap');
      if (w) { w.style.transition = 'none'; w.style.transform = 'none'; w.style.opacity = ''; }
    }, 460);
  }

  // 旧的 go('lyric') 也走全窗口歌词页
  async function goLyric() { return openNowPlaying(); }
  // 我的歌单：登录后展示账号下的歌单（创建 + 收藏）
  async function goLiked() {
    loading();
    const html = el('div','page'); html.appendChild(el('h2','page-title','我的歌单'));
    try {
      const st = await NE.loginStatus();
      const uid = (st && st.data && st.data.profile && st.data.profile.userId)
        || (st && st.profile && st.profile.userId) || 0;
      if (!uid) {
        html.appendChild(el('p','muted','登录后可以查看你创建和收藏的歌单'));
        const b = el('button','action-btn','去登录'); b.onclick = () => { document.querySelectorAll('#sidebar a').forEach(x=>x.classList.remove('on')); go('account'); };
        html.appendChild(b);
      } else {
        const r = await NE.userPlaylist(uid, 200);
        const list = (r && r.playlist) || [];
        const sub = (r && r.playlist && r.playlist.length) ? '' : '';
        html.appendChild(el('p','muted','共 ' + list.length + ' 个歌单'));
        const grid = el('div','pl-grid');
        list.forEach(p => grid.appendChild(playlistCard(p)));
        html.appendChild(grid);
      }
    } catch (e) { html.appendChild(el('div','big-load','加载失败: ' + e.message)); }
    view.innerHTML=''; view.appendChild(html);
  }
  async function goAccount() {
    loading();
    const html = el('div','page'); html.appendChild(el('h2','page-title','账号'));
    try {
      const st = await NE.loginStatus();
      const d = (st && st.data) || st || {};
      const p = d.profile || {};
      if (p.userId) {
        html.appendChild(el('div','account-info',
          '<img class="acc-avatar" src="' + (p.avatarUrl || '') + '?param=120y120" alt="">'
          + '<div><div class="acc-nick">' + esc(p.nickname || '') + '</div>'
          + '<div class="acc-sub">已登录 · uid=' + p.userId + '</div></div>'));
        const out = el('button','action-btn','退出登录');
        out.onclick = async () => {
          try { await NE.logout(); } catch (e) { }
          NE.post({ type:'save_cookie', cookie:'' });
          toast('已退出登录'); go('account');
        };
        html.appendChild(out);
      } else {
        html.appendChild(el('p','muted','未登录 —— 用手机上的网易云音乐 App 扫描二维码登录'));
        const card = el('div','qr-card');
        const img = el('img','qr-img');
        const mask = el('div','qr-mask');
        const stat = el('div','qr-status','');
        const btn = el('button','action-btn','获取二维码');
        card.appendChild(img); card.appendChild(mask);
        html.appendChild(card); html.appendChild(stat); html.appendChild(btn);

        let polling = false, unikey = '';
        function stop() { polling = false; }
        async function poll() {
          if (!polling) return;
          try {
            const ch = await NE.loginQrCheck(unikey);
            const code = ch && ch.code;
            if (code === 800) { stop(); stat.textContent = '二维码已过期，请点「刷新二维码」'; mask.className = 'qr-mask show'; mask.textContent = '已过期'; return; }
            if (code === 801) stat.textContent = '等待扫码…';
            else if (code === 802) stat.textContent = '已扫码，请在手机上确认登录';
            else if (code === 803) {
              stop();
              if (ch.cookie) NE.post({ type: 'save_cookie', cookie: ch.cookie });
              stat.textContent = '登录成功，正在加载…'; toast('登录成功');
              setTimeout(function () { go('account'); }, 500);
              return;
            }
          } catch (e) { }
          setTimeout(poll, 2500);
        }
        async function start() {
          stop();
          img.removeAttribute('src');
          mask.className = 'qr-mask show'; mask.textContent = '正在获取…';
          stat.textContent = '正在获取二维码…';
          try {
            const k = await NE.loginQrKey();
            unikey = (k && k.data && k.data.unikey) || (k && k.unikey) || '';
            if (!unikey) throw new Error('未取到二维码 key');
            const c = await NE.loginQrCreate(unikey);
            const qrimg = (c && c.data && c.data.qrimg) || (c && c.qrimg) || '';
            if (!qrimg) throw new Error('未取到二维码图片');
            img.src = qrimg;
            mask.className = 'qr-mask'; mask.textContent = '';
            stat.textContent = '请用网易云音乐 App 扫码';
            btn.textContent = '刷新二维码';
            polling = true; poll();
          } catch (e) {
            mask.className = 'qr-mask show'; mask.textContent = '获取失败';
            stat.textContent = '获取二维码失败：' + e.message;
          }
        }
        btn.onclick = start;
        start();
      }
    } catch (e) { html.appendChild(el('div','big-load','获取登录状态失败')); }
    view.innerHTML = ''; view.appendChild(html);
  }

  // ---------- 设置（内嵌主窗口的网页设置页） ----------
  async function goSettings() {
    loading();
    var s = await NE.getSettings();
    var html = el('div','page');
    html.appendChild(el('h2','page-title','设置'));
    function group(title, rows) { var g = el('div','set-group'); g.appendChild(el('h3','',title)); rows.forEach(function(r){ g.appendChild(r); }); return g; }
    function sw(label, key, val) { var r = el('div','set-row'); r.appendChild(el('label','',label)); var t = el('div','set-switch'+(val?' on':'')); t.onclick = function(){ var on=!t.classList.contains('on'); t.classList.toggle('on',on); NE.setSetting(key, on?'true':'false'); }; r.appendChild(t); return r; }
    // 原生 <select> 的弹层在 WebView2 里定位会飘，这里统一用自定义下拉
    function sel(label, key, val, opts) { return custSel(label, key, val, opts); }
    function txt(label, key, val) { var r = el('div','set-row'); r.appendChild(el('label','',label)); var i=el('input'); i.type='text'; i.value=val||''; i.onchange=function(){ NE.setSetting(key, i.value); }; r.appendChild(i); return r; }
    function rng(label, key, val) { var r = el('div','set-row'); var lb=el('label','',label+' ('+(val||80)+')'); r.appendChild(lb); var i=el('input'); i.type='range'; i.min=0; i.max=100; i.value=val||80; i.oninput=function(){ lb.textContent=label+' ('+i.value+')'; NE.setSetting(key, i.value); }; r.appendChild(i); return r; }
    function info(label) { var r = el('div','set-row'); r.appendChild(el('label','',label)); return r; }
    // 自定义下拉：opts 支持字符串数组或 [{v:值,t:显示名}]；onPick(值) 用于即时生效
    function custSel(label, key, val, opts, onPick) {
      var list0 = opts || [];
      function valOf(o) { return (o && typeof o === 'object') ? o.v : o; }
      function txtOf(o) { return (o && typeof o === 'object') ? o.t : o; }
      var r = el('div','set-row'); r.appendChild(el('label','',label));
      var box = el('div','cust-select');
      var cur = null;
      list0.forEach(function(o){ if (valOf(o) === val) cur = o; });
      if (cur === null && list0.length) cur = list0[0];
      var v = el('div','cs-value'); v.innerHTML = '<span>'+esc(txtOf(cur))+'</span><i class="ic">&#xE70D;</i>';
      var list = el('div','cs-list');
      list0.forEach(function(o){
        var it = el('div','cs-item'+(valOf(o)===val?' sel':''), esc(txtOf(o)));
        it.onclick = function(e){
          e.stopPropagation();
          var rr = it.getBoundingClientRect();
          if (key === 'scheme') { rippleArm(rr.left + rr.width/2, rr.top + rr.height/2, [function(){ box.classList.remove('open'); }]); }
          else { box.classList.remove('open'); }
          NE.setSetting(key, valOf(o));
          v.querySelector('span').textContent = txtOf(o);
          list.querySelectorAll('.cs-item').forEach(function(x){ x.classList.remove('sel'); });
          it.classList.add('sel');
          if (key === 'scheme') { setTimeout(function(){ box.classList.remove('open'); }, 1200); }
          if (onPick) { try { onPick(valOf(o)); } catch (err) { } }
        };
        list.appendChild(it);
      });
      v.onclick=function(e){ e.stopPropagation(); document.querySelectorAll('.cust-select.open').forEach(function(x){ if(x!==box) x.classList.remove('open'); }); box.classList.toggle('open'); };
      box.appendChild(v); box.appendChild(list); r.appendChild(box);
      document.addEventListener('click', function(){ box.classList.remove('open'); });
      return r;
    }
    // ---- 鼠标特效（细分设置；改动即时生效，不用重开界面）----
    function fxApply(p) { try { if (window.FX && window.FX.setConfig) window.FX.setConfig(p); } catch (e) { } }
    function fxSw(label, key, val, fk) {
      var r = el('div','set-row'); r.appendChild(el('label','',label));
      var t = el('div','set-switch'+(val?' on':'')); t.title = label;
      t.onclick = function(){ var on=!t.classList.contains('on'); t.classList.toggle('on',on); NE.setSetting(key, on?'true':'false'); var p={}; p[fk]=on; fxApply(p); };
      r.appendChild(t); return r;
    }
    function fxSel(label, key, val, opts, fk) {
      return custSel(label, key, val, opts, function(v){ var p={}; p[fk]=v; fxApply(p); });
    }
    function fxRng(label, key, val, fk) {
      var v = (val===undefined||val===null||val==='') ? 50 : Number(val);
      var r = el('div','set-row'); var lb = el('label','',label+' ('+v+')'); r.appendChild(lb);
      var i = el('input'); i.type='range'; i.min=0; i.max=100; i.value=v;
      i.oninput = function(){ lb.textContent = label+' ('+i.value+')'; NE.setSetting(key, i.value); var p={}; p[fk]=Number(i.value); fxApply(p); };
      r.appendChild(i); return r;
    }
    function fxColorRow(colorVal, autoVal) {
      var PALETTE = ['#e23535', '#07c160', '#1d6eff', '#5865f2', '#9b59b6', '#f0a020', '#00bcd4', '#ffffff'];
      var r = el('div','set-row'); r.appendChild(el('label','','特效颜色'));
      var box = el('div','fx-color');
      var auto = el('div','set-switch'+(autoVal?' on':'')); auto.title = '跟随当前配色方案的强调色';
      var tip = el('span','fx-color-tip','跟随主题');
      var cur = (colorVal && colorVal !== 'auto' && /^#[0-9a-fA-F]{6}$/.test(colorVal)) ? colorVal : '#e23535';
      var wrap = el('div','fx-picker' + (autoVal ? ' off' : ''));
      var sw = el('div','fx-swatch-list');
      PALETTE.forEach(function(c){
        var s = el('span','fx-swatch' + (!autoVal && c.toLowerCase() === cur.toLowerCase() ? ' on' : ''));
        s.style.background = c; s.title = c;
        s.onclick = function(){
          if (auto.classList.contains('on')) return;
          cur = c;
          sw.querySelectorAll('.fx-swatch').forEach(function(x){ x.classList.remove('on'); });
          s.classList.add('on'); hex.value = c;
          NE.setSetting('fx_color', c); fxApply({ color: c });
        };
        sw.appendChild(s);
      });
      var hex = el('input'); hex.type = 'text'; hex.className = 'fx-hex';
      hex.value = autoVal ? '' : cur; hex.placeholder = autoVal ? '跟随主题' : '#RRGGBB'; hex.disabled = !!autoVal;
      hex.onchange = function(){
        var v = (hex.value || '').trim(); if (v.charAt(0) !== '#') v = '#' + v;
        if (!/^#[0-9a-fA-F]{6}$/.test(v)) { hex.value = cur; return; }
        cur = v.toLowerCase(); hex.value = cur;
        NE.setSetting('fx_color', cur); fxApply({ color: cur });
        sw.querySelectorAll('.fx-swatch').forEach(function(x){ x.classList.remove('on'); });
      };
      wrap.appendChild(sw); wrap.appendChild(hex);
      auto.onclick = function(){
        var on = !auto.classList.contains('on'); auto.classList.toggle('on', on);
        wrap.classList.toggle('off', on); hex.disabled = on;
        hex.value = on ? '' : cur;
        NE.setSetting('fx_color', on ? 'auto' : cur); fxApply({ color: on ? 'auto' : cur });
      };
      box.appendChild(tip); box.appendChild(auto); box.appendChild(wrap); r.appendChild(box); return r;
    }
    // 自定义设置键（snake_case）统一从 C# 回传的 [App] 段里取，新增项无需改 C#
    function fxGroup(s) {
      var g = el('div','set-group');
      g.appendChild(el('h3','','鼠标特效'));
      var on = cfgBool(s, 'ui_effects', true);
      var master = el('div','set-row'); master.appendChild(el('label','','启用鼠标特效'));
      var mt = el('div','set-switch'+(on?' on':'')); master.appendChild(mt); g.appendChild(master);
      var d = el('div','fx-detail'+(on?'':' hidden'));
      // 拖尾
      d.appendChild(fxSw('光标拖尾','fx_trail', cfgBool(s,'fx_trail',true), 'trail'));
      d.appendChild(fxRng('拖尾长度','fx_trail_len', cfgGet(s,'fx_trail_len',55), 'trailLen'));
      d.appendChild(fxRng('拖尾粗细','fx_trail_width', cfgGet(s,'fx_trail_width',50), 'trailWidth'));
      d.appendChild(fxSw('光标光晕','fx_glow', cfgBool(s,'fx_glow',true), 'glow'));
      // 点击
      d.appendChild(fxSw('点击特效','fx_click', cfgBool(s,'fx_click',true), 'click'));
      d.appendChild(fxSel('点击样式','fx_click_style', cfgGet(s,'fx_click_style','both'), [ {v:'both',t:'波纹 + 火花'}, {v:'ring',t:'仅波纹'}, {v:'spark',t:'仅火花'} ], 'clickStyle'));
      d.appendChild(fxRng('特效大小','fx_click_size', cfgGet(s,'fx_click_size',50), 'clickSize'));
      // 抖动
      d.appendChild(fxSw('按钮抖动','fx_shake', cfgBool(s,'fx_shake',true), 'shake'));
      d.appendChild(fxRng('抖动强度','fx_shake_power', cfgGet(s,'fx_shake_power',50), 'shakePower'));
      // 颜色
      var col = cfgGet(s, 'fx_color', 'auto');
      d.appendChild(fxColorRow(col, col === 'auto'));
      g.appendChild(d);
      mt.onclick = function(){ var v=!mt.classList.contains('on'); mt.classList.toggle('on',v); d.classList.toggle('hidden',!v); NE.setSetting('ui_effects', v?'true':'false'); fxApply({ enabled: v }); };
      return g;
    }

    // ---- 歌词页外观（改动即时生效并落 config）----
    function lyricGroup(s) {
      var g = el('div','set-group');
      g.appendChild(el('h3','','歌词页（全窗口歌词）'));
      function persist(k, v) { NE.setSetting(k, v); npApplyStyleFromState(); if (npLines.length) npRenderLyric(npLines); }
      // 开关
      function lSw(label, key, field, def) {
        var r = el('div','set-row'); r.appendChild(el('label','',label));
        var v = cfgBool(s, key, def);
        var t = el('div','set-switch'+(v?' on':'')); t.title = label; r.appendChild(t);
        t.onclick = function(){
          var on = !t.classList.contains('on'); t.classList.toggle('on', on);
          lyStyles[field] = on;
          if (field === 'glow' && on) { lyStyles.shadow = false; shadowSw.classList.remove('on'); NE.setSetting('lyric_shadow','false'); }
          if (field === 'shadow' && on) { lyStyles.glow = false; glowSw.classList.remove('on'); NE.setSetting('lyric_glow','false'); }
          persist(key, on ? 'true' : 'false');
        };
        r.__sw = t;
        return r;
      }
      var rowGlow = lSw('字体辉光','lyric_glow','glow', false);
      var rowShadow = lSw('字体阴影','lyric_shadow','shadow', true);
      var glowSw = rowGlow.__sw, shadowSw = rowShadow.__sw;
      g.appendChild(rowGlow); g.appendChild(rowShadow);
      g.appendChild(lSw('字体描边','lyric_stroke','stroke', false));
      // 排列
      var rowLayout = el('div','set-row'); rowLayout.appendChild(el('label','','歌词排列'));
      var selLayout = el('div','cust-select'); selLayout.innerHTML = '<div class="cust-value"></div><div class="cust-list"></div>';
      var curLayout = String(cfgGet(s, 'lyric_layout', 'vertical'));
      var LAY = [{v:'vertical',t:'竖向（默认）'},{v:'curved',t:'旋转弧形'}];
      selLayout.querySelector('.cust-value').textContent = (LAY.filter(function(x){return x.v===curLayout;})[0]||LAY[0]).t;
      var llist = selLayout.querySelector('.cust-list');
      LAY.forEach(function(o){
        var it = el('div','cust-item'+(o.v===curLayout?' on':''), o.t);
        it.onclick = function(e){ e.stopPropagation(); selLayout.querySelector('.cust-value').textContent = o.t;
          Array.prototype.forEach.call(llist.children, function(x){ x.classList.remove('on'); }); it.classList.add('on');
          selLayout.classList.remove('open'); lyStyles.layout = o.v; persist('lyric_layout', o.v); };
        llist.appendChild(it);
      });
      selLayout.querySelector('.cust-value').onclick = function(e){ e.stopPropagation(); selLayout.classList.toggle('open'); };
      document.addEventListener('click', function(){ selLayout.classList.remove('open'); });
      rowLayout.appendChild(selLayout); g.appendChild(rowLayout);
      // 曲率
      function lRng(label, key, field, lo, hi, def) {
        var r = el('div','set-row'); var v = Number(cfgGet(s, key, def)) || def;
        var lb = el('label','', label + ' (' + v + ')'); r.appendChild(lb);
        var i = el('input'); i.type = 'range'; i.min = lo; i.max = hi; i.value = v;
        i.oninput = function(){ lb.textContent = label + ' (' + i.value + ')'; lyStyles[field] = Number(i.value); npApplyStyleFromState(); };
        i.onchange = function(){ persist(key, i.value); };
        r.appendChild(i); return r;
      }
      g.appendChild(lRng('排列弯曲曲率','lyric_curve','curve', 0, 100, 50));
      g.appendChild(lSw('逐字动画','lyric_char_anim','charAnim', false));
      g.appendChild(lSw('非当前行模糊','lyric_blur','blur', false));
      g.appendChild(lRng('模糊程度','lyric_blur_amount','blurAmt', 0, 100, 40));
      // 动画曲线
      var rowEase = el('div','set-row'); rowEase.appendChild(el('label','','动画曲线'));
      var selEase = el('div','cust-select'); selEase.innerHTML = '<div class="cust-value"></div><div class="cust-list"></div>';
      var EASE = [{v:'smooth',t:'平滑'},{v:'sharp',t:'急促'},{v:'gentle',t:'温和'},{v:'easeout',t:'缓出'}];
      var curEase = String(cfgGet(s, 'lyric_ease', 'smooth'));
      selEase.querySelector('.cust-value').textContent = (EASE.filter(function(x){return x.v===curEase;})[0]||EASE[0]).t;
      var elist = selEase.querySelector('.cust-list');
      EASE.forEach(function(o){
        var it = el('div','cust-item'+(o.v===curEase?' on':''), o.t);
        it.onclick = function(e){ e.stopPropagation(); selEase.querySelector('.cust-value').textContent = o.t;
          Array.prototype.forEach.call(elist.children, function(x){ x.classList.remove('on'); }); it.classList.add('on');
          selEase.classList.remove('open'); lyStyles.ease = o.v; persist('lyric_ease', o.v); };
        elist.appendChild(it);
      });
      selEase.querySelector('.cust-value').onclick = function(e){ e.stopPropagation(); selEase.classList.toggle('open'); };
      document.addEventListener('click', function(){ selEase.classList.remove('open'); });
      rowEase.appendChild(selEase); g.appendChild(rowEase);
      return g;
    }

    html.appendChild(group('主题 / 外观', [ custSel('配色方案','scheme', s.scheme, s.schemes||[]), sw('Mica 背景','mica', s.mica), sw('关闭按钮最小化到托盘','closeToTray', s.closeToTray) ]));
    html.appendChild(fxGroup(s));
    html.appendChild(group('下载', [ txt('默认下载目录','downloadDir', s.downloadDir), sel('音质','quality', s.quality, ['standard','high','lossless']) ]));
    html.appendChild(group('播放', [ rng('默认音量','volume', s.volume), sel('播放模式','playMode', s.playMode, ['order','list','single','random']) ]));
    // ---- 快捷键（可自定义）----
    function hotkeyGroup() {
      var g = el('div','set-group');
      g.appendChild(el('h3','','快捷键'));
      Object.keys(HK_LABELS).forEach(function (key) {
        var r = el('div','set-row');
        r.appendChild(el('label','', HK_LABELS[key]));
        var b = el('button','hk-btn', hotkeys[key] || HK_DEFAULTS[key]);
        b.onclick = function (e) {
          e.stopPropagation();
          if (hkRecording) stopRecord(false);
          hkRecording = { key: key, btn: b };
          b.classList.add('rec'); b.textContent = '按下按键…（Esc 取消）';
        };
        var rst = el('button','hk-reset','重置');
        rst.onclick = function (e) {
          e.stopPropagation();
          hotkeys[key] = HK_DEFAULTS[key];
          NE.setSetting(key, HK_DEFAULTS[key]);
          b.textContent = HK_DEFAULTS[key];
        };
        r.appendChild(b); r.appendChild(rst);
        g.appendChild(r);
      });
      g.appendChild(el('p','muted','点按键框后按下想要的组合键即可（Esc 取消录制）；播放/暂停默认是空格。'));
      return g;
    }
    html.appendChild(hotkeyGroup());
    html.appendChild(group('网络 / 代理', [ txt('代理地址','proxy', s.proxy) ]));
    html.appendChild(group('语言', [ sel('界面语言','language', s.language, ['zh_cn','en_US']) ]));
    html.appendChild(group('桌面歌词', [ sw('启用桌面歌词','desktopLyric', s.desktopLyric), sw('强制置顶 + 鼠标穿透','desktopLyricTopmost', s.desktopLyricTopmost), sw('桌面歌曲信息','desktopSongInfo', s.desktopSongInfo) ]));
    html.appendChild(group('通知', [ sw('下载完成通知','toast', s.toast) ]));
    html.appendChild(group('关于', [ info('netHEmusic 版本 v' + (s.version||'')), info('WinUI3 + Fluent · 第三方软件，仅供学习交流，禁止商用及任何侵权用途') ]));
    view.innerHTML=''; view.appendChild(html);
  }

  // 主题：把 C# 传来的 Material You 变量以【行内样式】写到 <html>（优先级最高，覆盖 :root 默认值）
  function applyThemeVars(d) {
    if (d.dark !== undefined) document.documentElement.classList.toggle('dark', !!d.dark);
    if (d.vars) {
      var root = document.documentElement;
      d.vars.split(';').forEach(function (pair) {
        var i = pair.indexOf(':'); if (i <= 0) return;
        var name = pair.slice(0, i).trim(), val = pair.slice(i + 1).trim();
        if (name && val) root.style.setProperty(name, val);
      });
      window._themeVars = d.vars;
    }
  }

  // ---------- 配色切换水波：以列表项为圆心向外扩散，用新配色覆盖旧界面 ----------
  var rippleJob = null, rippleToken = 0;
  function rippleArm(x, y, after) {
    var tok = ++rippleToken;
    // 夹取到视口内：万一列表项被滚动到可视区之外，水波也从最近的屏幕边缘发起
    x = Math.min(Math.max(x, 0), window.innerWidth);
    y = Math.min(Math.max(y, 0), window.innerHeight);
    rippleJob = { x: x, y: y, after: after || [] };
    try { NE.post({ type: 'log', msg: 'ripple origin=' + Math.round(x) + ',' + Math.round(y) + ' viewport=' + window.innerWidth + 'x' + window.innerHeight }); } catch (e) { }
    setTimeout(function () { if (rippleJob && tok === rippleToken) rippleJob = null; }, 2000); // 兜底：未等到配色推送就作废
  }
  function rippleRadius(x, y) {
    var dx = Math.max(x, window.innerWidth - x), dy = Math.max(y, window.innerHeight - y);
    return Math.ceil(Math.sqrt(dx * dx + dy * dy)) + 32;
  }
  function rippleRing(x, y) {
    var r = rippleRadius(x, y);
    var d = document.createElement('div');
    d.className = 'nm-ripple-ring';
    d.style.width = d.style.height = (r * 2) + 'px';
    d.style.left = x + 'px'; d.style.top = y + 'px';
    document.body.appendChild(d);
    setTimeout(function () { if (d.parentNode) d.parentNode.removeChild(d); }, 900);
  }
  function themeWithRipple(d) {
    var job = rippleJob; rippleJob = null;
    var after = job ? job.after : [], done = false;
    function finish() { if (done) return; done = true; after.forEach(function (f) { try { f(); } catch (e) { } }); }
    var reduce = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    if (!job || reduce || typeof document.startViewTransition !== 'function') {
      applyThemeVars(d);
      if (job) { rippleRing(job.x, job.y); setTimeout(finish, 280); } else { finish(); }
      return;
    }
    var rs = document.documentElement.style;
    rs.setProperty('--nm-ripple-x', job.x + 'px');
    rs.setProperty('--nm-ripple-y', job.y + 'px');
    rs.setProperty('--nm-ripple-r', rippleRadius(job.x, job.y) + 'px');
    var vt;
    try { vt = document.startViewTransition(function () { applyThemeVars(d); }); }
    catch (e) { applyThemeVars(d); rippleRing(job.x, job.y); setTimeout(finish, 280); return; }
    vt.finished.then(finish, finish);
    setTimeout(finish, 1600);
  }
  NE.on('theme', themeWithRipple);
  NE.on('playing', function(d){
    if(d.song){
      nowPlaying = normSong(d.song);
      setPlayer(d.song);
      npSetPlaying(true);
      if (npOpen) openNowPlaying();          // 歌词页开着时跟着换歌换词
      renderQueue();                          // 刷新播放列表高亮
    }
  });
  NE.on('position', function(d){
    $('#pl-cur').textContent=fmt(d.pos||0); if(d.dur){ $('#pl-dur').textContent=fmt(d.dur); }
    $('#pl-fill').style.width=(d.dur?Math.min(100,(d.pos||0)/d.dur*100):0)+'%';
    // 全窗口歌词页
    npPosMs = d.pos || 0;
    if (d.dur) npDurMs = d.dur;
    npSync(npPosMs);
    var f = $('#np-fill'), c = $('#np-cur'), u = $('#np-dur');
    if (f) f.style.width = (d.dur ? Math.min(100, (d.pos||0)/d.dur*100) : 0) + '%';
    if (c) c.textContent = fmt(d.pos||0);
    if (u && d.dur) u.textContent = fmt(d.dur);
  });
  NE.on('toast', function(d){ toast(d.text||''); });
  NE.on('nav', function(d){ if(d.view) go(d.view, d); });

  // ================= Dock：图标 / 播放模式 / 桌面歌词 / 当前播放列表 =================
  var MODES = [
    { v: 'order',  ic: 'modeOrder',  t: '顺序播放' },
    { v: 'list',   ic: 'modeList',   t: '列表循环' },
    { v: 'single', ic: 'modeSingle', t: '单曲循环' },
    { v: 'random', ic: 'modeRandom', t: '随机播放' }
  ];
  var curMode = 'order';
  function modeInfo(v) { for (var i = 0; i < MODES.length; i++) if (MODES[i].v === v) return MODES[i]; return MODES[0]; }
  function applyMode(v, silent) {
    var m = modeInfo(v); curMode = m.v;
    var b = $('#pb-mode'); if (b) { b.innerHTML = SVG[m.ic]; b.title = '播放模式：' + m.t + '（点击切换）'; }
    if (!silent) { NE.setSetting('playMode', curMode); toast(m.t); }
  }
  function setLyricBtn(on) {
    var b = $('#pb-lyric'); if (!b) return;
    b.classList.toggle('on', !!on);
    b.title = on ? '桌面歌词：已开启（点击关闭）' : '桌面歌词：已关闭（点击开启）';
  }
  function initDockIcons() {
    var map = { 'pb-prev': SVG.prev, 'pb-next': SVG.next, 'pl-like': SVG.star, 'pl-dl': SVG.dl, 'pl-list': SVG.playlist };
    Object.keys(map).forEach(function (id) { var b = document.getElementById(id); if (b) b.innerHTML = map[id]; });
    var pb = $('#pb-play'); if (pb && !pb.innerHTML.trim()) pb.innerHTML = SVG.play;
    applyMode(curMode, true);
  }

  // ---- 当前播放列表面板 ----
  var plOpen = false, plCtxIndex = -1;

  function renderQueue() {
    var box = $('#plpanel-list'); if (!box) return;
    var cnt = $('#plpanel-count'); if (cnt) cnt.textContent = queue.length ? (queue.length + ' 首') : '';
    box.innerHTML = '';
    if (!queue.length) { box.innerHTML = '<div class="pl-empty">播放列表是空的</div>'; return; }
    queue.forEach(function (ns, i) {
      var it = el('div', 'pl-item' + (i === playingIndex ? ' on' : ''));
      it.innerHTML = '<span class="pl-item-idx">' + String(i + 1).padStart(2, '0') + '</span>'
        + '<span class="pl-item-title">' + esc(ns.Title) + '</span>'
        + '<span class="pl-item-artist">' + esc(ns.Artist) + '</span>';
      it.onclick = function () { NE.post({ type: 'play_index', index: i }); };
      it.oncontextmenu = function (e) { e.preventDefault(); e.stopPropagation(); showQueueMenu(e.clientX, e.clientY, i); };
      box.appendChild(it);
    });
    var cur = box.querySelector('.pl-item.on'); if (cur && cur.scrollIntoView) cur.scrollIntoView({ block: 'nearest' });
  }

  // 播放列表右键菜单：播放 / 下一首播放 / 删除 / 分享
  var PL_MENU = [
    { a: 'play', ic: 'play', t: '播放' },
    { a: 'next', ic: 'playNext', t: '下一首播放' },
    { a: 'remove', ic: 'trash', t: '删除' },
    { a: 'share', ic: 'share', t: '分享' }
  ];
  function ensureQueueMenu() {
    var m = $('#pl-ctx'); if (!m || m.dataset.ready) return m;
    m.innerHTML = '';
    PL_MENU.forEach(function (it) {
      var d = el('div', 'ctx-item');
      d.innerHTML = '<span class="ctx-ic">' + SVG[it.ic] + '</span><span>' + it.t + '</span>';
      d.onclick = function (e) {
        e.stopPropagation();
        hideQueueMenu();
        if (plCtxIndex < 0 || !queue[plCtxIndex]) return;
        var ns = queue[plCtxIndex], idx = plCtxIndex;
        if (it.a === 'play') NE.post({ type: 'play_index', index: idx });
        else if (it.a === 'next') { NE.post({ type: 'queue_next', index: idx }); toast('已把《' + ns.Title + '》设为下一首播放'); }
        else if (it.a === 'remove') { NE.post({ type: 'queue_remove', index: idx }); toast('已从播放列表移除'); }
        else if (it.a === 'share') NE.post({ type: 'share', song: ns });
      };
      m.appendChild(d);
    });
    m.dataset.ready = '1';
    return m;
  }
  function showQueueMenu(x, y, index) {
    var m = ensureQueueMenu(); if (!m) return;
    plCtxIndex = index;
    m.style.display = 'block';
    m.style.left = Math.min(x, window.innerWidth - 170) + 'px';
    m.style.top = Math.min(y, window.innerHeight - 170) + 'px';
  }
  function hideQueueMenu() { var m = $('#pl-ctx'); if (m) m.style.display = 'none'; plCtxIndex = -1; }
  document.addEventListener('click', hideQueueMenu);
  document.addEventListener('contextmenu', function (e) {
    if (plOpen && !(e.target.closest && e.target.closest('.pl-item'))) hideQueueMenu();
  });
  function toggleQueuePanel(force) {
    plOpen = (force === undefined) ? !plOpen : !!force;
    var p = $('#plpanel'); if (!p) return;
    p.classList.toggle('show', plOpen);
    if (plOpen) NE.post({ type: 'queue_get' });
  }

  initDockIcons();
  // 播放列表“清空”按钮：点一次变成确认态，再点才真的清空
  (function initClearBtn() {
    var b = $('#plpanel-clear'); if (!b) return;
    b.innerHTML = SVG.trash;
    var armed = false, timer = 0;
    b.onclick = function (e) {
      e.stopPropagation();
      if (!armed) {
        armed = true; b.classList.add('armed'); b.title = '再点一次确认清空';
        clearTimeout(timer); timer = setTimeout(function () { armed = false; b.classList.remove('armed'); b.title = '清空播放列表'; }, 3000);
        toast('再点一次确认清空播放列表');
        return;
      }
      armed = false; clearTimeout(timer); b.classList.remove('armed'); b.title = '清空播放列表';
      NE.post({ type: 'queue_clear' });
      toast('播放列表已清空');
    };
  })();
  $('#pb-play').onclick = () => { NE.post({type:'toggle'}); setPlaying(!$('#player').classList.contains('playing')); pop($('#pb-play')); };
  $('#pb-prev').onclick = () => NE.post({type:'prev'});
  $('#pb-next').onclick = () => NE.post({type:'next'});
  $('#pb-mode').onclick = function () {
    var vs = MODES.map(function (m) { return m.v; });
    applyMode(vs[(vs.indexOf(curMode) + 1) % vs.length], false);
  };
  // 桌面歌词按钮：暂时锁定（功能已实现但先不开放），点击只提示开发中
  var LYRIC_LOCKED = true;
  $('#pb-lyric').onclick = function () {
    if (LYRIC_LOCKED) { toast('桌面歌词开发中…'); return; }
    var on = !$('#pb-lyric').classList.contains('on');
    setLyricBtn(on); NE.post({ type: 'desktop_lyric', on: on });
  };
  if (LYRIC_LOCKED) {
    var lb = $('#pb-lyric');
    if (lb) { lb.classList.add('locked'); lb.title = '桌面歌词（开发中）'; }
  }
  $('#pl-list').onclick = function (e) { e.stopPropagation(); toggleQueuePanel(); };
  document.addEventListener('click', function (e) {
    if (plOpen && !(e.target.closest && (e.target.closest('#plpanel') || e.target.closest('#pl-list')))) toggleQueuePanel(false);
  });
  $('#pl-dl').onclick = () => { if(queue[playingIndex]) NE.post({type:'download', song:queue[playingIndex]}); };
  // 点击播放条封面 → 进入歌词页
  $('#pl-cover').onclick = () => openNowPlaying();
  $('#pl-like').onclick = () => toast('收藏开发中');
  // ---- 音量：滑块与真实音量双向同步；点喇叭图标静音/恢复 ----
  var lastVol = 80;
  function updateVolUI(v) {
    var s = $('#pl-volume'); if (s) s.value = v;
    var ic = $('#pl-vol-icon');
    if (ic) {
      ic.innerHTML = v <= 0 ? SVG.volMute : SVG.volOn;
      ic.classList.toggle('muted', v <= 0);
      ic.title = v <= 0 ? '已静音（点击恢复音量 ' + lastVol + '%）' : '音量 ' + v + '%（点击静音）';
    }
  }
  function applyVolume(v, post) {
    v = Math.max(0, Math.min(100, Math.round(Number(v) || 0)));
    if (v > 0) lastVol = v;
    updateVolUI(v);
    if (post) NE.post({ type: 'volume', v: v });
  }
  $('#pl-volume').oninput = e => applyVolume(e.target.value, true);
  var volIcon0 = $('#pl-vol-icon');
  if (volIcon0) volIcon0.onclick = () => { var cur = Number($('#pl-volume').value) || 0; applyVolume(cur > 0 ? 0 : lastVol, true); };
  NE.on('volume_changed', function (d) { applyVolume(d.v, false); });

  // ---- 全窗口歌词页：按钮 / Esc / 进度条跳转 ----
  (function initNowPlaying() {
    var np = document.getElementById('np'); if (!np) return;
    var close = document.getElementById('np-close');
    if (close) { close.innerHTML = ico('<polyline points="6 9 12 15 18 9"/>'); close.onclick = closeNowPlaying; }
    var dl = document.getElementById('np-download');
    if (dl) { dl.innerHTML = SVG.dl; dl.onclick = function () { var ns = queue[playingIndex]; if (ns) NE.post({ type: 'download', song: ns }); }; }
    var sh = document.getElementById('np-share');
    if (sh) { sh.innerHTML = SVG.share; sh.onclick = function () { var ns = queue[playingIndex]; if (ns) NE.post({ type: 'share', song: ns }); }; }
    // 进度条：点击 / 拖动跳转（时长取播放器真实时长，队列里的 Duration 常常是 0）
    function npSeekRatio(clientX) {
      var prog = document.getElementById('np-prog'); if (!prog) return -1;
      var r = prog.getBoundingClientRect();
      if (!r.width) return -1;
      return Math.min(1, Math.max(0, (clientX - r.left) / r.width));
    }
    function npApplySeek(clientX, commit) {
      var ratio = npSeekRatio(clientX); if (ratio < 0) return;
      var d = npDurMs || (queue[playingIndex] && queue[playingIndex].Duration) || 0;
      if (d <= 0) { if (commit) toast('时长还没加载出来，稍后再试'); return; }
      var ms = Math.round(ratio * d);
      var f = document.getElementById('np-fill');
      if (f) f.style.width = (ratio * 100) + '%';
      var c = document.getElementById('np-cur');
      if (c) c.textContent = fmt(ms);
      if (commit) { NE.post({ type: 'seek', pos: ms }); npPosMs = ms; }
    }
    var npProg = document.getElementById('np-prog');
    if (npProg) {
      var dragging = false;
      npProg.onmousedown = function (e) { e.preventDefault(); dragging = true; npApplySeek(e.clientX, false); };
      document.addEventListener('mousemove', function (e) { if (dragging) npApplySeek(e.clientX, false); });
      document.addEventListener('mouseup', function (e) {
        if (!dragging) return;
        dragging = false;
        npApplySeek(e.clientX, true);
      });
    }
    // 播放控制
    function npIcon(id, svg) { var b = document.getElementById(id); if (b) b.innerHTML = svg; return b; }
    var pv = npIcon('np-prev', SVG.prev); if (pv) pv.onclick = function () { NE.post({ type: 'prev' }); };
    var nx = npIcon('np-next', SVG.next); if (nx) nx.onclick = function () { NE.post({ type: 'next' }); };
    var gear = npIcon('np-gear', ico('<circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.9l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.9-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1A1.7 1.7 0 0 0 8.9 19a1.7 1.7 0 0 0-1.9.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.9 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1A1.7 1.7 0 0 0 4.9 8.9a1.7 1.7 0 0 0-.3-1.9l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.9.3H9.4a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.9-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.9v.1a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z"/>'));
    if (gear) gear.onclick = function (e) { e.stopPropagation(); npToggleSettings(); };
    var pl = npIcon('np-play', npPlaying ? SVG.pause : SVG.play);
    if (pl) pl.onclick = function () { NE.post({ type: 'toggle' }); npSetPlaying(!npPlaying); };
    // dock 的进度条也支持点击跳转
    var dockBar = document.getElementById('pl-bar');
    if (dockBar) dockBar.onclick = function (e) { npApplySeek(e.clientX, true); };
  })();

  // TEMP-SEEK-TEST：自动放歌 → 打开歌词页 → 合成“点击进度条 50%”
  NE.on('queue', function (d) {
    queue = (d.songs || []).map(normSong);
    playingIndex = (typeof d.index === 'number') ? d.index : -1;
    renderQueue();
    var cur = queue[playingIndex];
    if (cur && $('#pl-title').textContent === '未在播放') showSongMeta(cur);
  });
  NE.on('queue_changed', function (d) {
    if (d && typeof d.index === 'number') { playingIndex = d.index; if (plOpen) { NE.post({ type: 'queue_get' }); } }
    else if (plOpen) NE.post({ type: 'queue_get' });
  });
  NE.on('desktop_lyric_state', function (d) { if (!LYRIC_LOCKED) setLyricBtn(!!d.on); });
  // 启动时同步：播放模式 / （未锁定时）桌面歌词状态 / 上次的播放列表
  NE.getSettings().then(function (s) {
    try {
      appSettings = s || {};
      applyMode(s.playMode || 'order', true);
      applyVolume(s.volume != null ? s.volume : 80, false);   // 音量滑块跟随真实音量，别再出现“滑块 80% 实际静音”
      npApplyLyricSettings(s);                                 // 歌词页外观设置
      hotkeysFromSettings(s);                                  // 快捷键绑定
      if (!LYRIC_LOCKED) setLyricBtn(String(s.desktopLyric) !== 'false' && s.desktopLyric !== false);
    } catch (e) { }
  }).catch(function () { });
  setTimeout(function () { NE.post({ type: 'queue_get' }); }, 900);

  const navLinks = document.querySelectorAll('#sidebar a[data-nav]');
  navLinks.forEach(a => a.onclick = () => {
    if (a.dataset.nav === 'settings') { navLinks.forEach(x => x.classList.remove('on')); a.classList.add('on'); go('settings'); return; }
    navLinks.forEach(x => x.classList.remove('on')); a.classList.add('on'); go(a.dataset.nav);
  });

  // 顶栏搜索框 → 触发后端搜索并在主内容区展示结果
  const topSearch = document.getElementById('topbar-search');
  if (topSearch) {
    topSearch.addEventListener('keydown', e => { if (e.key === 'Enter') { const kw = topSearch.value.trim(); if (kw) doTopSearch(kw); } });
  }
  async function doTopSearch(kw) {
    loading();
    try {
      const r = await NE.search(kw, 50, 0);
      const songs = (r.result && r.result.songs) || [];
      const html = el('div', 'page');
      html.appendChild(el('h2', 'page-title', '“' + esc(kw) + '” 搜索结果 (' + songs.length + ')'));
      const dl = el('div'); renderSongs(songs, dl); html.appendChild(dl);
      view.innerHTML = ''; view.appendChild(html);
      document.querySelectorAll('#sidebar a[data-nav]').forEach(x => x.classList.remove('on'));
    } catch (e) { toast('搜索失败: ' + e.message); }
  }

  // ----- 自定义右键菜单（屏蔽 html 默认右键） -----
  document.addEventListener('contextmenu', e => e.preventDefault());
  const menu = document.createElement('div'); menu.className='ctx-menu'; menu.style.display='none'; menu.innerHTML =
    '<div class="ctx-item" data-a="play">播放</div><div class="ctx-item" data-a="next">下一首播放</div><div class="ctx-item" data-a="dl">下载</div>';
  document.body.appendChild(menu);
  let ctxSong = null;
  menu.querySelectorAll('.ctx-item').forEach(it => { it.onclick = (e)=>{ e.stopPropagation(); const a=it.getAttribute('data-a'); if(ctxSong){ if(a==='play') play(ctxSong); else if(a==='next') { NE.post({type:'playnext', song:ctxSong}); toast('已加入下一首播放'); } else if(a==='dl') NE.post({type:'download', song:ctxSong}); } hideMenu(); }; });
  document.addEventListener('click', ()=>hideMenu());
  function hideMenu(){ menu.style.display='none'; }
  function showMenu(x,y,song){ ctxSong=song; menu.style.display='block'; menu.style.left=x+'px'; menu.style.top=y+'px'; }

  // 给列表行的右键绑定自定义菜单
  view.addEventListener('contextmenu', function(e){
    const row = e.target.closest('.song-row');
    if(row){ e.preventDefault(); e.stopPropagation(); const idx=Number(row.querySelector('.sr-idx').textContent)-1; const ns = currentList[idx]; if(ns) showMenu(e.clientX, e.clientY, ns); }
  });


  // ---------- 更新公告弹窗（由 C# 比对 config [Version] 后触发）----------
  (function notice() {
    var mask = document.getElementById('notice'); if (!mask) return;
    var verEl = document.getElementById('notice-ver');
    var leadEl = document.getElementById('notice-lead');
    var pending = null;   // 待回写的版本号（看完公告才写入 config）
    var closed = false;

    // 只有点「知道了」才算看过：已去掉右上角 ×、点遮罩关闭、Esc 关闭，避免用户没看到内容就关掉
    function close() {
      if (closed) return;
      closed = true;
      mask.classList.remove('show'); mask.setAttribute('aria-hidden', 'true');
      if (pending) { NE.post({ type: 'notice_seen', version: pending }); pending = null; }
    }
    mask.querySelectorAll('[data-close]').forEach(function (b) { b.onclick = close; });

    // C# 检测到 config 里的版本比当前版本旧 → 弹更新公告
    NE.on('update_notice', function (d) {
      if (!d) return;
      var from = d.from ? d.from : '首次运行';
      pending = d.to;
      closed = false;
      if (leadEl) leadEl.innerHTML = '软件已更新到 <b>netHEmusic ' + esc(d.to) + '</b>。<span class="modal-dim">（本次更新说明留空，下次发版在 index.html 的公告正文注释里填写）</span>';
      if (verEl) verEl.textContent = '版本：' + from + ' → ' + d.to;
      mask.classList.add('show');
      mask.setAttribute('aria-hidden', 'false');
    });
  })();

  window.addEventListener('load', ()=>{ NE.post({type:'discover'}); go('home'); });
})();
