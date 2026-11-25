// glass.js — 「液态玻璃」材质：使用 Weiqin-Mo/Liquid-Glass-HTML 的位移图引擎
// ---------------------------------------------------------------------------
// 2026-11-25 整套替换：旧的自研实现（SDF 法线剖面、色散三路、厚度倾向、视角跟随、
// 液珠/涟漪/环境流动）全部移除，改为引入该项目的 liquid-glass-engine.js：
//   · 归一化坐标 + smoothStep 边缘折射带（0.12 × 长边）
//   · distortion 内部 swirl + 正弦流场扰动
//   · 滤镜 LRU 缓存（profile + 尺寸 + radius + strength + distortion + clamp）
// 参数只剩两个，映射到原有配置键（设置页与旧配置都兼容）：
//   ui_liquid_power 0~200  → refractionStrength 0~2（默认 1.2）
//   ui_liquid_fluid 0~100  → distortion 0~1.2（默认 0.6）
// 折射滤镜由引擎写到 --lg-filter，本文件负责把它接到元素的 backdrop-filter 上。
(function () {
  var TARGETS = ['#player', '.pl-panel', '.ctx-menu', '.qual-menu'];
  var STRENGTH = 1.2;
  var DISTORT = 0.6;
  var applied = [];

  function enabled() { return document.documentElement.classList.contains('liquid'); }
  function clamp(v, a, b) { return v < a ? a : (v > b ? b : v); }

  function noteErr(where, e) {
    try { if (window.NE_showErr) NE_showErr('液态玻璃', where + ': ' + (e && e.message ? e.message : e), '', (e && e.stack) || ''); } catch (x) { }
    try { console.error('[NE-glass] ' + where, e); } catch (x) { }
  }

  function glassApi() { return window.LiquidGlass; }

  // 把 --lg-filter 写进 backdrop-filter（inline 优先级最高，覆盖 main.css 里原本的 blur(6px)）
  function pushFilter(el) {
    var f = el.style.getPropertyValue('--lg-filter');
    if (!f) return;
    // 折射强度为 0 时位移图是张空图，挂着 url() 只是白耗滤镜链——退化成纯模糊
    var bf = (STRENGTH > 0.001 ? f + ' ' : '') + 'blur(7px) saturate(150%) brightness(1.03)';
    if (el.style.backdropFilter === bf) return;
    if (el.style.backdropFilter && el.style.backdropFilter !== 'none') {
      // 换滤镜时先落 none 再写新值，强制 Chromium 重新光栅化（历史踩过的坑）
      el.style.backdropFilter = 'none';
      el.style.webkitBackdropFilter = 'none';
      void el.offsetHeight;
    }
    el.style.backdropFilter = bf;
    el.style.webkitBackdropFilter = bf;
  }

  function applyOne(el) {
    var api = glassApi(); if (!api) return;
    try {
      api.setParameters({ refractionStrength: STRENGTH, distortion: DISTORT });
      api.apply(el, true);
      el.setAttribute('data-lg', 'lg');
      pushFilter(el);
    } catch (e) { noteErr('apply', e); }
  }

  function clearOne(el) {
    el.style.backdropFilter = '';
    el.style.webkitBackdropFilter = '';
    el.classList.remove('glass-active');
    el.style.removeProperty('--lg-filter');
    el.removeAttribute('data-lg');
  }

  function applyAll() {
    applied.forEach(clearOne);
    applied = [];
    if (!enabled()) return;
    TARGETS.forEach(function (sel) {
      var els = document.querySelectorAll(sel);
      for (var i = 0; i < els.length; i++) {
        var el = els[i];
        var r = el.getBoundingClientRect();
        if (r.width < 24 || r.height < 12) continue;
        applyOne(el);
        applied.push(el);
      }
    });
  }

  var pending = false;
  function refresh() {
    if (pending) return;
    pending = true;
    requestAnimationFrame(function () {
      pending = false;
      try { applyAll(); } catch (e) { noteErr('refresh', e); }
    });
  }

  function setOptions(opt) {
    if (!opt) return window.neGlassDebug();
    if (opt.power !== undefined) {
      var p = Number(opt.power);
      STRENGTH = clamp(isFinite(p) ? p : 100, 0, 200) / 100;
    }
    if (opt.fluid !== undefined) {
      var f = Number(opt.fluid);
      DISTORT = clamp(isFinite(f) ? f : 50, 0, 100) / 100 * 1.2;
    }
    // ca / thickness / parallax 在新引擎里没有对应概念，忽略即可（旧配置键仍可读）
    var api = glassApi();
    if (api) api.setParameters({ refractionStrength: STRENGTH, distortion: DISTORT });
    refresh();
    return window.neGlassDebug();
  }

  // 播放列表面板 / 右键菜单这类浮层是动态插入的，出现时要重新铺一遍玻璃
  function observeDom() {
    if (!window.MutationObserver) return;
    var timer = 0;
    try {
      new MutationObserver(function () {
        if (!enabled()) return;
        clearTimeout(timer);
        timer = setTimeout(refresh, 180);
      }).observe(document.body, { childList: true, subtree: true });
    } catch (e) { noteErr('observe', e); }
  }

  function boot() { try { refresh(); observeDom(); } catch (e) { noteErr('boot', e); } }

  window.neGlassSet = setOptions;
  window.neGlassPower = function (pct) { return setOptions({ power: pct }); };
  window.neGlassRefresh = refresh;
  window.neGlassStats = function () { return { applied: applied.length }; };
  window.neGlassDebug = function () {
    var first = applied[0];
    return {
      enabled: enabled(),
      strength: STRENGTH,
      distortion: DISTORT,
      applied: applied.length,
      filter: first ? first.style.getPropertyValue('--lg-filter') : '',
      engine: !!(window.LiquidGlass)
    };
  };

  if (document.readyState === 'complete' || document.readyState === 'interactive') boot();
  else document.addEventListener('DOMContentLoaded', boot);
  window.addEventListener('resize', function () { if (enabled()) refresh(); });
})();
