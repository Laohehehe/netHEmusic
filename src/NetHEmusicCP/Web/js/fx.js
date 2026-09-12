// fx.js —— 鼠标特效：光标拖尾、点击波纹/火花、按钮抖动
// 全部子项可在 设置 → 鼠标特效 里调整（含自定义颜色）；支持 prefers-reduced-motion
(function () {
  function reportError(msg) { try { if (window.NE && NE.post) NE.post({ type: 'log', msg: '[fx] ' + msg }); } catch (e) { } }
  window.addEventListener('error', function (e) { reportError('ERROR ' + e.message + ' @' + (e.filename || '') + ':' + e.lineno); });
  window.addEventListener('unhandledrejection', function (e) { reportError('REJECT ' + (e.reason && e.reason.message ? e.reason.message : e.reason)); });

  var reduce = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  var SHAKE_SEL = 'button, .row-btn, .ic-btn, .cs-value, .cs-item, .set-switch, #nav a, a.nav-bottom, .action-btn, .hot-item, .song-row, .pl-cover';

  var cfg = {
    enabled: true, trail: true, trailLen: 55, trailWidth: 50, glow: true,
    click: true, clickStyle: 'both', clickSize: 50,
    shake: true, shakePower: 50, color: 'auto'
  };

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

  function hexToRgb(hex) {
    if (!hex) return null;
    var h = String(hex).trim().replace('#', '');
    if (h.length === 3) h = h[0] + h[0] + h[1] + h[1] + h[2] + h[2];
    if (!/^[0-9a-fA-F]{6}$/.test(h)) return null;
    return parseInt(h.substr(0, 2), 16) + ',' + parseInt(h.substr(2, 2), 16) + ',' + parseInt(h.substr(4, 2), 16);
  }
  function effectRgb() {
    if (cfg.color && cfg.color !== 'auto') { var c = hexToRgb(cfg.color); if (c) return c; }
    var v = getComputedStyle(document.documentElement).getPropertyValue('--md-accent-color-rgb').trim();
    return v || '226,53,53';
  }
  function num(v, def) { var n = parseFloat(v); return isNaN(n) ? def : n; }
  function bool(v, def) { if (v === undefined || v === null || v === '') return def; return String(v) !== 'false' && v !== false; }

  // 由配置推导渲染参数
  function trailMs() { return 180 + num(cfg.trailLen, 55) * 7; }          // 180 ~ 880ms
  function widthScale() { return 0.35 + num(cfg.trailWidth, 50) / 100 * 1.85; }
  function ringScale() { return 0.45 + num(cfg.clickSize, 50) / 100 * 1.35; }
  function sparkCount() { return Math.round(6 + num(cfg.clickSize, 50) / 100 * 14); }

  var trail = [], rings = [], sparks = [], mouse = { x: -999, y: -999 }, last = -999;
  var running = false;
  function now() { return performance.now(); }
  function kick() { if (!running) { running = true; requestAnimationFrame(frame); } }

  function frame() {
    var t = now(), TMS = trailMs(), WS = widthScale();
    var RING_MS = 420 + num(cfg.clickSize, 50) * 3, SPARK_MS = 660;
    while (trail.length && t - trail[0].t > TMS) trail.shift();
    if (trail.length > 40) trail.splice(0, trail.length - 40);
    rings = rings.filter(function (r) { return t - r.t < RING_MS; });
    sparks = sparks.filter(function (s) { return t - s.t < SPARK_MS; });

    ctx.clearRect(0, 0, window.innerWidth, window.innerHeight);
    var rgb = effectRgb();

    // ---- 拖尾 ----
    if (cfg.trail && trail.length > 1) {
      ctx.lineCap = 'round'; ctx.lineJoin = 'round';
      var g;
      for (g = 1; g < trail.length; g++) {
        var pg = g / (trail.length - 1);
        var ag = Math.pow(pg, 2) * 0.22 * Math.max(0, 1 - (t - trail[g].t) / TMS);
        if (ag <= 0.008) continue;
        ctx.strokeStyle = 'rgba(' + rgb + ',' + ag.toFixed(3) + ')';
        ctx.lineWidth = (3 + 22 * pg) * WS;
        ctx.beginPath(); ctx.moveTo(trail[g - 1].x, trail[g - 1].y); ctx.lineTo(trail[g].x, trail[g].y); ctx.stroke();
      }
      for (var i = 1; i < trail.length; i++) {
        var p = i / (trail.length - 1);
        var a = Math.pow(p, 1.35) * 0.95 * Math.max(0, 1 - (t - trail[i].t) / TMS);
        if (a <= 0.01) continue;
        ctx.strokeStyle = 'rgba(' + rgb + ',' + a.toFixed(3) + ')';
        ctx.lineWidth = (1.2 + 9 * p) * WS;
        ctx.beginPath(); ctx.moveTo(trail[i - 1].x, trail[i - 1].y); ctx.lineTo(trail[i].x, trail[i].y); ctx.stroke();
      }
    }
    // ---- 光标光晕 ----
    if (cfg.glow && mouse.x > -900) {
      var fade = Math.max(0, 1 - (t - last) / 420);
      if (fade > 0.02) {
        var rad = 24 + num(cfg.trailWidth, 50) * 0.28;
        var gr = ctx.createRadialGradient(mouse.x, mouse.y, 0, mouse.x, mouse.y, rad);
        gr.addColorStop(0, 'rgba(' + rgb + ',' + (0.48 * fade).toFixed(3) + ')');
        gr.addColorStop(1, 'rgba(' + rgb + ',0)');
        ctx.fillStyle = gr;
        ctx.beginPath(); ctx.arc(mouse.x, mouse.y, rad, 0, Math.PI * 2); ctx.fill();
        ctx.fillStyle = 'rgba(255,255,255,' + (0.85 * fade).toFixed(3) + ')';
        ctx.beginPath(); ctx.arc(mouse.x, mouse.y, 2.8, 0, Math.PI * 2); ctx.fill();
      }
    }
    // ---- 点击波纹 ----
    if (cfg.click && (cfg.clickStyle === 'ring' || cfg.clickStyle === 'both')) {
      for (var k = 0; k < rings.length; k++) {
        var r = rings[k], e = (t - r.t) / RING_MS;
        var rr = (6 + 78 * e) * ringScale(), alpha = Math.pow(1 - e, 1.6);
        var g2 = ctx.createRadialGradient(r.x, r.y, 0, r.x, r.y, rr);
        g2.addColorStop(0, 'rgba(' + rgb + ',' + (alpha * 0.26).toFixed(3) + ')');
        g2.addColorStop(1, 'rgba(' + rgb + ',0)');
        ctx.fillStyle = g2;
        ctx.beginPath(); ctx.arc(r.x, r.y, rr, 0, Math.PI * 2); ctx.fill();
        ctx.strokeStyle = 'rgba(' + rgb + ',' + (alpha * 0.95).toFixed(3) + ')';
        ctx.lineWidth = 0.8 + 5 * (1 - e);
        ctx.beginPath(); ctx.arc(r.x, r.y, rr, 0, Math.PI * 2); ctx.stroke();
        ctx.strokeStyle = 'rgba(255,255,255,' + (alpha * 0.5).toFixed(3) + ')';
        ctx.lineWidth = 1 + 2.5 * (1 - e);
        ctx.beginPath(); ctx.arc(r.x, r.y, rr * 0.62, 0, Math.PI * 2); ctx.stroke();
        var fl = Math.max(0, 1 - e * 3.2);
        if (fl > 0.01) {
          var g3 = ctx.createRadialGradient(r.x, r.y, 0, r.x, r.y, 22);
          g3.addColorStop(0, 'rgba(255,255,255,' + (0.55 * fl).toFixed(3) + ')');
          g3.addColorStop(1, 'rgba(255,255,255,0)');
          ctx.fillStyle = g3;
          ctx.beginPath(); ctx.arc(r.x, r.y, 22, 0, Math.PI * 2); ctx.fill();
        }
      }
    }
    // ---- 点击火花 ----
    if (cfg.click && (cfg.clickStyle === 'spark' || cfg.clickStyle === 'both')) {
      for (var j = 0; j < sparks.length; j++) {
        var s = sparks[j], es = (t - s.t) / SPARK_MS;
        var x = s.x + s.vx * es * 60, y = s.y + s.vy * es * 60 + 90 * es * es;
        ctx.fillStyle = 'rgba(' + rgb + ',' + (Math.pow(1 - es, 1.5) * 0.95).toFixed(3) + ')';
        ctx.beginPath(); ctx.arc(x, y, 2.6 * (1 - es) + 0.6, 0, Math.PI * 2); ctx.fill();
      }
    }

    if ((cfg.trail && trail.length > 1) || rings.length || sparks.length || (cfg.glow && mouse.x > -900 && t - last < 420)) requestAnimationFrame(frame);
    else { ctx.clearRect(0, 0, window.innerWidth, window.innerHeight); running = false; }
  }

  document.addEventListener('pointermove', function (e) {
    if (!cfg.enabled) return;
    mouse.x = e.clientX; mouse.y = e.clientY; last = now();
    if (cfg.trail) trail.push({ x: e.clientX, y: e.clientY, t: last });
    kick();
  }, { passive: true });

  document.addEventListener('pointerdown', function (e) {
    if (!cfg.enabled) return;
    var t = now();
    if (cfg.click) {
      rings.push({ x: e.clientX, y: e.clientY, t: t });
      if (rings.length > 6) rings.shift();
      var n = cfg.clickStyle === 'ring' ? 0 : sparkCount();
      for (var i = 0; i < n; i++) {
        var ang = Math.random() * Math.PI * 2, sp = 0.6 + Math.random() * 1.9 * ringScale();
        sparks.push({ x: e.clientX, y: e.clientY, vx: Math.cos(ang) * sp, vy: Math.sin(ang) * sp - 0.4, t: t });
      }
      if (sparks.length > 120) sparks.splice(0, sparks.length - 120);
    }
    if (cfg.shake) {
      var el = e.target && e.target.closest ? e.target.closest(SHAKE_SEL) : null;
      if (el) {
        el.classList.remove('fx-shake');
        void el.offsetWidth;
        el.classList.add('fx-shake', 'fx-press');
        setTimeout(function () { el.classList.remove('fx-shake', 'fx-press'); }, 460);
      }
    }
    kick();
  }, true);

  function clearAll() { trail = []; rings = []; sparks = []; ctx.clearRect(0, 0, window.innerWidth, window.innerHeight); running = false; }
  function setConfig(patch) {
    if (patch) for (var k in patch) if (Object.prototype.hasOwnProperty.call(patch, k)) cfg[k] = patch[k];
    if (reduce) cfg.enabled = false;
    document.documentElement.classList.toggle('no-fx', !cfg.enabled);
    document.documentElement.style.setProperty('--fx-k', (0.4 + num(cfg.shakePower, 50) / 100 * 1.6).toFixed(2));
    if (!cfg.enabled) clearAll();
    return cfg;
  }

  try {
    if (window.NE && NE.getSettings) NE.getSettings().then(function (s) {
      setConfig({
        enabled: bool(s.uiEffects, true), trail: bool(s.fxTrail, true),
        trailLen: num(s.fxTrailLen, 55), trailWidth: num(s.fxTrailWidth, 50), glow: bool(s.fxGlow, true),
        click: bool(s.fxClick, true), clickStyle: s.fxClickStyle || 'both', clickSize: num(s.fxClickSize, 50),
        shake: bool(s.fxShake, true), shakePower: num(s.fxShakePower, 50), color: s.fxColor || 'auto'
      });
    }).catch(function () { setConfig({}); });
  } catch (e) { setConfig({}); }
  setConfig({});

  window.FX = { setConfig: setConfig, get: function () { return cfg; }, clear: clearAll };
})();
