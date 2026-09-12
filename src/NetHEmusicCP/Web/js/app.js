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
  function pop(el) { if(!el) return; el.classList.remove('fx-pop'); void el.offsetWidth; el.classList.add('fx-pop'); }
  function setPlaying(on) { var p = $('#player'); if(p) p.classList.toggle('playing', !!on); var pb = $('#pb-play'); if(pb) pb.innerHTML = '<i class="ic">' + (on ? '&#xE769;' : '&#xE768;') + '</i>'; }
  function setPlayer(ns) {
    if(!ns) return;
    $('#pl-title').textContent = ns.Title;
    $('#pl-artist').textContent = ns.Artist;
    var c = $('#pl-cover'), wrap = $('#pl-cover-wrap');
    if(ns.Pic){ c.src = ns.Pic.replace(/\^\d+\^/,''); if(wrap) wrap.classList.add('has-cover'); }
    else { c.removeAttribute('src'); if(wrap) wrap.classList.remove('has-cover'); }
    setPlaying(true);
    pop($('#pb-play'));
  }

  // 图标全部用圆角几何（rx / 圆弧），配合 css 里的无底色大图标
  var SVG = {
    play: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M9.1 5.2c-1-.6-2.3.1-2.3 1.3v11c0 1.2 1.3 1.9 2.3 1.3l9.2-5.5c1-.6 1-2 0-2.6L9.1 5.2z"/></svg>',
    add:  '<svg viewBox="0 0 24 24" aria-hidden="true">'
        + '<rect x="3" y="5.4" width="11" height="2.6" rx="1.3"/>'
        + '<rect x="3" y="10.7" width="11" height="2.6" rx="1.3"/>'
        + '<rect x="3" y="16" width="7.5" height="2.6" rx="1.3"/>'
        + '<rect x="16.2" y="9.6" width="2.6" height="12" rx="1.3"/>'
        + '<rect x="11.5" y="14.3" width="12" height="2.6" rx="1.3"/>'
        + '</svg>',
    // 描边风格：下载（用户提供的图标，fill:none 写在内联样式里，避免被 .row-btn svg 的 fill 覆盖）
    dl:   '<svg viewBox="0 0 24 24" aria-hidden="true" style="fill:none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">'
        + '<path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/>'
        + '<polyline points="7 10 12 15 17 10"/>'
        + '<line x1="12" x2="12" y1="15" y2="3"/>'
        + '</svg>'
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
    function cfgGet(s, k, def) { var v = (s && s.app) ? s.app[k] : undefined; return (v === undefined || v === null || v === '') ? def : v; }
    function cfgBool(s, k, def) { var v = cfgGet(s, k, def); return String(v) !== 'false' && v !== false; }
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

    html.appendChild(group('主题 / 外观', [ custSel('配色方案','scheme', s.scheme, s.schemes||[]), sw('Mica 背景','mica', s.mica), sw('关闭按钮最小化到托盘','closeToTray', s.closeToTray) ]));
    html.appendChild(fxGroup(s));
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

  $('#pb-play').onclick = () => { NE.post({type:'toggle'}); setPlaying(!$('#player').classList.contains('playing')); pop($('#pb-play')); };
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
