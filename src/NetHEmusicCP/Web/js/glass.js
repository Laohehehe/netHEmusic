// glass.js — 「液态玻璃」材质：网页端近似实现
// ---------------------------------------------------------------------------
// Apple 的 Liquid Glass 靠实时光线折射 + 高光边缘（WWDC25）。Windows 没有对应原生材质
// （只有 Mica / Desktop Acrylic），但 Chromium 支持把 SVG 滤镜当 backdrop-filter 用，
// 于是：窗口底层用原生亚克力（能透看后方窗口），界面自身的条/面板再叠一层
//   backdrop-filter: url(#折射滤镜) blur() saturate() brightness()
// 折射用 feDisplacementMap + 一张按元素尺寸生成的位移图（凸 squircle 剖面，边缘向内放大）。
// 参考：https://kube.io/blog/liquid-glass-css-svg/  （Chrome-only 特性，WebView2 = Chromium）
(function () {
  var SVGNS = 'http://www.w3.org/2000/svg';
  var SEQ = 0;
  var maps = {};          // "WxH" -> dataURL（位移图缓存）
  var filters = {};       // "WxH" -> filter id
  var MAXSIDE = 320;      // 位移图长边上限（够用且省内存）
  var BEZEL = 12;         // 玻璃边缘宽度（元素像素），决定折射带多宽
  var STRENGTH = 0.55;    // 折射强度（相对边缘宽度）

  function enabled() {
    return document.documentElement.classList.contains('liquid');
  }

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

  /// 位移图：R 通道 = x 位移，G 通道 = y 位移（128 = 不动）。
  /// 剖面用凸 squircle：y = (1-(1-t)^4)^(1/4)，越靠边折射越强、到边缘归零，避免采样到元素外。
  function makeMap(w, h, scale) {
    var key = w + 'x' + h + '@' + scale.toFixed(3);
    if (maps[key]) return maps[key];
    var c = document.createElement('canvas');
    c.width = w; c.height = h;
    var ctx = c.getContext('2d');
    var img = ctx.createImageData(w, h);
    var d = img.data;
    var bez = Math.max(1.5, BEZEL * scale);                   // 位移图比元素小，边缘宽度同步缩放
    var mw = w, mh = h;
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

  /// 给某个尺寸造（或复用）一个折射滤镜，返回 filter id
  function makeFilter(w, h, scale) {
    var key = w + 'x' + h + '@' + scale.toFixed(3);
    if (filters[key]) return filters[key];
    var mapUrl = makeMap(w, h, scale);
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
    dm.setAttribute('scale', String(Math.round(BEZEL * STRENGTH)));
    dm.setAttribute('xChannelSelector', 'R');
    dm.setAttribute('yChannelSelector', 'G');
    f.appendChild(im); f.appendChild(dm);
    svg.appendChild(f);
    filters[key] = id;
    return id;
  }

  function mapSize(w, h) {
    var s = Math.min(1, MAXSIDE / Math.max(w, h, 1));
    return [Math.max(8, Math.round(w * s)), Math.max(8, Math.round(h * s)), s];
  }

  /// 元素尺寸 → 缩放后的位移图尺寸 + 滤镜
  function apply(el) {
    if (!el || !el.isConnected) return;
    var r = el.getBoundingClientRect();
    if (r.width < 24 || r.height < 12) return;
    var ms = mapSize(r.width, r.height);
    var id = makeFilter(ms[0], ms[1], ms[2]);
    var bf = 'url(#' + id + ') blur(7px) saturate(185%) brightness(1.06)';
    if (el.style.backdropFilter !== bf) {
      el.style.backdropFilter = bf;
      el.style.webkitBackdropFilter = bf;
    }
    el.setAttribute('data-lg', id);
  }

  function clear(el) {
    el.style.backdropFilter = '';
    el.style.webkitBackdropFilter = '';
    el.removeAttribute('data-lg');
  }

  var pending = false;
  function refresh() {
    if (pending) return;
    pending = true;
    requestAnimationFrame(function () {
      pending = false;
      try {
        var list = document.querySelectorAll('[data-lg]');
        for (var i = 0; i < list.length; i++) clear(list[i]);
        if (!enabled()) return;
        TARGETS.forEach(function (sel) {
          var els = document.querySelectorAll(sel);
          for (var k = 0; k < els.length; k++) apply(els[k]);
        });
      } catch (e) { }
    });
  }

  window.neGlassRefresh = refresh;
  window.addEventListener('resize', function () { if (enabled()) refresh(); });
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', refresh); else refresh();
})();
