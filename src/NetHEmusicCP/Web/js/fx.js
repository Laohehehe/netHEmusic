// fx.js —— 鼠标特效：光标拖尾、点击波纹 + 火花、按钮抖动
// 通过 --md-accent-color-rgb 跟随当前配色方案；支持 prefers-reduced-motion 与设置项 uiEffects
(function () {
  // 把 JS 运行时错误写进 C# 日志，便于排查（WebView2 里没有控制台可看）
  function reportError(msg) { try { if (window.NE && NE.post) NE.post({ type: 'log', msg: '[fx] ' + msg }); } catch (e) { } }
  window.addEventListener('error', function (e) { reportError('ERROR ' + e.message + ' @' + (e.filename || '') + ':' + e.lineno); });
  window.addEventListener('unhandledrejection', function (e) { reportError('REJECT ' + (e.reason && e.reason.message ? e.reason.message : e.reason)); });

  var reduce = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  var SHAKE_SEL = 'button, .row-btn, .ic-btn, .cs-value, .cs-item, .set-switch, #nav a, a.nav-bottom, .action-btn, .hot-item, .song-row, .pl-cover';

  var enabled = !reduce;
  var cv = document.createElement('canvas');
  cv.id = 'fx-canvas';
  document.body.appendChild(cv);
  var ctx = cv.getContext('2d');
  var dpr = Math.max(1, Math.min(2, window.devicePixelRatio || 1));

  function resize() {
    cv.width = Math.floor(window.innerWidth * dpr);
    cv.height = Math.floor(window.innerHeight * dpr);
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  }
  resize();
  window.addEventListener('resize', resize);

  function accentRgb() {
    var v = getComputedStyle(document.documentElement).getPropertyValue('--md-accent-color-rgb').trim();
    return v || '226,53,53';
  }

  var TRAIL_MS = 540, TRAIL_MAX = 30, RING_MS = 560, SPARK_MS = 660;
  var trail = [], rings = [], sparks = [], mouse = { x: -999, y: -999 }, last = -999;
  var running = false;
  var now = function () { return performance.now(); };

  function kick() { if (!running) { running = true; requestAnimationFrame(frame); } }

  function frame() {
    var t = now();
    // 拖尾点：按时间淘汰
    while (trail.length && t - trail[0].t > TRAIL_MS) trail.shift();
    if (trail.length > TRAIL_MAX) trail.splice(0, trail.length - TRAIL_MAX);
    rings = rings.filter(function (r) { return t - r.t < RING_MS; });
    sparks = sparks.filter(function (s) { return t - s.t < SPARK_MS; });

    ctx.clearRect(0, 0, window.innerWidth, window.innerHeight);
    var rgb = accentRgb();

    // ---- 拖尾（渐细的光带 + 头部光晕）----
    if (trail.length > 1) {
      ctx.lineCap = 'round'; ctx.lineJoin = 'round';
      // 外层辉光（一圈更宽、更淡的光带）
      for (var g0 = 1; g0 < trail.length; g0++) {
        var pg = g0 / (trail.length - 1);
        var ag = Math.pow(pg, 2) * 0.22 * Math.max(0, 1 - (t - trail[g0].t) / TRAIL_MS);
        if (ag <= 0.008) continue;
        ctx.strokeStyle = 'rgba(' + rgb + ',' + ag.toFixed(3) + ')';
        ctx.lineWidth = 3 + 22 * pg;
        ctx.beginPath();
        ctx.moveTo(trail[g0 - 1].x, trail[g0 - 1].y);
        ctx.lineTo(trail[g0].x, trail[g0].y);
        ctx.stroke();
      }
      // 主体光带（渐细、渐亮）
      for (var i = 1; i < trail.length; i++) {
        var p = i / (trail.length - 1);
        var a = Math.pow(p, 1.35) * 0.95 * Math.max(0, 1 - (t - trail[i].t) / TRAIL_MS);
        if (a <= 0.01) continue;
        ctx.strokeStyle = 'rgba(' + rgb + ',' + a.toFixed(3) + ')';
        ctx.lineWidth = 1.2 + 9 * p;
        ctx.beginPath();
        ctx.moveTo(trail[i - 1].x, trail[i - 1].y);
        ctx.lineTo(trail[i].x, trail[i].y);
        ctx.stroke();
      }
    }
    if (mouse.x > -900) {
      var fade = Math.max(0, 1 - (t - last) / 420);
      if (fade > 0.02) {
        var g = ctx.createRadialGradient(mouse.x, mouse.y, 0, mouse.x, mouse.y, 36);
        g.addColorStop(0, 'rgba(' + rgb + ',' + (0.48 * fade).toFixed(3) + ')');
        g.addColorStop(1, 'rgba(' + rgb + ',0)');
        ctx.fillStyle = g;
        ctx.beginPath(); ctx.arc(mouse.x, mouse.y, 36, 0, Math.PI * 2); ctx.fill();
        ctx.fillStyle = 'rgba(255,255,255,' + (0.85 * fade).toFixed(3) + ')';
        ctx.beginPath(); ctx.arc(mouse.x, mouse.y, 2.8, 0, Math.PI * 2); ctx.fill();
      }
    }

    // ---- 点击波纹 ----
    for (var k = 0; k < rings.length; k++) {
      var r = rings[k], e = (t - r.t) / RING_MS;
      var rr = 6 + 78 * e, alpha = Math.pow(1 - e, 1.6);
      // 扩散光晕
      var g2 = ctx.createRadialGradient(r.x, r.y, 0, r.x, r.y, rr);
      g2.addColorStop(0, 'rgba(' + rgb + ',' + (alpha * 0.26).toFixed(3) + ')');
      g2.addColorStop(1, 'rgba(' + rgb + ',0)');
      ctx.fillStyle = g2;
      ctx.beginPath(); ctx.arc(r.x, r.y, rr, 0, Math.PI * 2); ctx.fill();
      // 外圈两道环
      ctx.strokeStyle = 'rgba(' + rgb + ',' + (alpha * 0.95).toFixed(3) + ')';
      ctx.lineWidth = 0.8 + 5 * (1 - e);
      ctx.beginPath(); ctx.arc(r.x, r.y, rr, 0, Math.PI * 2); ctx.stroke();
      ctx.strokeStyle = 'rgba(255,255,255,' + (alpha * 0.5).toFixed(3) + ')';
      ctx.lineWidth = 1 + 2.5 * (1 - e);
      ctx.beginPath(); ctx.arc(r.x, r.y, rr * 0.62, 0, Math.PI * 2); ctx.stroke();
      // 中心闪光
      var fl = Math.max(0, 1 - e * 3.2);
      if (fl > 0.01) {
        var g3 = ctx.createRadialGradient(r.x, r.y, 0, r.x, r.y, 22);
        g3.addColorStop(0, 'rgba(255,255,255,' + (0.55 * fl).toFixed(3) + ')');
        g3.addColorStop(1, 'rgba(255,255,255,0)');
        ctx.fillStyle = g3;
        ctx.beginPath(); ctx.arc(r.x, r.y, 22, 0, Math.PI * 2); ctx.fill();
      }
    }

    // ---- 点击火花 ----
    for (var j = 0; j < sparks.length; j++) {
      var s = sparks[j], es = (t - s.t) / SPARK_MS;
      var x = s.x + s.vx * es * 60, y = s.y + s.vy * es * 60 + 90 * es * es;
      ctx.fillStyle = 'rgba(' + rgb + ',' + (Math.pow(1 - es, 1.5) * 0.95).toFixed(3) + ')';
      ctx.beginPath(); ctx.arc(x, y, 2.6 * (1 - es) + 0.6, 0, Math.PI * 2); ctx.fill();
    }

    if (trail.length > 1 || rings.length || sparks.length || (mouse.x > -900 && t - last < 420)) requestAnimationFrame(frame);
    else { ctx.clearRect(0, 0, window.innerWidth, window.innerHeight); running = false; }
  }

  document.addEventListener('pointermove', function (e) {
    if (!enabled) return;
    mouse.x = e.clientX; mouse.y = e.clientY; last = now();
    trail.push({ x: e.clientX, y: e.clientY, t: last });
    kick();
  }, { passive: true });


  document.addEventListener('pointerdown', function (e) {
    if (!enabled) return;
    var t = now();
    rings.push({ x: e.clientX, y: e.clientY, t: t });
    if (rings.length > 6) rings.shift();
    for (var i = 0; i < 14; i++) {
      var ang = Math.random() * Math.PI * 2, sp = 0.6 + Math.random() * 1.9;
      sparks.push({ x: e.clientX, y: e.clientY, vx: Math.cos(ang) * sp, vy: Math.sin(ang) * sp - 0.4, t: t });
    }
    if (sparks.length > 80) sparks.splice(0, sparks.length - 80);
    // 按钮抖动 + 强调色按下光圈
    var el = e.target && e.target.closest ? e.target.closest(SHAKE_SEL) : null;
    if (el) {
      el.classList.remove('fx-shake');
      void el.offsetWidth;
      el.classList.add('fx-shake');
      el.classList.add('fx-press');
      setTimeout(function () { el.classList.remove('fx-shake'); el.classList.remove('fx-press'); }, 460);
    }
    kick();
  }, true);

  function setEnabled(on) {
    enabled = !!on && !reduce;
    document.documentElement.classList.toggle('no-fx', !enabled);
    if (!enabled) { trail = []; rings = []; sparks = []; ctx.clearRect(0, 0, window.innerWidth, window.innerHeight); running = false; }
  }

  // 读取设置项（默认开）
  try {
    if (window.NE && NE.getSettings) NE.getSettings().then(function (s) { setEnabled(s.uiEffects !== false && String(s.uiEffects) !== 'false'); }).catch(function () { });
  } catch (e) { }
  document.documentElement.classList.toggle('no-fx', !enabled);

  window.FX = { setEnabled: setEnabled, isEnabled: function () { return enabled; } };
})();
