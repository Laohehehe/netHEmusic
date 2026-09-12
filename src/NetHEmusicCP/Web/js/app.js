// app.js — YesPlayMusic-style full player
(function () {
  const NE = window.NE; const $ = s => document.querySelector(s);
  const view = $('#view');
  let queue = []; let playingIndex = -1; let currentList = [];

  function el(tag, cls, html) { const e = document.createElement(tag); if (cls) e.className = cls; if (html !== undefined) e.innerHTML = html; return e; }
  function fmt(ms) { if (!ms || ms <= 0) return '00:00'; const s = Math.floor(ms/1000); return String(Math.floor(s/60)).padStart(2,'0')+':'+String(s%60).padStart(2,'0'); }
  function esc(s){ return String(s||'').replace(/[&<>"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c])); }
  function normSong(s) { const artists=(s.ar||s.artists||[]).map(a=>a.name).join(' / '); const al=s.al||s.album||{}; return { Id:s.id, Title:s.name||'未知歌曲', Artist:artists, Album:al.name||'', Pic: s.al?al.picUrl:(s.album?al.picUrl:(s.pic||'')), Duration:s.dt||s.duration||0 }; }
  function toast(t) { const el=$('#toast'); el.textContent=t; el.style.display='block'; setTimeout(()=>el.style.display='none',2000); }
  function loading() { view.innerHTML = '<div class="big-load">正在加载…</div>'; }

  function play(ns) { NE.post({ type:'play', song: ns }); setPlayer(ns); }
  function setPlayer(ns) { if(!ns) return; $('#pl-title').textContent = ns.Title; $('#pl-artist').textContent = ns.Artist; const c=$('#pl-cover'); if(ns.Pic){ c.src = ns.Pic.replace(/\^\d+\^/,''); } else c.removeAttribute('src'); $('#pb-play').innerHTML = '<i>&#xE769;</i>'; }

  var SVG = {
    play: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M8 5v14l11-7z"/></svg>',
    add:  '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M14 10H3v2h11v-2zM14 6H3v2h11V6zm-3 8H3v2h8v-2zm5-4v3h-3v2h3v3h2v-3h3v-2h-3v-3h-2z"/></svg>',
    dl:   '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M5 20h14v-2H5v2zM17 8h-4V2H11v6H7l5 5 5-5z"/></svg>'
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

  async function go(viewName, data) {
    try {
      if (viewName==='home') return goHome();
      if (viewName==='recommend') return goRecommend();
      if (viewName==='toplist') return goToplist();
      if (viewName==='playlist') return data && data.id ? goPlaylist(data.id, data.name) : goPlaylists();
      if (viewName==='search') return goSearch(data);
      if (viewName==='lyric') return goLyric();
      if (viewName==='account') return goAccount();
      if (viewName==='settings') return goSettings();
      if (viewName==='liked') return goLiked();
    } catch(e) { view.innerHTML = '<div class="big-load">加载失败: '+esc(e.message)+'</div>'; }
  }

  async function goHome() {
    loading();
    const [ r, st ] = await Promise.all([ NE.recommend(), NE.loginStatus().catch(function(){ return {}; }) ]);
    const songs = (r.data && r.data.dailySongs) || [];
    var nick = (st && st.profile && st.profile.nickname) ? st.profile.nickname : '朋友';
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
  async function goLyric() {
    loading();
    const ns = queue[playingIndex];
    if(!ns){ view.innerHTML='<div class="big-load">请先播放一首歌再查看歌词</div>'; return; }
    try { const r = await NE.lyric(ns.Id); const lrc=(r.lrc&&r.lrc.lyric)||'暂无歌词'; const html=el('div','page'); html.appendChild(el('h2','page-title','歌词 — '+esc(ns.Title))); html.appendChild(el('pre','lyric-pre', esc(lrc))); view.innerHTML=''; view.appendChild(html); }
    catch(e){ view.innerHTML='<div class="big-load">歌词加载失败</div>'; }
  }
  async function goLiked() {
    loading();
    const html = el('div','page'); html.appendChild(el('h2','page-title','我喜欢的音乐'));
    try { const r=await NE.recommend(); const songs=(r.data&&r.data.dailySongs)||[]; const dl=el('div'); renderSongs(songs,dl); html.appendChild(dl); } catch(e){ html.appendChild(el('div','big-load','加载失败')); }
    view.innerHTML=''; view.appendChild(html);
  }
  async function goAccount() {
    loading();
    const html = el('div','page'); html.appendChild(el('h2','page-title','账号'));
    try {
      const st = await NE.loginStatus(); const p = st.profile || {};
      if(p.userId){ html.appendChild(el('div','account-info','<img src="'+(p.avatarUrl||'')+'?param=100y100"><div class="acc-nick">'+esc(p.nickname||'')+'</div><div class="acc-sub">已登录 · uid='+p.userId+'</div>')); const out=el('button','action-btn','退出登录'); out.onclick=async()=>{ try{await NE.logout(); toast('已退出'); go('account');}catch(e){} }; html.appendChild(out); }
      else {
        html.appendChild(el('p','muted','未登录，扫码登录'));
        const btn = el('button','action-btn','扫码登录'); const img = el('img','qr'); img.style.display='none'; img.width=200; img.height=200;
        btn.onclick = async ()=>{ try{ const k=await NE.loginQrKey(); const key=k.unikey; const c=await NE.loginQrCreate(key); if(c.qrimg){ img.src=c.qrimg; img.style.display='block'; } toast('请用网易云音乐扫码'); for(let i=0;i<120;i++){ await new Promise(r=>setTimeout(r,3000)); const ch=await NE.loginQrCheck(key); if(ch.code===803){ if(ch.cookie) NE.post({type:'save_cookie', cookie: ch.cookie}); toast('登录成功'); go('account'); return; } } } catch(e){ toast('登录失败: '+e.message); } };
        html.appendChild(btn); html.appendChild(img);
      }
    } catch(e){ html.appendChild(el('div','big-load','获取登录状态失败')); }
    view.innerHTML=''; view.appendChild(html);
  }

  // ---------- 设置（内嵌主窗口的网页设置页） ----------
  async function goSettings() {
    loading();
    var s = await NE.getSettings();
    var html = el('div','page');
    html.appendChild(el('h2','page-title','设置'));
    function group(title, rows) { var g = el('div','set-group'); g.appendChild(el('h3','',title)); rows.forEach(function(r){ g.appendChild(r); }); return g; }
    function sw(label, key, val) { var r = el('div','set-row'); r.appendChild(el('label','',label)); var t = el('div','set-switch'+(val?' on':'')); t.onclick = function(){ var on=!t.classList.contains('on'); t.classList.toggle('on',on); NE.setSetting(key, on?'true':'false'); if(key==='uiEffects' && window.FX) window.FX.setEnabled(on); }; r.appendChild(t); return r; }
    function sel(label, key, val, opts) { var r = el('div','set-row'); r.appendChild(el('label','',label)); var sl=el('select'); opts.forEach(function(o){ var op=el('option','',o); op.value=o; if(o===val) op.selected=true; sl.appendChild(op); }); sl.onchange=function(){ NE.setSetting(key, sl.value); }; r.appendChild(sl); return r; }
    function txt(label, key, val) { var r = el('div','set-row'); r.appendChild(el('label','',label)); var i=el('input'); i.type='text'; i.value=val||''; i.onchange=function(){ NE.setSetting(key, i.value); }; r.appendChild(i); return r; }
    function rng(label, key, val) { var r = el('div','set-row'); var lb=el('label','',label+' ('+(val||80)+')'); r.appendChild(lb); var i=el('input'); i.type='range'; i.min=0; i.max=100; i.value=val||80; i.oninput=function(){ lb.textContent=label+' ('+i.value+')'; NE.setSetting(key, i.value); }; r.appendChild(i); return r; }
    function info(label) { var r = el('div','set-row'); r.appendChild(el('label','',label)); return r; }
    function custSel(label, key, val, opts) {
      var r = el('div','set-row'); r.appendChild(el('label','',label));
      var box = el('div','cust-select');
      var v = el('div','cs-value'); v.innerHTML = '<span>'+esc(val)+'</span><i class="ic">&#xE70D;</i>';
      var list = el('div','cs-list');
      (opts||[]).forEach(function(o){ var it = el('div','cs-item'+(o===val?' sel':''),esc(o)); it.onclick=function(e){ e.stopPropagation(); var rr=it.getBoundingClientRect(); if(key==='scheme'){ rippleArm(rr.left+rr.width/2, rr.top+rr.height/2, [function(){ box.classList.remove('open'); }]); } else { box.classList.remove('open'); } NE.setSetting(key,o); v.querySelector('span').textContent=o; list.querySelectorAll('.cs-item').forEach(function(x){x.classList.remove('sel');}); it.classList.add('sel'); if(key==='scheme'){ setTimeout(function(){ box.classList.remove('open'); }, 1200); } }; list.appendChild(it); });
      v.onclick=function(e){ e.stopPropagation(); document.querySelectorAll('.cust-select.open').forEach(function(x){ if(x!==box) x.classList.remove('open'); }); box.classList.toggle('open'); };
      box.appendChild(v); box.appendChild(list); r.appendChild(box);
      document.addEventListener('click', function(){ box.classList.remove('open'); });
      return r;
    }
    html.appendChild(group('主题 / 外观', [ custSel('配色方案','scheme', s.scheme, s.schemes||[]), sw('Mica 背景','mica', s.mica), sw('鼠标特效（拖尾 / 点击 / 抖动）','uiEffects', s.uiEffects), sw('关闭按钮最小化到托盘','closeToTray', s.closeToTray) ]));
    html.appendChild(group('下载', [ txt('默认下载目录','downloadDir', s.downloadDir), sel('音质','quality', s.quality, ['standard','high','lossless']) ]));
    html.appendChild(group('播放', [ rng('默认音量','volume', s.volume), sel('播放模式','playMode', s.playMode, ['order','list','single','random']) ]));
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
  NE.on('playing', function(d){ if(d.song) setPlayer(d.song); });
  NE.on('position', function(d){ $('#pl-cur').textContent=fmt(d.pos||0); if(d.dur){ $('#pl-dur').textContent=fmt(d.dur); } $('#pl-fill').style.width=(d.dur?Math.min(100,(d.pos||0)/d.dur*100):0)+'%'; });
  NE.on('toast', function(d){ toast(d.text||''); });
  NE.on('nav', function(d){ if(d.view) go(d.view, d); });

  $('#pb-play').onclick = () => NE.post({type:'toggle'});
  $('#pb-prev').onclick = () => NE.post({type:'prev'});
  $('#pb-next').onclick = () => NE.post({type:'next'});
  $('#pl-dl').onclick = () => { if(queue[playingIndex]) NE.post({type:'download', song:queue[playingIndex]}); };
  // 点击播放条封面 → 进入歌词页
  $('#pl-cover').onclick = () => { document.querySelectorAll('#nav a').forEach(x=>x.classList.remove('on')); go('lyric'); };
  $('#pl-like').onclick = () => toast('收藏开发中');
  $('#pl-volume').oninput = e => NE.post({type:'volume', v: e.target.value});

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


  window.addEventListener('load', ()=>{ NE.post({type:'discover'}); go('home'); });
})();
