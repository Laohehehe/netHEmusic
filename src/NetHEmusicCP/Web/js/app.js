// app.js — YesPlayMusic-style full player
// ---- 前端异常上报：把 JS 错误写进应用日志，便于排查（以前前端报错在日志里完全看不到）----
window.addEventListener('error', function (e) {
  try { (window.NE && NE.post) ? NE.post({ type: 'log', msg: '[jserr] ' + (e.message || '') + ' @' + (e.lineno || 0) + ':' + (e.colno || 0) + ' ' + ((e.filename || '').split('/').pop() || '') }) : 0; } catch (x) { }
}, true);
window.addEventListener('unhandledrejection', function (e) {
  try { var r = e.reason; (window.NE && NE.post) ? NE.post({ type: 'log', msg: '[jserr] promise: ' + ((r && r.message) || r) }) : 0; } catch (x) { }
});


// ---- 全局把 requestAnimationFrame 限到 60fps ----
// 高刷屏（例如 180Hz）下浏览器按显示器刷新率出帧，界面里的 JS 动画没必要跑 180fps
(function () {
  var orig = window.requestAnimationFrame ? window.requestAnimationFrame.bind(window) : null;
  if (!orig) return;
  var MIN = 1000 / 62;
  window.requestAnimationFrame = function (cb) {
    var last = 0;                       // 每条调用链各自计时，互不抢额度
    function step(ts) {
      var now = performance.now();
      if (now - last < MIN) { orig(step); return; }
      last = now;
      cb(ts);
    }
    return orig(step);
  };
})();

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
  var HK_OFF = 'none';                     // 存进 config 的“已关闭”标记
  function hotkeysFromSettings(s) {
    hotkeys = {};
    for (var k in HK_DEFAULTS) {
      var v = String(cfgGet(s, k, HK_DEFAULTS[k]) || HK_DEFAULTS[k]);
      hotkeys[k] = (v === HK_OFF) ? '' : v;   // 空串 = 不绑定
    }
  }
  function isTyping(el) {
    return !!el && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.tagName === 'SELECT' || el.isContentEditable);
  }
  // ================= 背景音乐律动（Phase B，用真实频谱）=================
  var VZ = {
    on: false, style: 'bars', strength: 60, sens: 100,
    canvas: null, c2d: null, raf: 0, w: 0, h: 0, dpr: 1, peak: 0
  };
  function vzSetup() {
    var c = document.getElementById('np-bgfx');
    if (!c) return;
    VZ.canvas = c;
    VZ.c2d = c.getContext('2d');
    vzResize();
  }
  function vzResize() {
    var c = VZ.canvas; if (!c) return;
    if (!c.clientWidth || !c.clientHeight) return;      // 还没显示出来就别量，避免量成 0 后画到可视区外
    var dpr = Math.min(2, window.devicePixelRatio || 1);
    var w = c.clientWidth, h = c.clientHeight;
    c.width = Math.max(1, Math.round(w * dpr));
    c.height = Math.max(1, Math.round(h * dpr));
    VZ.w = c.width; VZ.h = c.height; VZ.dpr = dpr;
  }
  function vzAccent(alpha) {
    try {
      var v = getComputedStyle(document.documentElement).getPropertyValue('--md-accent-color').trim() || '#8ab4f8';
      return 'rgba(' + hexToRgb(v).join(',') + ',' + alpha + ')';
    } catch (e) { return 'rgba(255,255,255,' + alpha + ')'; }
  }
  function hexToRgb(hex) {
    var m = /^#?([a-f\d]{2})([a-f\d]{2})([a-f\d]{2})$/i.exec((hex || '').trim());
    if (!m) return [200, 210, 255];
    return [parseInt(m[1], 16), parseInt(m[2], 16), parseInt(m[3], 16)];
  }
  var VZ_FRAME_MS = 30;               // 频谱帧率（可在设置里改，默认 33fps）
  var __vzLast = 0;
  function vzFrame(ts) {
    if (!VZ.on || !npOpen) { VZ.raf = 0; return; }
    VZ.raf = requestAnimationFrame(vzFrame);
    if (ts && ts - __vzLast < VZ_FRAME_MS) return;
    __vzLast = ts || 0;
    var c = VZ.c2d; if (!c) return;
    var el = VZ.canvas;
    if (el && el.clientWidth && Math.abs(el.clientWidth * VZ.dpr - VZ.w) > 1) vzResize();   // 尺寸变了（或首次量到 0）就重算
    var fft = (window.AUFft && VZ.on && npOpen) ? window.AUFft() : null;
    c.clearRect(0, 0, VZ.w, VZ.h);
    if (!fft) return;
    var mx0 = 0; for (var z = 0; z < fft.length; z++) { if (fft[z] > mx0) mx0 = fft[z]; }
    if (mx0 < 3) { VZ._silent = true; return; }     // 静音：清屏后直接返回，不画
    VZ._silent = false;
    var k = (VZ.strength / 100) * (VZ.sens / 100) * 2.2;
    var n = fft.length;
    if (VZ.style === 'ring') {
      // 环形：绕封面一圈
      var cover = document.querySelector('#np .np-cover-wrap');
      var cx = VZ.w * 0.5, cy = VZ.h * 0.5, R = Math.min(VZ.w, VZ.h) * 0.22;
      if (cover) {
        var r = cover.getBoundingClientRect();
        cx = (r.left + r.width / 2) * VZ.dpr;
        cy = (r.top + r.height / 2) * VZ.dpr;
        R = (r.width / 2 + 10) * VZ.dpr;
      }
      c.save();
      c.translate(cx, cy);
      for (var i = 0; i < n; i++) {
        var v = fft[i] / 255 * k;
        if (v <= 0.01) continue;
        var a = (i / n) * Math.PI * 2 - Math.PI / 2;
        var len = 6 * VZ.dpr + v * 46 * VZ.dpr;
        c.save();
        c.rotate(a);
        c.beginPath();
        c.lineWidth = Math.max(2, VZ.dpr * 3);
        c.lineCap = 'round';
        c.strokeStyle = vzAccent(0.10 + 0.42 * Math.min(1, v));
        c.moveTo(R, 0); c.lineTo(R + len, 0);
        c.stroke();
        c.restore();
      }
      c.restore();
    } else if (VZ.style === 'wave') {
      // 波形：一条横向波形带
      var mid = VZ.h * 0.72, amp = VZ.h * 0.12 * (VZ.strength / 100) * (VZ.sens / 100);
      c.beginPath();
      for (var i2 = 0; i2 < n; i2++) {
        var x = VZ.w * (i2 / (n - 1));
        var y = mid - (fft[i2] / 255 - 0.35) * amp * 2;
        if (i2 === 0) c.moveTo(x, y); else c.lineTo(x, y);
      }
      c.lineWidth = Math.max(2, VZ.dpr * 2.5);
      c.strokeStyle = vzAccent(0.5);
      c.shadowColor = vzAccent(0.45); c.shadowBlur = 18 * VZ.dpr;
      c.stroke();
      c.shadowBlur = 0;
    } else {
      // 柱状：底部一排（渐变整帧只建一次，之前每根柱子建一次 gradient 非常费）
      var bars = 40, gap = VZ.dpr * 4;
      var bw = (VZ.w - gap * (bars + 1)) / bars;
      var base = VZ.h - VZ.dpr * 6;
      var gr = c.createLinearGradient(0, base - VZ.h * 0.34, 0, base);
      gr.addColorStop(0, vzAccent(0.95));
      gr.addColorStop(0.55, vzAccent(0.62));
      gr.addColorStop(1, vzAccent(0.22));
      c.fillStyle = gr;
      for (var b = 0; b < bars; b++) {
        var idx = Math.floor(Math.pow(b / bars, 1.5) * (n - 1));
        var val = fft[idx] / 255 * k;
        var bh = Math.max(2 * VZ.dpr, val * VZ.h * 0.34);
        var x2 = gap + b * (bw + gap);
        var rr = Math.min(bw / 2, 4 * VZ.dpr);
        c.beginPath();
        c.moveTo(x2, base);
        c.lineTo(x2, base - bh + rr);
        c.quadraticCurveTo(x2, base - bh, x2 + rr, base - bh);
        c.lineTo(x2 + bw - rr, base - bh);
        c.quadraticCurveTo(x2 + bw, base - bh, x2 + bw, base - bh + rr);
        c.lineTo(x2 + bw, base);
        c.closePath();
        c.fill();
      }
    }
  }
  function vzApply() {
    var on = !!lyStyles.vz;
    VZ.style = lyStyles.vzStyle || 'bars';
    VZ.strength = (lyStyles.vzStrength == null) ? 60 : lyStyles.vzStrength;
    VZ.sens = (lyStyles.vzSens == null) ? 100 : lyStyles.vzSens;
    VZ.on = on;
    var c = document.getElementById('np-bgfx');
    if (c) c.style.display = (on && npOpen) ? 'block' : 'none';   // 先显示，再量尺寸
    if (on) { try { if (!VZ.c2d) vzSetup(); vzResize(); if (!VZ.raf) vzFrame(); } catch (e8) { try { NE.post({ type: 'log', msg: '[vz] start ERROR ' + e8.message }); } catch (e7) { } } }
    else if (VZ.raf) { cancelAnimationFrame(VZ.raf); VZ.raf = 0; if (VZ.c2d) VZ.c2d.clearRect(0, 0, VZ.w, VZ.h); }
  }
  window.addEventListener('resize', function () { if (VZ.on) vzResize(); });

  // ================= 前端播放内核（Phase A）=================
  // 音频由本页的 <audio> 播放，接 Web Audio 的 Analyser 拿真实频谱（背景律动用）。
  // C# 侧只负责解析直链 / 队列与模式 / 持久化 / SMTC。
  // 双播放器：交叉淡化时两个 <audio> 各自接一个 GainNode，混流后进 Analyser
  var AU = {
    ps: [null, null], cur: 0,
    ctx: null, analyser: null, freq: null,
    playing: false, durMs: 0, posMs: 0, lastPost: 0, curIndex: -1,
    xfade: 0, vol: 70, fading: 0
  };
  function auCtx() {
    if (AU.ctx) return AU.ctx;
    try {
      var AC = window.AudioContext || window.webkitAudioContext;
      AU.ctx = new AC();
      AU.analyser = AU.ctx.createAnalyser();
      AU.analyser.fftSize = 256;
      AU.analyser.smoothingTimeConstant = 0.8;
      AU.freq = new Uint8Array(AU.analyser.frequencyBinCount);
      AU.analyser.connect(AU.ctx.destination);
      NE.post({ type: 'log', msg: '[au] AudioContext ready, bins=' + AU.analyser.frequencyBinCount });
    } catch (e) { NE.post({ type: 'log', msg: '[au] AudioContext FAILED: ' + e.message }); }
    return AU.ctx;
  }
  function auMaster() { return AU.vol / 100; }
  function auMake(i) {
    if (AU.ps[i]) return AU.ps[i];
    var el = document.createElement('audio');
    el.crossOrigin = 'anonymous';        // 必须：否则 createMediaElementSource 后频谱被跨域污染
    el.preload = 'auto';
    el.style.display = 'none';
    document.body.appendChild(el);
    var p = { el: el, src: null, gain: null, idx: i };
    AU.ps[i] = p;
    el.addEventListener('loadedmetadata', function () { if (p !== AU.ps[AU.cur]) return; AU.durMs = (el.duration || 0) * 1000; auPushUI(0); auPostState(true); });
    el.addEventListener('durationchange', function () { if (p !== AU.ps[AU.cur]) return; AU.durMs = (el.duration || 0) * 1000; auPushUI(AU.posMs); });
    el.addEventListener('timeupdate', function () { if (p !== AU.ps[AU.cur]) return; AU.posMs = (el.currentTime || 0) * 1000; pcBuffer(el); auPushUI(AU.posMs); auPostState(false); });
    el.addEventListener('progress', function () { if (p !== AU.ps[AU.cur]) return; pcBuffer(el); });
    el.addEventListener('play', function () { if (p !== AU.ps[AU.cur]) return; AU.playing = true; PC.playing = true; PC.t0 = performance.now(); pcKick(); setPlaying(true); auPostState(true); });
    el.addEventListener('pause', function () { if (p !== AU.ps[AU.cur]) return; AU.playing = false; PC.playing = false; pcPaint(); setPlaying(false); auPostState(true); });
    el.addEventListener('ended', function () { if (p !== AU.ps[AU.cur]) return; AU.playing = false; auPostState(true); NE.post({ type: 'audio_ended' }); });
    el.addEventListener('error', function () {
      if (p !== AU.ps[AU.cur]) return;
      var msg = (el.error && el.error.message) || 'unknown';
      try { NE.post({ type: 'log', msg: '[au] error ' + (el.error && el.error.code) + ' ' + msg }); } catch (e) { }
      NE.post({ type: 'audio_error', message: msg });
    });
    return p;
  }
  function auWire(p) {
    auCtx();
    if (!AU.ctx || p.src) return;
    try {
      p.src = AU.ctx.createMediaElementSource(p.el);
      p.gain = AU.ctx.createGain();
      p.gain.gain.value = 1;
      p.src.connect(p.gain);
      p.gain.connect(AU.analyser);
    } catch (e) { try { NE.post({ type: 'log', msg: '[au] wire failed: ' + e.message }); } catch (e2) { } }
  }
  function auCur() {
    if (!AU.ps[AU.cur]) auMake(AU.cur);
    return AU.ps[AU.cur];
  }
  function auFft() {
    if (!AU.analyser || !AU.freq) return null;
    try { AU.analyser.getByteFrequencyData(AU.freq); return AU.freq; } catch (e) { return null; }
  }
  // ---- 进度条平滑推进（参考 BetterNCM/FluentProgessBar：用本地时钟插值，而不是每次上报就跳一格）----
  var PC = { pos: 0, t0: 0, playing: false, dur: 0, drag: false, raf: 0 };
  function pcSet(pos, playing, dur) {
    var now = performance.now();
    var next = Number(pos) || 0;
    var est = PC.pos + ((PC.playing && PC.t0) ? (now - PC.t0) : 0);   // 本地估算到的位置
    var delta = next - est;
    if (Math.abs(delta) > 1200 || !PC.t0) { PC.pos = next; }            // 跳转/换歌/首次：直接对齐
    else { PC.pos = est + delta * 0.12; }                               // 正常播放：只向目标靠 12%，抹掉量化回跳
    PC.t0 = now;
    if (typeof playing === "boolean") PC.playing = playing;
    if (dur) PC.dur = dur;
    pcPaint(); pcKick();
  }
  function pcNow() {
    if (PC.drag) return PC.pos;
    return PC.pos + (PC.playing ? (performance.now() - PC.t0) : 0);
  }
  var pcLastSec = -1;
  function pcPaint() {
    var d = PC.dur || AU.durMs || npDurMs || 0;
    var p = pcNow();
    var ratio = d > 0 ? Math.min(1, Math.max(0, p / d)) : 0;
    var pct = (ratio * 100).toFixed(4);
    var tf = "inset(0 " + (100 - ratio * 100).toFixed(4) + "% 0 0 round 4px)";
    var f = $("#pl-fill"), nf = $("#np-fill");
    if (f) f.style.clipPath = tf;
    if (nf) nf.style.clipPath = tf;
    var sec = Math.floor(p / 1000);
    if (sec !== pcLastSec) {                 // 时间文字每秒才写一次
      pcLastSec = sec;
      var c1 = $("#pl-cur"), c2 = $("#np-cur");
      if (c1) c1.textContent = fmt(p);
      if (c2) c2.textContent = fmt(p);
      if (d) { var u = $("#pl-dur"), nu = $("#np-dur"); if (u) u.textContent = fmt(d); if (nu) nu.textContent = fmt(d); }
    }
    try { npCharFill(); } catch (e15) { }
    var th = $("#np-thumb");
    if (th) th.style.left = pct + "%";
  }
  function pcKick() { if (!PC.raf && (PC.playing || PC.drag)) PC.raf = requestAnimationFrame(pcLoop); }
  function pcLoop(ts) {
    PC.raf = 0;
    if (!PC.lastPaint || !ts || ts - PC.lastPaint >= 15) { PC.lastPaint = ts || performance.now(); pcPaint(); }
    if (PC.playing || PC.drag) PC.raf = requestAnimationFrame(pcLoop);
  }
  function pcBuffer(el) {
    try {
      var d = el.duration || 0, n = el.buffered.length;
      if (!d || !n) return;
      var end = el.buffered.end(n - 1);
      var pct = Math.min(100, end / d * 100).toFixed(2) + "%";
      var b1 = $("#pl-buf"), b2 = $("#np-buf");
      if (b1) b1.style.width = pct;
      if (b2) b2.style.width = pct;
    } catch (e) { }
  }
  function auPushUI(pos) {
    var d = AU.durMs || npDurMs || 0;
    npPosMs = pos; if (d) npDurMs = d;
    PC.dur = d;
    pcSet(pos, AU.playing, d);        // 交给本地时钟做平滑推进
    npSync(pos);
  }
  function auPostState(force) {
    var now = Date.now();
    if (!force && now - AU.lastPost < 900) return;      // 限流：约 1 次/秒
    AU.lastPost = now;
    NE.post({ type: 'audio_state', playing: AU.playing, pos: Math.round(AU.posMs), dur: Math.round(AU.durMs) });
  }
  // 交叉淡化：新曲从 0 淡入、旧曲同步淡出，xfade 秒后关掉旧曲
  function auCrossfadeTo(url, startMs) {
    var old = auCur();
    var ni = 1 - AU.cur;
    var np2 = auMake(ni);
    var el = np2.el;
    el.src = url;
    el.volume = auMaster();
    auWire(np2);
    try { el.load(); } catch (e) { }
    if (startMs > 0) { try { el.currentTime = startMs / 1000; } catch (e) { } }
    if (AU.ctx && AU.ctx.state === 'suspended') { try { AU.ctx.resume(); } catch (e) { } }
    if (np2.gain) { np2.gain.gain.cancelScheduledValues(AU.ctx ? AU.ctx.currentTime : 0); np2.gain.gain.value = 0; }
    var pr = el.play();
    if (pr && pr.catch) pr.catch(function (e) { });
    var t = AU.ctx ? AU.ctx.currentTime : 0;
    var dur = AU.xfade;
    if (np2.gain) { np2.gain.gain.setValueAtTime(0, t); np2.gain.gain.linearRampToValueAtTime(1, t + dur); }
    if (old && old.gain) {
      old.gain.gain.cancelScheduledValues(t);
      old.gain.gain.setValueAtTime(old.gain.gain.value, t);
      old.gain.gain.linearRampToValueAtTime(0, t + dur);
    }
    AU.cur = ni;
    AU.fading = old ? 1 : 0;
    var oldP = old;
    setTimeout(function () {
      try { if (oldP && oldP !== AU.ps[AU.cur]) { oldP.el.pause(); oldP.el.removeAttribute('src'); } } catch (e) { }
      AU.fading = 0;
    }, Math.round(dur * 1000) + 150);
  }
  function auLoad(url, song, index, autoplay, startMs) {
    auCtx();
    AU.curIndex = (typeof index === 'number') ? index : -1;
    AU.posMs = startMs || 0;
    AU.playing = false;
    var canFade = AU.xfade > 0 && AU.ctx && AU.ps[AU.cur] && AU.ps[AU.cur].el.src && !AU.ps[AU.cur].el.paused;
    if (canFade) { auCrossfadeTo(url, startMs); return; }
    // 硬切：必要时先停掉另一个
    var other = AU.ps[1 - AU.cur];
    if (other) { try { other.el.pause(); other.el.removeAttribute('src'); if (other.gain) other.gain.gain.value = 1; } catch (e) { } }
    var p = auCur();
    try { p.el.pause(); } catch (e) { }
    p.el.src = url;
    p.el.volume = auMaster();
    auWire(p);
    if (p.gain) p.gain.gain.value = 1;
    try { p.el.load(); } catch (e) { }
    if (startMs > 0) { try { p.el.currentTime = startMs / 1000; } catch (e) { } }
    if (autoplay) auPlay();
  }
  function auPlay() {
    auCtx();
    if (AU.ctx && AU.ctx.state === 'suspended') { try { AU.ctx.resume(); } catch (e) { } }
    var p = auCur(); if (!p.el.src) return;
    var pr = p.el.play();
    if (pr && pr.catch) pr.catch(function (e) { try { NE.post({ type: 'log', msg: '[au] play rejected: ' + e.message }); } catch (e2) { } });
  }
  function auPause() {
    for (var i = 0; i < 2; i++) { if (AU.ps[i]) { try { AU.ps[i].el.pause(); } catch (e) { } } }
  }
  function auSeek(ms) {
    var p = auCur(); if (!p.el.src) return;
    try { p.el.currentTime = ms / 1000; AU.posMs = ms; auPushUI(ms); } catch (e) { }
  }
  function auSetVolume(v) {
    AU.vol = v;
    for (var i = 0; i < 2; i++) { if (AU.ps[i]) { try { AU.ps[i].el.volume = auMaster(); } catch (e) { } } }
  }

  NE.on('audio_load', function (d) { auLoad(d.url, d.song, d.index, d.autoplay !== false, d.startMs || 0); });
  NE.on('audio_cmd', function (d) {
    if (!d || !d.cmd) return;
    if (d.cmd === 'play') auPlay();
    else if (d.cmd === 'pause') auPause();
    else if (d.cmd === 'seek') auSeek(Number(d.value) || 0);
    else if (d.cmd === 'stop') { auPause(); auSeek(0); }
    else if (d.cmd === 'volume') auSetVolume(Number(d.value) || 0);
  });
  window.AU = AU;                    // 供频谱/交叉淡化使用
  window.AUFft = auFft;
  window.AU = AU;                    // 供频谱/交叉淡化使用
  window.AUFft = auFft;

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
  // 设置页改动后立刻在页内生效（否则要重开界面/重启才看得到）
