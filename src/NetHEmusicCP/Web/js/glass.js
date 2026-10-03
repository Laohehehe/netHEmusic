// glass.js — 「液态玻璃」材质：网页端近似实现
// ---------------------------------------------------------------------------
// Apple 的 Liquid Glass 靠实时光线折射 + 高光边缘（WWDC25）。Windows 没有对应原生材质
// （只有 Mica / Desktop Acrylic），但 Chromium 支持把 SVG 滤镜当 backdrop-filter 用，
// 于是：窗口底层用原生亚克力（能透看后方窗口），界面自身的条/面板再叠一层
//   backdrop-filter: url(#折射滤镜) blur() saturate() brightness()
// 折射用 feDisplacementMap + 一张按元素尺寸生成的位移图（凸 squircle 剖面，边缘向内放大）。
//
// 可调：设置 → 主题/外观 → 窗口材质 → 液态玻璃强度（ui_liquid_power 0~200%）、色散（ui_liquid_ca 0~100%）
//   做法来自几个开源实现把【形状】和【强度】拆开的思路——
//   · nikdelvin/liquid-glass：depth（边缘折射带宽度）与 strength（位移滤镜强度）分开，
//     另有 chromaticAberration 与独立的 specular 贴图
//   · PallavAg/liquid-glass-web-react：depth / strength / quality / chromaticAberration，
//     位移图只在"镜片形状变了"时重画（实现来自 https://aave.com/design/building-glass-for-the-web）
//   · Rethink-JS/rt-liquid-glass：同样的旋钮（blur / scale / map）
//   落到这里：位移图只跟【尺寸 + depth】有关，强度只改 feDisplacementMap 的 scale 属性，
//   所以拖强度滑块不重画位移图、不涨缓存，改一个属性即时生效。
//
// 色散（chromatic aberration）：R/G/B 三路按 1±c 的倍率分别折射，通道分离后用 feBlend screen 合成。
//   ⚠️ 但开源实现那种"直接 screen 合成"的写法在这里会偏色：网页 body 背景是 rgba(…,0.55)，
//   而 feBlend = blend + source-over 合成，α<1 时红通道会被除以 (2-α)≈1.45。
//   所以这里多做两步：把三个单通道图的 alpha 先强制成 1（screen 才是精确的通道重建），
//   最后用 feComposite operator="in" 把基准那一路（g0）的真实 alpha 收回来 —— 颜色精确、透明度不变。
//
// 高光层：每个元素按尺寸生成一张"镜面高光贴图"（圆角边缘反光 + 斜向光带 + 下侧回光），
//   通过 CSS 变量 --lg-spec-img 贴上去（effects.css 里作为 background-image 的第一层，
//   没生成出来时回退到原来的三条渐变）。强度跟着 --lg-power 走。
// 参考：https://kube.io/blog/liquid-glass-css-svg/  （Chrome-only 特性，WebView2 = Chromium）
(function () {
  var SVGNS = 'http://www.w3.org/2000/svg';
  var SEQ = 0;
  var maps = {};          // "WxH" -> dataURL（位移图缓存：只与尺寸有关）
  var specs = {};         // "WxH@r@spec" -> dataURL（高光贴图缓存）
  var filters = {};       // "WxH@ca" -> { id, node, dms:[{dm,f}] }（折射滤镜：强度/色散变化时复用）
  var order = [];         // 建过的所有滤镜，改强度时统一更新 scale
  var applied = [];       // 当前铺了玻璃的元素（改强度时要重刷高光贴图）
  var MAXSIDE = 320;      // 位移图长边上限（够用且省内存）
  var DEPTH = 12;         // 玻璃边缘折射带宽度（元素像素）—— 开源实现里叫 depth
  var STRENGTH = 0.55;    // 100% 时的折射强度（乘在 DEPTH 上）—— 开源实现里叫 strength
  var BLUR = 7;           // 折射后的模糊
  var CA_MAX = 0.45;      // 色散上限：色散 100% 时 R/B 的位移倍率 = 1±0.45（50% ≈ ±0.22，看得出彩边但不脏）
  var POWER = 1;          // 强度设置（ui_liquid_power / 100）：0 = 不折射，2 = 200%
  var CA = 0.5;           // 色散设置（ui_liquid_ca / 100）：0 = 关（回到单步折射）
  var SPECMUL = 1;        // 高光倍率（开发期 A/B 用，正常恒为 1）

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
  /// 只与尺寸有关（强度/色散不参与），所以改设置不用重画。
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

  function roundRectPath(ctx, x, y, w, h, r) {
    r = Math.max(0, Math.min(r, Math.min(w, h) / 2));
    ctx.beginPath();
    ctx.moveTo(x + r, y);
    ctx.lineTo(x + w - r, y); ctx.arcTo(x + w, y, x + w, y + r, r);
    ctx.lineTo(x + w, y + h - r); ctx.arcTo(x + w, y + h, x + w - r, y + h, r);
    ctx.lineTo(x + r, y + h); ctx.arcTo(x, y + h, x, y + h - r, r);
    ctx.lineTo(x, y + r); ctx.arcTo(x, y, x + r, y, r);
    ctx.closePath();
  }

  /// 高光倍率：跟随强度设置（100% 时 = 1，和改造前的 CSS 高光一致）
  function specAmount() { return (0.5 + 0.5 * POWER) * SPECMUL; }

  /// 每元素"镜面高光贴图"：圆角边缘反光（软光晕 + 亮线）+ 斜向光带 + 右下回光 + 顶边内侧柔光。
  /// 纯 canvas 绘图（没有逐像素 JS），所以跟着强度重画也很快。
  function makeSpec(w, h, radius, spec) {
    var key = w + 'x' + h + '@' + Math.round(radius) + '@' + spec.toFixed(2);
    if (specs[key]) return specs[key];
    var c = document.createElement('canvas');
    c.width = w; c.height = h;
    var ctx = c.getContext('2d');
    function W(al) { return 'rgba(255,255,255,' + Math.max(0, al * spec).toFixed(3) + ')'; }
    // ① 斜向主光带（左上受光）
    var g1 = ctx.createLinearGradient(0, 0, w * 0.8, h * 1.4);
    g1.addColorStop(0, W(0.115)); g1.addColorStop(0.28, W(0.045)); g1.addColorStop(0.55, W(0));
    ctx.fillStyle = g1; ctx.fillRect(0, 0, w, h);
    // ② 右下回光
    var g2 = ctx.createLinearGradient(w, h, w * 0.5, h * 0.35);
    g2.addColorStop(0, W(0.075)); g2.addColorStop(0.4, W(0));
    ctx.fillStyle = g2; ctx.fillRect(0, 0, w, h);
    // ③ 顶边内侧柔光
    var g3 = ctx.createLinearGradient(0, 0, 0, Math.max(8, Math.min(h * 0.5, 44)));
    g3.addColorStop(0, W(0.13)); g3.addColorStop(1, W(0));
    ctx.fillStyle = g3; ctx.fillRect(0, 0, w, h);
    // ④ 圆角边缘反光：宽而淡的光晕 + 细而亮的亮线
    var r = Math.max(0, Math.min(radius, Math.min(w, h) / 2));
    var st = ctx.createLinearGradient(0, 0, w * 0.9, h);
    st.addColorStop(0, W(0.34)); st.addColorStop(0.42, W(0.15)); st.addColorStop(1, W(0.06));
    ctx.strokeStyle = st; ctx.lineJoin = 'round';
    ctx.lineWidth = 3.6; ctx.globalAlpha = 0.5;
    roundRectPath(ctx, 1.8, 1.8, w - 3.6, h - 3.6, r - 1.8); ctx.stroke();
    ctx.globalAlpha = 1; ctx.lineWidth = 1.2;
    roundRectPath(ctx, 0.6, 0.6, w - 1.2, h - 1.2, r - 0.6); ctx.stroke();
    var url = c.toDataURL('image/png');
    specs[key] = url;
    return url;
  }

  /// feDisplacementMap 的 scale（px）：强度只体现在这个属性上
  function scaleNow() { return DEPTH * STRENGTH * POWER; }
  function scaleStr(f) { return String(Math.round(scaleNow() * f * 10) / 10); }

  /// backdrop-filter 字符串：强度 0 时干脆不带 url()（等于不折射，只留模糊/调色）
  function filterCss(id) {
    var rest = 'blur(' + BLUR + 'px) saturate(' + (150 + 35 * POWER).toFixed(0) + '%) brightness(' + (1 + 0.06 * POWER).toFixed(3) + ')';
    return (POWER > 0.001 ? 'url(#' + id + ') ' : '') + rest;
  }

  function el3(tag, attrs) {
    var e = document.createElementNS(SVGNS, tag);
    for (var k in attrs) e.setAttribute(k, attrs[k]);
    return e;
  }

  /// 给某个尺寸造（或复用）一个折射滤镜，返回 { id, node, dms }
  /// 色散关（CA≈0）时就是以前那两步：feImage + 单次 feDisplacementMap
  function makeFilter(w, h) {
    var caKey = Math.round(CA * 100);
    var key = w + 'x' + h + '@' + caKey;
    if (filters[key]) return filters[key];
    var mapUrl = makeMap(w, h);
    var id = 'lg-f' + (++SEQ);
    var svg = svgHost();
    var f = el3('filter', {
      id: id, x: '0', y: '0', width: '100%', height: '100%',
      filterUnits: 'objectBoundingBox', 'color-interpolation-filters': 'sRGB'
    });
    var im = el3('feImage', {
      href: mapUrl, x: '0', y: '0', width: '100%', height: '100%',
      preserveAspectRatio: 'none', result: 'm'
    });
    im.setAttributeNS('http://www.w3.org/1999/xlink', 'xlink:href', mapUrl);
    f.appendChild(im);
    var dms = [];
    function disp(factor, result) {
      var dm = el3('feDisplacementMap', {
        'in': 'SourceGraphic', in2: 'm', scale: scaleStr(factor),
        xChannelSelector: 'R', yChannelSelector: 'G'
      });
      if (result) dm.setAttribute('result', result);
      f.appendChild(dm);
      dms.push({ dm: dm, f: factor });
      return dm;
    }
    var ca = CA_MAX * CA;
    if (ca > 0.002) {
      // 三路折射（R 偏移量最大、G 基准、B 最小）→ 单通道分离 → screen 合成 → 用 G 那一路的 alpha 收口
      disp(1 + ca, 'r0');
      disp(1, 'g0');
      disp(1 - ca, 'b0');
      // 单通道 + alpha 强制为 1（screen 只有在 α=1 时才是精确的通道重建）
      f.appendChild(el3('feColorMatrix', { 'in': 'r0', type: 'matrix', result: 'chR', values: '1 0 0 0 0  0 0 0 0 0  0 0 0 0 0  0 0 0 0 1' }));
      f.appendChild(el3('feColorMatrix', { 'in': 'g0', type: 'matrix', result: 'chG', values: '0 0 0 0 0  0 1 0 0 0  0 0 0 0 0  0 0 0 0 1' }));
      f.appendChild(el3('feColorMatrix', { 'in': 'b0', type: 'matrix', result: 'chB', values: '0 0 0 0 0  0 0 0 0 0  0 0 1 0 0  0 0 0 0 1' }));
      f.appendChild(el3('feBlend', { 'in': 'chR', in2: 'chG', mode: 'screen', result: 'rg' }));
      f.appendChild(el3('feBlend', { 'in': 'rg', in2: 'chB', mode: 'screen', result: 'rgb' }));
      // 把真实 alpha（~0.55）收回来：颜色取自上面合成的结果，alpha 用 g0 的（= 基准折射那一路）
      f.appendChild(el3('feComposite', { 'in': 'rgb', in2: 'g0', operator: 'in' }));
    } else {
      disp(1);
    }
    svg.appendChild(f);
    var rec = { id: id, node: f, dms: dms };
    filters[key] = rec;
    order.push(rec);
    return rec;
  }

  /// 色散开关变化时，旧的滤镜链作废（结构不同）
  function resetFilters() {
    for (var k in filters) {
      var n = filters[k].node;
      if (n && n.parentNode) n.parentNode.removeChild(n);
    }
    filters = {}; order = []; SEQ = 0;
  }

  /// 元素尺寸 → 位移图 + 滤镜 + 高光贴图，并把 backdrop-filter / --lg-spec-img 写到元素上
  function apply(el) {
    if (!el || !el.isConnected) return;
    var r = el.getBoundingClientRect();
    if (r.width < 24 || r.height < 12) return;
    var w = Math.max(24, Math.round(r.width / 4) * 4);        // 4px 取整：尺寸微抖不重画位移图
    var h = Math.max(12, Math.round(r.height / 4) * 4);
    var rec = makeFilter(w, h);
    el.setAttribute('data-lg', rec.id);
    paint(el, rec, w, h);
    applied.push(el);
  }

  function paint(el, rec, w, h) {
    var bf = filterCss(rec.id);
    if (el.style.backdropFilter !== bf) {
      el.style.backdropFilter = bf;
      el.style.webkitBackdropFilter = bf;
    }
    var radius = 0;
    try { radius = parseFloat(getComputedStyle(el).borderTopLeftRadius) || 0; } catch (e) { radius = 0; }
    var url = makeSpec(w, h, Math.max(0, Math.min(radius, 40)), Math.round(specAmount() * 10) / 10);
    if (el.__lgSpec !== url) {                                  // 同一个 dataURL 不重复 setProperty
      el.style.setProperty('--lg-spec-img', 'url("' + url + '")');
      el.__lgSpec = url;
    }
  }

  function clear(el) {
    el.style.backdropFilter = '';
    el.style.webkitBackdropFilter = '';
    el.style.removeProperty('--lg-spec-img');
    el.removeAttribute('data-lg');
    el.__lgSpec = null;
  }

  /// 把当前强度写进 CSS 变量：高光层（effects.css）跟着强度一起缩放
  function pushVars() {
    var root = document.documentElement;
    root.style.setProperty('--lg-power', String(POWER));
    root.style.setProperty('--lg-spec', String(specAmount()));
  }

  /// 立刻整铺一遍（尺寸变化 / 材质切换 / 改设置后调用）
  function applyAll() {
    pushVars();
    var list = document.querySelectorAll('[data-lg]');
    for (var i = 0; i < list.length; i++) clear(list[i]);
    applied = [];
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

  /// 设置入口：{ power: 0~200, ca: 0~100, spec: 倍率(开发用) }
  /// 强度只改 feDisplacementMap 的 scale（不重画位移图）；色散变了才重建滤镜链
  function setOptions(opt) {
    opt = opt || {};
    if (opt.power !== undefined) {
      var p = Number(opt.power);
      if (!isFinite(p)) p = 100;
      POWER = clamp(p, 0, 200) / 100;
      var sc = [];
      for (var i = 0; i < order.length; i++) {
        for (var j = 0; j < order[i].dms.length; j++) {
          var d = order[i].dms[j];
          var s = scaleStr(d.f);
          if (d.dm.getAttribute('scale') !== s) d.dm.setAttribute('scale', s);
        }
      }
    }
    if (opt.ca !== undefined) {
      var c = Number(opt.ca);
      if (!isFinite(c)) c = 50;
      var nc = clamp(c, 0, 100) / 100;
      if (Math.abs(nc - CA) > 0.0005) { CA = nc; resetFilters(); }
    }
    if (opt.spec !== undefined) {
      var s2 = Number(opt.spec);
      SPECMUL = isFinite(s2) ? clamp(s2, 0, 2) : 1;
    }
    applyAll();
    return window.neGlassDebug();
  }

  /// 开发期自检：设置是不是真的落到了渲染上（"没报错"不算过）
  window.neGlassDebug = function () {
    var svg = document.getElementById('lg-svg');
    var els = document.querySelectorAll('[data-lg]');
    var out = {
      enabled: enabled(), power: POWER, ca: CA, spec: specAmount(),
      scale: Math.round(scaleNow() * 10) / 10,
      filters: svg ? svg.querySelectorAll('filter').length : 0,
      applied: els.length, items: []
    };
    for (var j = 0; j < els.length; j++) {
      var e = els[j], cs = getComputedStyle(e);
      var f = e.getAttribute('data-lg');
      var node = f ? document.getElementById(f) : null;
      var dms = node ? node.querySelectorAll('feDisplacementMap') : [];
      var scales = [];
      for (var k = 0; k < dms.length; k++) scales.push(dms[k].getAttribute('scale'));
      out.items.push({
        id: e.id || e.className,
        w: Math.round(e.getBoundingClientRect().width),
        bf: cs.backdropFilter || cs.webkitBackdropFilter,
        prims: node ? node.childNodes.length : 0,
        dmScales: scales,
        specImg: (e.style.getPropertyValue('--lg-spec-img') || '').length
      });
    }
    return out;
  };

  window.neGlassSet = setOptions;
  window.neGlassPower = function (pct) { return setOptions({ power: pct }); };
  window.neGlassRefresh = refresh;
  window.addEventListener('resize', function () { if (enabled()) refresh(); });
  pushVars();
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', refresh); else refresh();
})();
