// glass.js — 「液态玻璃」材质：网页端近似实现
// ---------------------------------------------------------------------------
// Apple 的 Liquid Glass 靠实时光线折射 + 高光边缘（WWDC25）。Windows 没有对应原生材质
// （只有 Mica / Desktop Acrylic），但 Chromium 支持把 SVG 滤镜当 backdrop-filter 用，
// 于是：窗口底层用原生亚克力（能透看后方窗口），界面自身的条/面板再叠一层
//   backdrop-filter: url(#折射滤镜) blur() saturate() brightness()
// 折射用 feDisplacementMap + 一张按元素尺寸生成的位移图（凸 squircle 剖面，边缘向内放大）。
//
// 强度可调（设置 → 主题/外观 → 窗口材质 → 液态玻璃强度，ui_liquid_power 0~200%）：
//   做法来自几个开源实现把【形状】和【强度】拆开的思路——
//   · nikdelvin/liquid-glass：depth（边缘折射带宽度）与 strength（位移滤镜强度）分开
//   · PallavAg/liquid-glass-web-react：depth / strength / quality / chromaticAberration，
//     其位移图只在"镜片形状变了"时重画，挪位置/改强度都不重画
//     （该库实现来自 https://aave.com/design/building-glass-for-the-web）
//   落到这里：位移图只跟【尺寸 + depth】有关，强度只改 feDisplacementMap 的 scale 属性，
//   所以拖强度滑块不重画位移图、不涨缓存，改一个属性即时生效。
// 参考：https://kube.io/blog/liquid-glass-css-svg/  （Chrome-only 特性，WebView2 = Chromium）
(function () {
  var SVGNS = 'http://www.w3.org/2000/svg';
  var SEQ = 0;
  var maps = {};          // "WxH" -> dataURL（位移图缓存：只与尺寸有关）
  var filters = {};       // "WxH" -> { id, dm }（折射滤镜：强度变化时复用同一个）
  var order = [];         // 建过的所有滤镜，改强度时统一更新 scale
  var MAXSIDE = 320;      // 位移图长边上限（够用且省内存）
  var DEPTH = 12;         // 玻璃边缘折射带宽度（元素像素）—— 开源实现里叫 depth
  var STRENGTH = 0.55;    // 100% 时的折射强度（乘在 DEPTH 上）—— 开源实现里叫 strength
  var BLUR = 7;           // 折射后的模糊
  var POWER = 1;          // 用户强度设置（ui_liquid_power / 100）：0 = 不折射，2 = 200%

  function enabled() {
    return document.documentElement.classList.contains('liquid');
  }
  function clamp(v, a, b) { return v < a ? a : (v > b ? b : v); }

  // 目标表面：底部 dock + 上滑面板 + 右键菜单（顶栏 topbar 是贴边的整条行，不参与玻璃，否则像一圈异常高亮）
  var TARGETS = ['#player', '.pl-panel', '.ctx-menu', '.qual-menu'];

  var host = null;
  function svgHost() {
    if (host && host.isConnected) return host;
    host = document.getElementById('lg-svg');
    if (!host) {
      host = document.createElementNS(SVGNS, 'svg');
      host.id = 'lg-svg';
      host.setAttribute('width', '0'); host.setAttribute('height', '0');
      host.setAttribute('aria-hidden', 'true');
      host.style.cssText = 'position:absolute;left:-9999px;top:0;width:0;height:0';
      document.body.appendChild(host);
    }
    return host;
  }

  function mapSize(w, h) {
    var s = Math.min(1, MAXSIDE / Math.max(w, h, 1));
    return [Math.max(8, Math.round(w * s)), Math.max(8, Math.round(h * s)), s];
  }

  /// 位移图：R 通道 = x 位移，G 通道 = y 位移（128 = 不动）。
  /// 剖面用凸 squircle：y = (1-(1-t)^4)^(1/4)，越靠边折射越强、到边缘归零，避免采样到元素外。
  /// 只与尺寸有关（强度不参与），所以改强度不用重画。
  function makeMap(w, h) {
    var key = w + 'x' + h;
    if (maps[key]) return maps[key];
    var ms = mapSize(w, h), mw = ms[0], mh = ms[1];
    var c = document.createElement('canvas');
    c.width = mw; c.height = mh;
    var ctx = c.getContext('2d');
    var img = ctx.createImageData(mw, mh);
    var d = img.data;
    var bez = Math.max(1.5, DEPTH * ms[2]);                   // 位移图比元素小，边缘宽度同步缩放
    function prof(t) {                                        // t: 0=边缘 1=边缘带结束
      if (t <= 0 || t >= 1) return 0;
      var x = 1 - t;
      return Math.pow(1 - x * x * x * x, 0.25);
    }
    for (var y = 0; y < mh; y++) {
      for (var x = 0; x < mw; x++) {
        var dl = x, dr = mw - 1 - x, dt = y, db = mh - 1 - y;
        var mx = prof(Math.min(dl, dr) / bez);
        var my = prof(Math.min(dt, db) / bez);
        if (dl < dr) mx = -mx;                                // 朝最近的边推 → 边缘放大
        if (dt < db) my = -my;
        var o = (y * mw + x) * 4;
        d[o] = 128 + Math.round(mx * 127);
        d[o + 1] = 128 + Math.round(my * 127);
        d[o + 2] = 128;
        d[o + 3] = 255;
      }
    }
    ctx.putImageData(img, 0, 0);
    var url = c.toDataURL('image/png');
    maps[key] = url;
    return url;
  }

  /// feDisplacementMap 的 scale（px）：强度只体现在这一个属性上
  function scaleNow() { return Math.round(DEPTH * STRENGTH * POWER * 10) / 10; }

  /// backdrop-filter 字符串：强度 0 时干脆不带 url()（等于不退折射，只留模糊/调色）
  function filterCss(id) {
    var rest = 'blur(' + BLUR + 'px) saturate(' + (150 + 35 * POWER).toFixed(0) + '%) brightness(' + (1 + 0.06 * POWER).toFixed(3) + ')';
    return (POWER > 0.001 ? 'url(#' + id + ') ' : '') + rest;
  }

  /// 给某个尺寸造（或复用）一个折射滤镜，返回 { id, dm }
  function makeFilter(w, h) {
    var key = w + 'x' + h;
    if (filters[key]) return filters[key];
    var mapUrl = makeMap(w, h);
    var id = 'lg-f' + (++SEQ);
    var svg = svgHost();
    var f = document.createElementNS(SVGNS, 'filter');
    f.setAttribute('id', id);
    f.setAttribute('x', '0'); f.setAttribute('y', '0');
    f.setAttribute('width', '100%'); f.setAttribute('height', '100%');
    f.setAttribute('filterUnits', 'objectBoundingBox');
    f.setAttribute('color-interpolation-filters', 'sRGB');
    var im = document.createElementNS(SVGNS, 'feImage');
    im.setAttribute('href', mapUrl);
    im.setAttributeNS('http://www.w3.org/1999/xlink', 'xlink:href', mapUrl);
    im.setAttribute('x', '0'); im.setAttribute('y', '0');
    im.setAttribute('width', '100%'); im.setAttribute('height', '100%');
    im.setAttribute('preserveAspectRatio', 'none');
    im.setAttribute('result', 'm');
    var dm = document.createElementNS(SVGNS, 'feDisplacementMap');
    dm.setAttribute('in', 'SourceGraphic');
    dm.setAttribute('in2', 'm');
    dm.setAttribute('scale', String(scaleNow()));
    dm.setAttribute('xChannelSelector', 'R');
    dm.setAttribute('yChannelSelector', 'G');
    f.appendChild(im); f.appendChild(dm);
    svg.appendChild(f);
    var rec = { id: id, dm: dm };
    filters[key] = rec;
    order.push(rec);
    return rec;
  }

  /// 元素尺寸 → 位移图 + 滤镜，并把 backdrop-filter 写到元素上
  function apply(el) {
    if (!el || !el.isConnected) return;
    var r = el.getBoundingClientRect();
    if (r.width < 24 || r.height < 12) return;
    var w = Math.max(24, Math.round(r.width / 4) * 4);        // 4px 取整：尺寸微抖不重画位移图
    var h = Math.max(12, Math.round(r.height / 4) * 4);
    var rec = makeFilter(w, h);
    var bf = filterCss(rec.id);
    if (el.style.backdropFilter !== bf) {
      el.style.backdropFilter = bf;
      el.style.webkitBackdropFilter = bf;
    }
    el.setAttribute('data-lg', rec.id);
  }

  function clear(el) {
    el.style.backdropFilter = '';
    el.style.webkitBackdropFilter = '';
    el.removeAttribute('data-lg');
  }

  /// 把当前强度写进 CSS 变量：高光层（effects.css 的 --lg-spec）跟着强度一起缩放
  function pushVars() {
    var root = document.documentElement;
    root.style.setProperty('--lg-power', String(POWER));
    root.style.setProperty('--lg-spec', String(0.5 + 0.5 * POWER));
  }

  /// 立刻整铺一遍（尺寸变化 / 材质切换 / 改强度后调用）
  function applyAll() {
    pushVars();
    var list = document.querySelectorAll('[data-lg]');
    for (var i = 0; i < list.length; i++) clear(list[i]);
    if (!enabled()) return;
    TARGETS.forEach(function (sel) {
      var els = document.querySelectorAll(sel);
      for (var k = 0; k < els.length; k++) apply(els[k]);
    });
  }

  var pending = false;
  function refresh() {
    if (pending) return;
    pending = true;
    requestAnimationFrame(function () {
      pending = false;
      try {
        applyAll();
      } catch (e) {
        // 以前这里把异常吞掉，导致'滤镜个数=0'这种静默失败很难查（errs 还是 0）——改成显式报错
        try { if (window.NE_showErr) NE_showErr('液态玻璃', 'glass refresh 失败: ' + (e && e.message ? e.message : e), '', (e && e.stack) || ''); } catch (x) { }
        try { console.error('[NE-glass] refresh failed', e); } catch (x) { }
      }
    });
  }

  /// 强度（0~200，%）：只改 feDisplacementMap 的 scale + 已应用元素的 filter 字符串，同步生效
  function setPower(pct) {
    var v = Number(pct);
    if (!isFinite(v)) v = 100;
    POWER = clamp(v, 0, 200) / 100;
    var sc = String(scaleNow());
    for (var i = 0; i < order.length; i++) {
      try { order[i].dm.setAttribute('scale', sc); } catch (e) { }
    }
    pushVars();
    var list = document.querySelectorAll('[data-lg]');
    for (var j = 0; j < list.length; j++) {
      var id = list[j].getAttribute('data-lg');
      var bf = filterCss(id);
      list[j].style.backdropFilter = bf;
      list[j].style.webkitBackdropFilter = bf;
    }
    return POWER;
  }

  /// 开发期自检：强度是不是真的落到了渲染上（"没报错"不算过，必须读到 url(#lg-fN) 且 scale 跟着变）
  window.neGlassDebug = function () {
    var svg = document.getElementById('lg-svg');
    var dms = svg ? svg.querySelectorAll('feDisplacementMap') : [];
    var els = document.querySelectorAll('[data-lg]');
    var out = {
      enabled: enabled(), power: POWER, scale: scaleNow(),
      filters: svg ? svg.querySelectorAll('filter').length : 0,
      dmCount: dms.length, scales: [], applied: els.length, items: []
    };
    for (var i = 0; i < dms.length; i++) out.scales.push(dms[i].getAttribute('scale'));
    for (var j = 0; j < els.length; j++) {
      var e = els[j], cs = getComputedStyle(e);
      out.items.push({
        id: e.id || e.className,
        w: Math.round(e.getBoundingClientRect().width),
        bf: cs.backdropFilter || cs.webkitBackdropFilter
      });
    }
    return out;
  };

  /// 设置页/启动同步用：改强度并立刻重铺，返回自检结果
  window.neGlassPower = function (pct) {
    setPower(pct);
    applyAll();
    return window.neGlassDebug();
  };

  window.neGlassRefresh = refresh;
  window.addEventListener('resize', function () { if (enabled()) refresh(); });
  pushVars();
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', refresh); else refresh();
})();