function applyLiveSetting(key, val) {
  try {
    if (!appSettings) appSettings = {};
    if (!appSettings.app) appSettings.app = {};
    appSettings.app[key] = String(val);
    if (key === 'perf_gpu') { toast('GPU 加速将在重启软件后生效'); }
    if (key === 'scheme' || key === 'custom_accent') { try { if (key === 'scheme') appSettings.scheme = String(val); appSettings.app = appSettings.app || {}; if (key === 'custom_accent') appSettings.app.custom_accent = val; applyAccentOverrides(); } catch (e) { } }
    if (/^(perf_anim|perf_bg_blur|perf_play_anim|perf_vz_fps)/.test(key)) applyPerfAnim(appSettings);
    if (/^(perf_|lyric_|vz_)/.test(key)) npApplyLyricSettings(appSettings);
    else if (key === 'crossfade') AU.xfade = Math.max(0, Math.min(12, Number(val) || 0));
    else if (key === 'volume') applyVolume(Number(val) || 0, false);
    else if (key === 'playMode') applyMode(String(val), true);
    else if (key.indexOf('hk_') === 0) hotkeysFromSettings(appSettings);
  } catch (e) { }
}
function cfgGet(s, k, def) { var v = (s && s.app) ? s.app[k] : undefined; return (v === undefined || v === null || v === '') ? def : v; }
  function cfgBool(s, k, def) { var v = cfgGet(s, k, def); return String(v) !== 'false' && v !== false; }

// ---- 性能：动画总开关 / 分组开关 / 预设与自定义速率 ----
var ANIM_PRESETS = {
  smooth:  { ease: "cubic-bezier(.18,.77,.58,.99)", k: 1.00, name: "平滑" },
  sharp:   { ease: "cubic-bezier(.45,0,.07,1)",     k: 0.70, name: "急促" },
  gentle:  { ease: "cubic-bezier(.25,.46,.45,.94)", k: 1.35, name: "温和" },
  easeout: { ease: "cubic-bezier(.15,.6,.35,1)",    k: 0.85, name: "缓出" }
};
function applyPerfAnim(s) {
  try {
    var on = cfgBool(s, "perf_anim", true);
    var page = cfgBool(s, "perf_anim_page", true);
    var np = cfgBool(s, "perf_anim_np", true);
    var rip = cfgBool(s, "perf_anim_ripple", true);
    var pre = String(cfgGet(s, "perf_anim_ease", "smooth"));
    var isCustom = (pre === "custom");
    var base = ANIM_PRESETS[pre] || ANIM_PRESETS.smooth;
    var k = isCustom ? Math.max(0.3, Math.min(2.5, (Number(cfgGet(s, "perf_anim_speed", 100)) || 100) / 100)) : base.k;
    var ease = isCustom ? String(cfgGet(s, "perf_anim_curve", ANIM_PRESETS.smooth.ease)) : base.ease;
    var h = document.documentElement;
    h.classList.toggle("anim-off", !on);
    h.classList.toggle("no-anim-page", !on || !page);
    h.classList.toggle("no-anim-np", !on || !np);
    h.classList.toggle("no-anim-ripple", !on || !rip);
    h.classList.toggle("anim-tuned", on && (isCustom || pre !== "smooth"));
    h.style.setProperty("--anim-k", String(k));
    h.style.setProperty("--anim-ease", ease);
    h.style.setProperty("--nm-ripple-dur", Math.max(0.3, Math.min(1.0, 0.62 * k)).toFixed(2) + "s");
  } catch (e) { }
}
  function toast(t) { const el=$('#toast'); el.textContent=t; el.style.display='block'; setTimeout(()=>el.style.display='none',2000); }
  function loading() { view.innerHTML = '<div class="big-load">正在加载…</div>'; }

  function play(ns) { NE.post({ type:'play', song: ns }); setPlayer(ns); }
  function pop(el) { if(!el) return; el.classList.remove('fx-pop'); void el.offsetWidth; el.classList.add('fx-pop'); }
  function setPlaying(on) {
    var p = $('#player'); if(p) p.classList.toggle('playing', !!on);
    var pb = $('#pb-play'); if(pb) pb.innerHTML = on ? SVG.pause : SVG.play;
    npSetPlaying(on);   // 歌词页的播放/暂停按钮同步
  }
  // 「播放全部 + 下载全部」按钮组：下载全部需再点一次确认，避免误触批量下载
  // ---- 音质切换（dock 与歌词页共用一套逻辑）----
  var QUAL_LABELS = { standard: "标准", higher: "较高", high: "较高", exhigh: "极高", lossless: "无损", hires: "Hi-Res" };
  var QUAL_ORDER = ["standard", "exhigh", "lossless"];
  var qualCur = "exhigh";
  function qualSvg(v) {
    var t = QUAL_LABELS[v] || "标准";
    var w = t.length > 2 ? 42 : 27;
    return '<svg viewBox="0 0 ' + w + ' 18" width="' + w + '" height="18" aria-hidden="true">' +
      '<rect x="1" y="1" width="' + (w - 2) + '" height="16" rx="4.5" fill="none" stroke="currentColor" stroke-width="1.4" opacity=".85"/>' +
      '<text x="' + (w / 2) + '" y="13" text-anchor="middle" font-size="11" fill="currentColor">' + esc(t) + '</text></svg>';
  }
  function qualPaint() {
    var svg = qualSvg(qualCur);
    ["pl-qual", "np-qual"].forEach(function (id) { var b = document.getElementById(id); if (b) { b.innerHTML = svg; b.title = "音质：" + (QUAL_LABELS[qualCur] || qualCur); } });
  }
  function qualClose() { var m = document.getElementById("qual-menu"); if (m) m.classList.remove("show"); }
  function qualOpen(anchor) {
    var m = document.getElementById("qual-menu"); if (!m) return;
    m.innerHTML = "";
    QUAL_ORDER.forEach(function (v) {
      var it = el("div", "qual-item" + (v === qualCur ? " sel" : ""), "<span>" + esc(QUAL_LABELS[v] || v) + "</span>");
      it.onclick = function (e) {
        e.stopPropagation();
        NE.setSetting("quality", v);
        qualCur = v; qualPaint(); qualClose();
        toast("音质已切换：" + (QUAL_LABELS[v] || v) + "（下一首生效）");
      };
      m.appendChild(it);
    });
    m.classList.add("show");
    var r = anchor.getBoundingClientRect();
    var mw = m.offsetWidth, mh = m.offsetHeight;
    var left = Math.min(Math.max(8, r.left + r.width / 2 - mw / 2), Math.max(8, window.innerWidth - mw - 8));
    var top = r.top - mh - 10;
    if (top < 8) top = Math.min(r.bottom + 10, window.innerHeight - mh - 8);
    m.style.left = Math.round(left) + "px";
    m.style.top = Math.round(top) + "px";
  }
  function qualBind() {
    ["pl-qual", "np-qual"].forEach(function (id) {
      var b = document.getElementById(id); if (!b || b._qb) return;
      b._qb = 1;
      b.onclick = function (e) {
        e.stopPropagation();
        var m = document.getElementById("qual-menu");
        if (m && m.classList.contains("show")) { qualClose(); return; }
        qualOpen(b);
      };
    });
    qualPaint();
  }
  document.addEventListener("click", function () { qualClose(); });
  document.addEventListener("keydown", function (e) { if (e.key === "Escape") qualClose(); });
  window.addEventListener("resize", function () { qualClose(); });

  // ---- 设置项的中文名（内部键名 → 显示名）----
  var ZH_NAMES = {
    scheme: {
      "dark-blue": "深蓝", "dark-gray": "深灰", "dark-green": "深绿", "dark-orange": "深橙",
      "dark-purple": "深紫", "dark-red": "深红", "dark-pink": "深粉", "dark-rose-pine": "暗夜玫瑰松",
      "light-blue": "浅蓝", "light-gray": "浅灰", "light-green": "浅绿", "light-orange": "浅橙",
      "light-purple": "浅紫", "light-red": "浅红", "light-pink": "浅粉", "light-rose-pine": "浅色玫瑰松",
      "tokyo-night": "东京夜", "one-dark-blue": "One Dark 蓝", "one-dark-green": "One Dark 绿",
      "one-dark-cyan": "One Dark 青", "one-dark-red": "One Dark 红", "one-dark-pink": "One Dark 粉",
      "one-dark-yellow": "One Dark 黄", "one-dark-purple": "One Dark 紫",
      "osu-pink": "osu! 粉", "osu-purple": "osu! 紫", "osu-blue": "osu! 蓝", "osu-green": "osu! 绿",
      "osu-orange": "osu! 橙", "osu-yellow": "osu! 黄",
      "cyberpunk": "赛博朋克", "matrix": "黑客帝国", "dracula-mint": "德古拉薄荷", "cerulean": "天青",
      "discord": "Discord", "wechat": "微信", "tim": "TIM", "pure-black": "纯黑",
      "netease-default": "网易云默认", "dynamic-auto": "跟随封面（自动）"
    },
    language: { "zh_cn": "简体中文", "en_US": "English" },
    quality: { "standard": "标准", "higher": "较高", "high": "较高", "exhigh": "极高", "lossless": "无损", "hires": "Hi-Res" },
    playMode: { "order": "顺序播放", "list": "列表循环", "single": "单曲循环", "random": "随机播放" }
  };
  function zhOpt(key, v) { return (ZH_NAMES[key] && ZH_NAMES[key][v]) || v; }
  function zhOpts(key, arr) { return (arr || []).map(function (v) { return { v: v, t: zhOpt(key, v) }; }); }

  // ---- 自定义强调色（配色方案选「自定义」时叠加在主题变量之上）----
  function hexToRgbStr(h) {
    var m = /^#?([0-9a-f]{6})$/i.exec(String(h || "").trim());
    if (!m) return null;
    var v = m[1];
    return [parseInt(v.slice(0, 2), 16), parseInt(v.slice(2, 4), 16), parseInt(v.slice(4, 6), 16)];
  }
  function shade(rgb, k) { return "rgb(" + rgb.map(function (n) { return Math.round(n * k); }).join(",") + ")"; }
  function applyAccentOverrides() {
    try {
      var sch = String((appSettings && appSettings.scheme) || "");
    var onCustom = sch === "custom";
    var onDynamic = sch === "dynamic-auto";
    var on = onCustom || (onDynamic && !!dynamicAccent);
      var st = document.documentElement.style;
      var keys = ["--md-accent-color", "--md-accent-color-rgb", "--md-accent-color-secondary", "--md-accent-color-secondary-rgb",
                  "--md-accent-color-bg", "--md-accent-color-bg-rgb", "--md-accent-color-bg-darken", "--md-accent-color-bg-darken-rgb"];
      if (!on) {
        // 关键：不能 removeProperty —— 那会把主题刚写入的变量一起删掉，导致所有配色失效
        String(window._themeVars || "").split(";").forEach(function (pair) {
          var i = pair.indexOf(":"); if (i <= 0) return;
          var n = pair.slice(0, i).trim(), v = pair.slice(i + 1).trim();
          if (n && v) st.setProperty(n, v);
        });
        return;
      }
      var hex = onCustom ? String((appSettings && appSettings.custom_accent) || "#b5b9d6") : dynamicAccent;
      var rgb = hexToRgbStr(hex); if (!rgb) return;
      st.setProperty("--md-accent-color", hex);
      st.setProperty("--md-accent-color-rgb", rgb.join(","));
      st.setProperty("--md-accent-color-secondary", hex);
      st.setProperty("--md-accent-color-secondary-rgb", rgb.join(","));
      st.setProperty("--md-accent-color-bg", shade(rgb, 0.20));
      st.setProperty("--md-accent-color-bg-rgb", rgb.map(function (n) { return Math.round(n * 0.20); }).join(","));
      st.setProperty("--md-accent-color-bg-darken", shade(rgb, 0.13));
      st.setProperty("--md-accent-color-bg-darken-rgb", rgb.map(function (n) { return Math.round(n * 0.13); }).join(","));
    } catch (e) { }
  }
  // ---- 「跟随封面（自动）」：从当前封面采样主色，推导整套强调色 ----
  var coverColorCache = {};
  var dynamicAccent = null;
  function boostColor(r, g, b) {
    var mx = Math.max(r, g, b), mn = Math.min(r, g, b);
    var l = (mx + mn) / 2 / 255;
    var s = mx === mn ? 0 : (mx - mn) / (l > .5 ? (510 - mx - mn) : (mx + mn));
    s = Math.min(1, s * 1.45 + .12);                 // 提高饱和度，避免灰扑扑
    l = Math.min(.82, Math.max(.52, l * .95 + .18)); // 压到中亮度，深色底上更清楚
    function hue2rgb(p2, q2, t) { if (t < 0) t += 1; if (t > 1) t -= 1; if (t < 1/6) return p2 + (q2 - p2) * 6 * t; if (t < 1/2) return q2; if (t < 2/3) return p2 + (q2 - p2) * (2/3 - t) * 6; return p2; }
    var r2, g2, b2;
    if (s === 0) { r2 = g2 = b2 = l; }
    else {
      var q = l < .5 ? l * (1 + s) : l + s - l * s, p2 = 2 * l - q;
      var h = 0;
      var rr = r, gg = g, bb = b;                      // 统一用 0~255，别和归一化混用
      if (mx === r) h = (gg - bb) / (mx - mn);
      else if (mx === g) h = 2 + (bb - rr) / (mx - mn);
      else h = 4 + (rr - gg) / (mx - mn);
      h = (h / 6 + 1) % 1;
      r2 = hue2rgb(p2, q, h + 1/3); g2 = hue2rgb(p2, q, h); b2 = hue2rgb(p2, q, h - 1/3);
    }
    return "#" + [r2, g2, b2].map(function (n) { return ("0" + Math.round(n * 255).toString(16)).slice(-2); }).join("");
  }
  function pickCoverColor(pic) {
    if (!pic) return;
    var url = pic.replace(/^d+^/, "");
    var key = url.split("?")[0];
    if (coverColorCache[key]) { dynamicAccent = coverColorCache[key]; applyAccentOverrides(); return; }
    var img = new Image();
    img.crossOrigin = "anonymous";
    img.onload = function () {
      try {
        var n = 24, cv = document.createElement("canvas"); cv.width = n; cv.height = n;
        var g2 = cv.getContext("2d");
        g2.drawImage(img, 0, 0, n, n);
        var d = g2.getImageData(0, 0, n, n).data;
        var R = 0, G = 0, B = 0, W = 0;
        for (var i = 0; i < d.length; i += 4) {
          if (d[i + 3] < 200) continue;
          var r = d[i], gg = d[i + 1], b = d[i + 2];
          var lum = r * .299 + gg * .587 + b * .114;
          if (lum < 26 || lum > 238) continue;                 // 丢开纯黑纯白（通常是边框/文字）
          var mx = Math.max(r, gg, b), mn = Math.min(r, gg, b);
          var sat = mx === 0 ? 0 : (mx - mn) / mx;
          var w = .3 + sat * sat * 2.2 + (1 - Math.abs(lum - 128) / 128) * .4;
          R += r * w; G += gg * w; B += b * w; W += w;
        }
        if (!W) { R = d[0]; G = d[1]; B = d[2]; W = 1; }
        dynamicAccent = boostColor(R / W, G / W, B / W);
        coverColorCache[key] = dynamicAccent;
        applyAccentOverrides();
      } catch (e) { }
    };
    img.onerror = function () { };
    img.src = url + (url.indexOf("?") >= 0 ? "&" : "?") + "param=48y48";
  }
  function allBtns(songs) {
    var wrap = el("div", "action-row");
    var b1 = el("button", "action-btn", "播放全部");
    b1.onclick = function () { addQueue(songs.map(normSong), 0); };
    var b2 = el("button", "action-btn ghost", "下载全部");
    var armed = false, timer = null;
    b2.onclick = function () {
      var list = songs.map(normSong).filter(function (s) { return s && s.Id; });
      if (!list.length) { toast("没有可下载的歌曲"); return; }
      if (!armed) {
        armed = true; b2.classList.add("armed");
        b2.textContent = "确认下载 " + list.length + " 首？";
        clearTimeout(timer);
        timer = setTimeout(function () { armed = false; b2.classList.remove("armed"); b2.textContent = "下载全部"; }, 4000);
        return;
      }
      clearTimeout(timer); armed = false; b2.classList.remove("armed"); b2.textContent = "下载全部";
      list.forEach(function (s, i) { setTimeout(function () { NE.post({ type: "download", song: s }); }, i * 40); });
      toast("已加入下载队列：" + list.length + " 首");
    };
    wrap.appendChild(b1); wrap.appendChild(b2);
    return wrap;
  }
  // 只更新歌名/歌手/封面（恢复播放列表时用，不改播放状态）
  function showSongMeta(ns) {
    if(!ns) return;
    $('#pl-title').textContent = ns.Title;
    $('#pl-artist').textContent = ns.Artist;
    var c = $('#pl-cover'), wrap = $('#pl-cover-wrap');
    if(ns.Pic){ c.src = ns.Pic.replace(/\^\d+\^/,''); if(wrap) wrap.classList.add('has-cover'); try { if (String((appSettings && appSettings.scheme) || '') === 'dynamic-auto') pickCoverColor(ns.Pic); } catch (e7) { } }
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
  // 播放全部：把列表【追加】到现有播放队列后面（不替换），并从新加入的第一首开始播放
  function addQueue(list, start) {
    if (!list || !list.length) return;
    var add = list.map(normSong).filter(function (s) { return s && s.Id; });
    if (!add.length) return;
    var base = (queue && queue.length) ? queue.slice() : [];
    var merged = base.concat(add);
    NE.post({ type: 'play_list', songs: merged, index: base.length });
    toast('已添加到播放列表 +' + add.length + ' 首（共 ' + merged.length + ' 首）');
  }
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
    html.appendChild(allBtns(songs));
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
    html.appendChild(allBtns(songs));
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
    html.appendChild(allBtns(songs));
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
  try { document.getElementById('np').classList.add('hidden'); } catch (e) { }
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
    if (pic) {
      cover.src = pic;
      // 背景用 240px 缩略图放大 —— 比整屏 72px 高斯模糊便宜一个数量级
      var small = pic + (pic.indexOf('?') >= 0 ? '&' : '?') + 'param=240y240';
      bg.style.backgroundImage = 'url("' + small + '")';
    }
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
    fontSize: 22,
    vz: false, vzStyle: 'bars', vzStrength: 60, vzSens: 100
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
    // 性能开关
    lyStyles.bgBlur = cfgBool(s, 'perf_bg_blur', true);
    lyStyles.playAnim = cfgBool(s, 'perf_play_anim', true);
    lyStyles.vzFps = Math.max(10, Math.min(120, Number(cfgGet(s, 'perf_vz_fps', 33)) || 33));
    lyStyles.vz = cfgBool(s, 'vz_enabled', false);
    lyStyles.vzStyle = String(cfgGet(s, 'vz_style', 'bars'));
    lyStyles.vzStrength = Math.max(0, Math.min(100, Number(cfgGet(s, 'vz_strength', 60)) || 0));
    lyStyles.vzSens = Math.max(10, Math.min(200, Number(cfgGet(s, 'vz_sens', 100)) || 100));
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
    np.classList.toggle('no-bg-blur', !lyStyles.bgBlur);
    document.documentElement.classList.toggle('no-play-anim', !lyStyles.playAnim);
    VZ_FRAME_MS = Math.round(1000 / (lyStyles.vzFps || 33));
    np.style.setProperty('--ly-blur', (s.blurAmt / 100 * 5).toFixed(2) + 'px');
    np.style.setProperty('--ly-ease', LY_EASE[s.ease] || LY_EASE.smooth);
    np.style.setProperty('--ly-font-size', s.fontSize + 'px');   // 字号（行高与排版会跟着重算）
    npLayout();
    try { npCharFill(); } catch (e18) { }   // 打开/切行后立即画一次
    vzApply();                                                   // 背景音乐律动
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
  var npGeom = { h: [], fs: 0 };      // 行高/字号缓存（只在重新渲染歌词或字号变化时重算）
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
    var fs = npGeom.fs || 22;
    var space = fs * 1.25;
    var h = [], s = [], b = [], o = [], base = [];
    var needMeasure = npGeom.h.length !== nodes.length || npGeom.fs === 0;
    if (needMeasure) {
      fs = parseFloat(getComputedStyle(nodes[cur]).fontSize) || 22;
      npGeom.fs = fs; space = fs * 1.25;
      npGeom.h = [];
      for (var i0 = 0; i0 < nodes.length; i0++) npGeom.h[i0] = nodes[i0].offsetHeight || fs * 1.5;
    }
    for (var i = 0; i < nodes.length; i++) h[i] = npGeom.h[i];
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
      // 只保留当前行附近的行参与绘制（73 行全画会明显吃 CPU/GPU）
      var near = Math.abs(off2) <= 10;
      if (!near) { n.style.display = 'none'; continue; }
      if (n.style.display === 'none') n.style.display = '';
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
      // 只给当前行附近的行加 filter（远处本来就透明看不见，还给 70 个元素加模糊层会很贵）
      n.style.filter = (b[i] > 0.05 && Math.abs(off2) <= 4) ? 'blur(' + b[i].toFixed(2) + 'px)' : '';
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
    npGeom.h = []; npGeom.fs = 0;      // 重建歌词 → 行高缓存作废
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

  // ---- 逐字填充（KTV 式）：没有逐字时间戳，就在行内按字符数均匀分配 ----
  var npFillCache = { line: -1, n: 0, sung: -1 };
  // ---- 逐字歌词（yrc）：解析出每个字的开始时间与时长 ----
  var yrcMap = {};                       // 行开始时间(ms) → [{ch,t,d}]
  function parseYrc(txt) {
    var map = {};
    try {
      String(txt || "").split("\n").forEach(function (raw) {
        var s = raw.trim(); if (!s || s.charAt(0) !== "[") return;
        var m = /^\[(\d+),(\d+)\]/.exec(s); if (!m) return;
        var lt = parseInt(m[1], 10), rest = s.slice(m[0].length), chars = [], re = /\((\d+),(\d+),\d+\)([^()]*)/g, mm;
        while ((mm = re.exec(rest))) { var t = mm[3]; if (!t) continue; chars.push({ ch: t, t: parseInt(mm[1], 10), d: parseInt(mm[2], 10) }); }
        if (chars.length) map[lt] = chars;
      });
    } catch (e) { }
    return map;
  }
  // 把 yrc 的字（可能是词/音节）摊平成"每个显示字符对应的时间"，与歌词行按顺序对齐
  function alignCharTimes(spans, units) {
    try {
      var flat = [];
      units.forEach(function (u) { var s = String(u.ch || ""); for (var i = 0; i < s.length; i++) flat.push({ c: s.charAt(i), t: u.t, d: u.d }); });
      var out = [], fi = 0;
      for (var i = 0; i < spans.length; i++) {
        var c = spans[i].textContent || "";
        if (c === " " || c === "\u3000") { out.push(null); continue; }
        if (fi < flat.length && flat[fi].c === c) { out.push(flat[fi]); fi++; continue; }
        return null;                       // 对不上就放弃，退回均匀分配
      }
      return out;
    } catch (e) { return null; }
  }
  function npCharFill() {
    try {
      if (!npOpen || !npLines.length || !lyStyles.charAnim) return;
      var ln = document.querySelector(".np-line.on"); if (!ln) return;
      if (npFillCache.line !== npIndex) {
        npFillCache.line = npIndex;
        npFillCache.n = ln.querySelectorAll(".ly-ch").length;
        npFillCache.sung = -1; npFillCache.times = null;
      }
      var n = npFillCache.n; if (!n) return;
      var cur = npLines[npIndex]; if (!cur) return;
      var t0 = cur.t;
      var t1 = (npLines[npIndex + 1] && npLines[npIndex + 1].t > t0) ? npLines[npIndex + 1].t : (t0 + (cur.d || 4000));
      var pos = (typeof PC !== "undefined") ? pcNow() : npPosMs;
      var spans = ln.querySelectorAll(".ly-ch");
      var units = yrcMap[t0];
      if (units && !npFillCache.times) npFillCache.times = alignCharTimes(spans, units);
      var times = units ? npFillCache.times : null;
      var sung = 0;
      if (times && times.length === spans.length) {
        for (var q = 0; q < times.length; q++) {
          var e = times[q]; if (!e) { sung = q + 1; continue; }
          if (pos >= e.t + e.d) sung = q + 1;              // 这个字已经唱完
          else break;
        }
      } else {
        var r = (pos - t0) / Math.max(1, t1 - t0);
        r = Math.max(0, Math.min(1, r));
        sung = Math.floor(r * n + 1e-6);                    // 没有逐字数据时按行内均匀分配
      }
      if (sung === npFillCache.sung) return;
      for (var i2 = 0; i2 < spans.length; i2++) {
        var on = i2 < sung;
        if (on !== spans[i2].classList.contains("sung")) spans[i2].classList.toggle("sung", on);
      }
      npFillCache.sung = sung;
    } catch (e) { }
  }
  function npSync(pos) {
    if (!npOpen || !npLines.length) return;
    var i = -1;
    for (var k = 0; k < npLines.length; k++) { if (npLines[k].t <= pos) i = k; else break; }
    if (i === npIndex) return;
    npIndex = i;
    npFillCache.line = -1; npFillCache.times = null;
    npLayout();
  }
  async function openNowPlaying() {
    var np = npEl('np'); if (!np) return;
    var wasOpen = npOpen;
    npOpen = true;
    np.classList.remove('hidden');          // 恢复渲染
    void np.offsetWidth;                    // 关键：强制回流一帧，让初始态生效，否则 show 的过渡不会播
    np.classList.add('show'); np.setAttribute('aria-hidden', 'false');
    if (lyStyles.vz) setTimeout(function () { vzApply(); }, 60);
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
    try { yrcMap = parseYrc(r && r.yrc && r.yrc.lyric); npFillCache.line = -1; npFillCache.times = null; } catch (e20) { }
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
      if (e.key === 'Backspace') { stopRecord(true, HK_OFF); return; }   // Backspace = 关闭该快捷键
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
      var off = (name === HK_OFF);
      hotkeys[rec.key] = off ? '' : name;
      NE.setSetting(rec.key, off ? HK_OFF : name);
      rec.btn.textContent = off ? '已关闭' : name;
      rec.btn.classList.toggle('off', off);
    } else {
      var cur = hotkeys[rec.key];
      rec.btn.textContent = cur ? cur : '已关闭';
      rec.btn.classList.toggle('off', !cur);
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
    // ---- 背景特效 ----
    box.appendChild(el('div', 'nps-title', '背景特效'));
    addSwitch('音乐律动（频谱）', 'vz_enabled', 'vz');
    addSelect('律动样式', 'vz_style', 'vzStyle', [
      { v: 'bars', t: '柱状' }, { v: 'ring', t: '环形（绕封面）' }, { v: 'wave', t: '波形' }
    ]);
    addRange('律动强度', 'vz_strength', 'vzStrength', 0, 100);
    addRange('灵敏度', 'vz_sens', 'vzSens', 20, 200);
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
    try { setTimeout(function () { if (!npOpen) document.getElementById('np').classList.add('hidden'); }, 420); } catch (e) { }
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
    function sw(label, key, val) { var r = el('div','set-row'); r.appendChild(el('label','',label)); var t = el('div','set-switch'+(val?' on':'')); t.onclick = function(){ var on=!t.classList.contains('on'); t.classList.toggle('on',on); NE.setSetting(key, on?'true':'false'); applyLiveSetting(key, on?'true':'false'); applyLiveSetting(key, on?'true':'false'); }; r.appendChild(t); return r; }
    // 原生 <select> 的弹层在 WebView2 里定位会飘，这里统一用自定义下拉
    function sel(label, key, val, opts) { return custSel(label, key, val, opts); }
    function txt(label, key, val) { var r = el('div','set-row'); r.appendChild(el('label','',label)); var i=el('input'); i.type='text'; i.value=val||''; i.onchange=function(){ NE.setSetting(key, i.value); applyLiveSetting(key, i.value); }; r.appendChild(i); return r; }
    // 自定义范围的滑条（用于动画速率这类非 0-100 的项）
    function rng2(label, key, val, min, max, suffix) {
      suffix = suffix || '';
      var r = el('div','set-row'); var lb = el('label','',label+' ('+val+suffix+')'); r.appendChild(lb);
      var i2 = el('input'); i2.type='range'; i2.min=min; i2.max=max; i2.step=5; i2.value=val;
      i2.oninput = function(){ lb.textContent = label+' ('+i2.value+suffix+')'; NE.setSetting(key, i2.value); applyLiveSetting(key, i2.value); };
      r.appendChild(i2); return r;
    }
    function rng(label, key, val) { var r = el('div','set-row'); var lb=el('label','',label+' ('+(val||80)+')'); r.appendChild(lb); var i=el('input'); i.type='range'; i.min=0; i.max=100; i.value=val||80; i.oninput=function(){ lb.textContent=label+' ('+i.value+')'; NE.setSetting(key, i.value); applyLiveSetting(key, i.value); }; r.appendChild(i); return r; }
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
          else { applyLiveSetting(key, valOf(o)); }
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
      i.oninput = function(){ lb.textContent = label+' ('+i.value+')'; NE.setSetting(key, i.value); applyLiveSetting(key, i.value); var p={}; p[fk]=Number(i.value); fxApply(p); };
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

    function colorRow() {
      var rr = el('div','set-row');
      rr.appendChild(el('label','','自定义强调色（配色方案选「自定义」时生效）'));
      var ii = el('input'); ii.type = 'color';
      ii.value = (s.app && s.app.custom_accent) || '#b5b9d6';
      ii.onchange = function () {
        NE.setSetting('custom_accent', ii.value);
        if (!appSettings) appSettings = {};
        appSettings.app = appSettings.app || {};
        appSettings.app.custom_accent = ii.value;
        applyAccentOverrides();
        toast('自定义强调色已应用');
      };
      rr.appendChild(ii); return rr;
    }
    html.appendChild(group('主题 / 外观', [ custSel('配色方案','scheme', s.scheme, zhOpts('scheme', s.schemes||[]).concat([{ v:'custom', t:'自定义…' }])), sw('Mica 背景','mica', s.mica), colorRow(), sw('关闭按钮最小化到托盘','closeToTray', s.closeToTray) ]));
    html.appendChild(fxGroup(s));
    html.appendChild(group('下载', [ txt('默认下载目录','downloadDir', s.downloadDir), sel('音质','quality', s.quality, zhOpts('quality', ['standard','exhigh','lossless'])) ]));
    function rngXfade() {
      var r = el('div','set-row'); var lb = el('label','','歌曲切换淡化 (秒)');
      var cur = Math.max(0, Math.min(12, Number(s.crossfade || 0) || 0));
      lb.textContent = '歌曲切换淡化 (' + cur + ')';
      r.appendChild(lb);
      var i = el('input'); i.type='range'; i.min=0; i.max=12; i.step=1; i.value=cur;
      i.oninput = function(){ lb.textContent = '歌曲切换淡化 (' + i.value + ')'; AU.xfade = Number(i.value); };
      i.onchange = function(){ NE.setSetting('crossfade', i.value); };
      r.appendChild(i); return r;
    }
    html.appendChild(group('播放', [ rng('默认音量','volume', s.volume), sel('播放模式','playMode', s.playMode, zhOpts('playMode', ['order','list','single','random'])), rngXfade() ]));
    // ---- 快捷键（可自定义）----
    function hotkeyGroup() {
      var g = el('div','set-group');
      g.appendChild(el('h3','','快捷键'));
      Object.keys(HK_LABELS).forEach(function (key) {
        var r = el('div','set-row');
        r.appendChild(el('label','', HK_LABELS[key]));
        var b = el('button','hk-btn' + (hotkeys[key] ? '' : ' off'), hotkeys[key] || '已关闭');
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
          b.classList.remove('off');
        };
        r.appendChild(b); r.appendChild(rst);
        g.appendChild(r);
      });
      g.appendChild(el('p','muted','点按键框后按下组合键即可重新绑定；录制时按 Backspace = 关闭这一项，Esc = 取消录制，右侧「重置」恢复默认。'));
      return g;
    }
    html.appendChild(hotkeyGroup());
    html.appendChild(group('性能', [
      sw('GPU 加速（硬件渲染，改动需重启）','perf_gpu', cfgBool(s, 'perf_gpu', true)),
      sw('界面动画总开关','perf_anim', cfgBool(s, 'perf_anim', true)),
      sel('动画速率','perf_anim_ease', String(cfgGet(s, 'perf_anim_ease', 'smooth')), [
        { v: 'smooth', t: '平滑（默认）' }, { v: 'sharp', t: '急促' }, { v: 'gentle', t: '温和' },
        { v: 'easeout', t: '缓出' }, { v: 'custom', t: '自定义…' }
      ]),
      rng2('自定义速率','perf_anim_speed', Number(cfgGet(s, 'perf_anim_speed', 100)) || 100, 30, 250, '%'),
      sel('自定义曲线','perf_anim_curve', String(cfgGet(s, 'perf_anim_curve', 'cubic-bezier(.18,.77,.58,.99)')), [
        { v: 'cubic-bezier(.18,.77,.58,.99)', t: '平滑' }, { v: 'cubic-bezier(.45,0,.07,1)', t: '急促' },
        { v: 'cubic-bezier(.25,.46,.45,.94)', t: '温和' }, { v: 'cubic-bezier(.15,.6,.35,1)', t: '缓出' },
        { v: 'linear', t: '匀速' }
      ]),
      sw('页面 / 列表过渡','perf_anim_page', cfgBool(s, 'perf_anim_page', true)),
      sw('歌词页动画（进入·换行滑动）','perf_anim_np', cfgBool(s, 'perf_anim_np', true)),
      sw('配色切换水波纹','perf_anim_ripple', cfgBool(s, 'perf_anim_ripple', true)),
      sw('歌词页背景模糊','perf_bg_blur', cfgBool(s, 'perf_bg_blur', true)),
      sw('播放态动画（呼吸 / 封面浮动）','perf_play_anim', cfgBool(s, 'perf_play_anim', true)),
      sel('频谱帧率','perf_vz_fps', String(cfgGet(s, 'perf_vz_fps', 33)), [
        { v: '15', t: '15 fps（最省）' }, { v: '24', t: '24 fps' }, { v: '33', t: '33 fps（默认）' }, { v: '60', t: '60 fps（最顺）' }
      ])
    ]));
    html.appendChild(group('网络 / 代理', [ txt('代理地址','proxy', s.proxy) ]));
    html.appendChild(group('语言', [ sel('界面语言','language', s.language, zhOpts('language', ['zh_cn','en_US'])) ]));
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
    applyAccentOverrides();     // 主题变量之后再叠加自定义强调色，否则会被覆盖
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
        var idx = plCtxIndex;              // 先取下标！hideQueueMenu 会把 plCtxIndex 置 -1
        if (idx < 0 || !queue[idx]) { hideQueueMenu(); return; }
        var ns = queue[idx];
        hideQueueMenu();
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
    try { if (window.AU) { AU.vol = v; if (typeof auSetVolume === 'function') auSetVolume(v); } } catch (e) { }
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
    function npSeekRatio(clientX, barEl) {
      var prog = barEl || document.getElementById('np-prog'); if (!prog) return -1;
      var r = prog.getBoundingClientRect();
      if (!r.width) return -1;
      return Math.min(1, Math.max(0, (clientX - r.left) / r.width));
    }
    function npApplySeek(clientX, commit, barEl) {
      var ratio = npSeekRatio(clientX, barEl); if (ratio < 0) return;
      PC.drag = !commit; PC.pos = 0;
      var d = npDurMs || (queue[playingIndex] && queue[playingIndex].Duration) || 0;
      if (d <= 0) { if (commit) toast('时长还没加载出来，稍后再试'); return; }
      var ms = Math.round(ratio * d);
      PC.pos = ms; PC.t0 = performance.now();
      var tip = document.getElementById('np-tip');
      if (tip) { tip.textContent = fmt(ms); tip.style.left = (ratio * 100) + '%'; }
      var f = document.getElementById('np-fill');
      if (f) f.style.clipPath = 'inset(0 ' + (100 - ratio * 100).toFixed(4) + '% 0 0 round 4px)';
      var c = document.getElementById('np-cur');
      if (c) c.textContent = fmt(ms);
      if (commit) { PC.drag = false; NE.post({ type: 'seek', pos: ms }); npPosMs = ms; pcKick(); }
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
    if (dockBar) {
      var dockDrag = false;
      dockBar.onmousedown = function (e) { e.preventDefault(); dockDrag = true; npApplySeek(e.clientX, false, dockBar); };
      document.addEventListener('mousemove', function (e) { if (dockDrag) npApplySeek(e.clientX, false, dockBar); });
      document.addEventListener('mouseup', function (e) { if (!dockDrag) return; dockDrag = false; npApplySeek(e.clientX, true, dockBar); });
    }
  })();

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
      applyPerfAnim(s);                                        // 性能：动画开关与速率
      if (s && s.quality) { qualCur = String(s.quality); }
      qualBind();
      hotkeysFromSettings(s);                                  // 快捷键绑定
      AU.xfade = Math.max(0, Math.min(12, Number(s.crossfade || 0) || 0));   // 交叉淡化秒数
      AU.vol = Number(s.volume != null ? s.volume : 70) || 0;
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


    var bodyEl = document.getElementById("notice-body");

    // ---- 极简 Markdown 渲染：标题 / 加粗 / 斜体 / 行内代码 / 代码块 / 有序无序列表 / 分隔线 / 链接 ----
    function mdToHtml(src) {
      var lines = String(src || "").replace(/\r\n?/g, "\n").split("\n");
      var out = [], para = [], list = "", code = false, buf = [];
      function inl(t) {
        t = esc(t);
        t = t.replace(/`([^`]+)`/g, "<code>$1</code>");
        t = t.replace(/\*\*([^*]+)\*\*/g, "<b>$1</b>");
        t = t.replace(/(^|[^*])\*([^*\n]+)\*/g, "$1<i>$2</i>");
        t = t.replace(/\[([^\]]+)\]\((https?:[^)\s]+)\)/g, "<a href=\"$2\" target=\"_blank\" rel=\"noreferrer\">$1</a>");
        return t;
      }
      function fp() { if (para.length) { out.push("<p>" + inl(para.join(" ")) + "</p>"); para = []; } }
      function fl() { if (list) { out.push("</" + list + ">"); list = ""; } }
      lines.forEach(function (raw) {
        var s = raw.replace(/\s+$/, ""), t = s.trim();
        if (/^```/.test(t)) {
          if (code) { out.push("<pre><code>" + esc(buf.join("\n")) + "</code></pre>"); buf = []; code = false; }
          else { fp(); fl(); code = true; }
          return;
        }
        if (code) { buf.push(s); return; }
        if (!t) { fp(); fl(); return; }
        if (/^(-{3,}|\*{3,}|_{3,})$/.test(t)) { fp(); fl(); out.push("<hr>"); return; }
        var m;
        if ((m = /^(#{1,4})\s+(.*)$/.exec(t))) {
          fp(); fl();
          var lv = m[1].length + 2;                       // 用 h3~h6，避免和弹窗自己的标题抢层级
          out.push("<h" + lv + ">" + inl(m[2]) + "</h" + lv + ">");
          return;
        }
        if ((m = /^[-*+]\s+(.*)$/.exec(t))) { fp(); if (list !== "ul") { fl(); out.push("<ul>"); list = "ul"; } out.push("<li>" + inl(m[1]) + "</li>"); return; }
        if ((m = /^\d+[.)]\s+(.*)$/.exec(t))) { fp(); if (list !== "ol") { fl(); out.push("<ol>"); list = "ol"; } out.push("<li>" + inl(m[1]) + "</li>"); return; }
        fl(); para.push(t);
      });
      fp(); fl();
      if (code && buf.length) out.push("<pre><code>" + esc(buf.join("\n")) + "</code></pre>");
      return out.join("");
    }
    // 请求更新日志（从 GitHub Release 取，不在程序里写死）
    function loadNotes(ver) {
      if (!bodyEl) return;
      bodyEl.innerHTML = '<p class="notice-loading">正在获取本次更新日志…</p>';
      try { NE.post({ type: "release_notes", version: ver }); } catch (e) { }
    }

    // C# 检测到 config 里的版本比当前版本旧 → 弹更新公告
    NE.on("update_notice", function (d) {
      if (!d) return;
      var from = d.from ? d.from : "首次运行";
      pending = d.to;
      closed = false;
      if (leadEl) leadEl.innerHTML = "软件已更新到 <b>netHEmusic " + esc(d.to) + "</b>";
      if (verEl) verEl.textContent = "版本：" + from + " → " + d.to;
      mask.classList.add("show");
      mask.setAttribute("aria-hidden", "false");
      loadNotes(d.to);
    });

    // C# 取到 Release 正文 → 按 Markdown 渲染
    NE.on("release_notes", function (d) {
      if (!bodyEl || !d) return;
      if (d.error) { bodyEl.innerHTML = '<p class="notice-loading">更新日志获取失败：' + esc(d.error) + "</p>"; return; }
      var html = mdToHtml(d.body || "");
      bodyEl.innerHTML = html || '<p class="notice-loading">本次没有更新说明。</p>';
    });
  })();

  // 启动：拉一次发现页并渲染首页
  window.addEventListener('load', function () {
    try { NE.post({ type: 'discover' }); go('home'); } catch (e) { }
    // TEMP-NPCHK
    setTimeout(function () { try { var n = document.getElementById('np'); NE.post({ type: 'log', msg: '[npchk] 打开前 cls=' + n.className + ' h=' + n.offsetHeight }); openNowPlaying(); setTimeout(function () { NE.post({ type: 'log', msg: '[npchk] 打开后 cls=' + n.className + ' h=' + n.offsetHeight + ' npOpen=' + npOpen }); }, 900); } catch (e12) { } }, 5000);
    setTimeout(function () { try { var n = document.getElementById('np'); closeNowPlaying(); setTimeout(function () { NE.post({ type: 'log', msg: '[npchk] 关闭后 cls=' + n.className + ' h=' + n.offsetHeight }); }, 700); } catch (e13) { } }, 8000);
  });

})();
