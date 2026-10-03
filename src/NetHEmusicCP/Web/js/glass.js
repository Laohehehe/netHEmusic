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
// 高光层：已按用户要求【整层删除】。原来每个元素会生成"镜面高光贴图"（圆角边缘反光 + 斜向光带），
//   再叠一条白色描边 + inset 白色高光 —— 实测在深色主题下看起来就是一圈发光的白边（异样的白光），
//   用户明确说"去掉，不是调小"。现在只保留：折射 + 模糊 + 饱和度/亮度 + 一层外投影，画面里没有白色装饰。
//   （视差不再靠移动光带，改成让折射本身随视角平移：滤镜里加一个 feOffset，见 VIEWSHIFT。）
// 参考：https://kube.io/blog/liquid-glass-css-svg/  （Chrome-only 特性，WebView2 = Chromium）
(function () {
  var SVGNS = 'http://www.w3.org/2000/svg';
  var SEQ = 0;
  var maps = {};          // "WxH@thick" -> dataURL（位移图缓存：只与尺寸 + 厚度有关）
  var filters = {};       // "WxH@ca" -> { id, node, dm, off }（折射滤镜：强度/色散变化时复用）
  var order = [];         // 建过的所有滤镜，改强度/视角时统一更新
  var applied = [];       // 当前铺了玻璃的元素
  var MAXSIDE = 320;      // 位移图长边上限（够用且省内存）
  var DEPTH = 12;         // 玻璃边缘折射带宽度（元素像素）—— 开源实现里叫 depth
  var STRENGTH = 0.55;    // 100% 时的折射强度（乘在 DEPTH 上）—— 开源实现里叫 strength
  var BLUR = 7;           // 折射后的模糊
  var CA_MAX = 0.45;      // 色散上限：色散 100% 时 R/B 的位移倍率 = 1±0.45（50% ≈ ±0.22，看得出彩边但不脏）
  var POWER = 1;          // 强度设置（ui_liquid_power / 100）：0 = 不折射，2 = 200%
  var CA = 0.5;           // 色散设置（ui_liquid_ca / 100）：0 = 关（回到单步折射）
  var THICK = 0.6;        // 厚度倾向（ui_liquid_thick / 100）：0 = 四周一样厚（改造前原样），1 = 上厚下薄最明显
  var PARA = 0.5;         // 视角跟随（ui_liquid_para / 100）：折射随鼠标位置平移的幅度，0 = 不动
  var VIEWSHIFT = 4;      // 视角跟随的最大位移（px）：feOffset 平移取样的背景
  var MAPFEATHER = 8;     // 位移图比元素尺寸多出的一圈（px）：元素尺寸取整后仍保证盖满，不留没覆盖的边

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
  /// 剖面换成 liquidGL 那套：圆角盒 SDF + 解析法线 + liquidRel 透镜剖面（A/B/C 常量照它的着色器）。
  /// 为什么换：原来"横竖各算一条剖面"在**圆角处是错的**（角上折射方向不对）；SDF + 法线给出真正沿边法线
  /// 的折射，四角自然变成斜面透镜。位移量 = 法线 × distance × (1-ref)：边缘归零、往里一段最强、再深归零。
  /// 只与尺寸 + 圆角 + 厚度倾向有关（强度/色散不参与），所以改强度不用重画。
  /// 厚度不均（THICK>0）：按法线方向把四条边的带宽/幅值混合；THICK=0 时四周一样（band=DEPTH, gain=1）。
  var LQ_A = 1.75, LQ_B = 1.25, LQ_C = 2.0;

  function edgeProfile() {
    var t = THICK;
    return {
      bezT: DEPTH * (1 + 0.45 * t), bezB: DEPTH * (1 - 0.30 * t),
      bezL: DEPTH * (1 + 0.10 * t), bezR: DEPTH * (1 - 0.10 * t),
      gainT: 1 + 0.15 * t, gainB: 1 - 0.15 * t, gainL: 1, gainR: 1 - 0.05 * t
    };
  }

  function sdRoundBox(px, py, hx, hy, r) {
    var qx = Math.abs(px) - hx + r, qy = Math.abs(py) - hy + r;
    var ax = qx > 0 ? qx : 0, ay = qy > 0 ? qy : 0;
    return Math.sqrt(ax * ax + ay * ay) + Math.min(Math.max(qx, qy), 0) - r;
  }

  /// liquidGL 的透镜剖面：边缘 0（不位移）→ 带内增强 → 深处回到 0
  function liquidRel(dst, r, blur) {
    var n = Math.pow(Math.max(0, Math.min(1, (dst - r + blur) / blur)), LQ_A);
    return 1 - Math.pow(1 - Math.pow(1 - n, LQ_B), LQ_C);
  }

  function makeMap(w, h, radius) {
    var rad = Math.max(0, Math.round(radius || 0));
    var key = w + 'x' + h + '@' + Math.round(THICK * 20) + '@' + rad;
    if (maps[key]) return maps[key];
    var ms = mapSize(w, h), mw = ms[0], mh = ms[1];
    var c = document.createElement('canvas');
    c.width = mw; c.height = mh;
    var ctx = c.getContext('2d');
    var img = ctx.createImageData(mw, mh);
    var d = img.data;
    var e = edgeProfile();
    // 位移图会被 feImage 铺到 (w+MAPFEATHER) × (h+MAPFEATHER) 个元素像素上，所以按同样比例换算元素坐标
    var fx = (w + MAPFEATHER) / mw, fy = (h + MAPFEATHER) / mh;
    var hx = Math.max(w * 0.5 - 0.75, 1), hy = Math.max(h * 0.5 - 0.75, 1);
    var r = Math.max(0, Math.min(rad, Math.min(hx, hy)));
    var vx = new Float32Array(mw * mh), vy = new Float32Array(mw * mh), maxMag = 0;
    for (var y = 0; y < mh; y++) {
      var py = (y + 0.5) * fy - h * 0.5;
      for (var x = 0; x < mw; x++) {
        var px = (x + 0.5) * fx - w * 0.5;
        var sd = sdRoundBox(px, py, hx, hy, r);
        // 法线：SDF 的数值梯度（±1 元素像素，指向外）
        var nx = sdRoundBox(px + 1, py, hx, hy, r) - sdRoundBox(px - 1, py, hx, hy, r);
        var ny = sdRoundBox(px, py + 1, hx, hy, r) - sdRoundBox(px, py - 1, hx, hy, r);
        var len = Math.sqrt(nx * nx + ny * ny);
        if (len < 1e-4) { nx = 0; ny = -1; len = 1; }
        nx /= len; ny /= len;
        var ax = nx < 0 ? -nx : nx, ay = ny < 0 ? -ny : ny;
        var band = ax * (nx < 0 ? e.bezL : e.bezR) + ay * (ny < 0 ? e.bezT : e.bezB);
        var gain = ax * (nx < 0 ? e.gainL : e.gainR) + ay * (ny < 0 ? e.gainT : e.gainB);
        var dist = Math.max(0, Math.min(r + sd, r + band));
        var mag = dist * (1 - liquidRel(dist, r, band)) * gain;
        // 取样取"往里"（与 liquidGL 一致：sample = coord - 法线×位移），所以这里取负号
        var i = y * mw + x;
        vx[i] = -nx * mag; vy[i] = -ny * mag;
        var abs = Math.sqrt(vx[i] * vx[i] + vy[i] * vy[i]);
        if (abs > maxMag) maxMag = abs;
      }
    }
    if (maxMag < 1e-6) maxMag = 1;                            // 极小元素兜底，避免除零
    for (var k = 0; k < mw * mh; k++) {
      var o = k * 4;
      d[o] = 128 + Math.round((vx[k] / maxMag) * 127);
      d[o + 1] = 128 + Math.round((vy[k] / maxMag) * 127);
      d[o + 2] = 128;
      d[o + 3] = 255;
    }
    ctx.putImageData(img, 0, 0);
    var url = c.toDataURL('image/png');
    maps[key] = url;
    return url;
  }

  /// feDisplacementMap 的 scale（px）：强度只体现在这个属性上
  function scaleNow() { return DEPTH * STRENGTH * POWER; }
  function scaleStr(f) { return String(Math.round(scaleNow() * f * 10) / 10); }

  /// 视角跟随：把取样的背景整体平移一点（模拟"从不同角度看这块玻璃"），没有白色装饰
  function viewDx() { return Math.round(LX * PARA * VIEWSHIFT * 100) / 100; }
  function viewDy() { return Math.round(LY * PARA * VIEWSHIFT * 100) / 100; }

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
  function makeFilter(w, h, radius) {
    var caKey = Math.round(CA * 100);
    var rad = Math.max(0, Math.round(radius || 0));
    var key = w + 'x' + h + '@' + caKey + '@' + rad;      // 圆角变了位移图也变（SDF 用它算斜面和法线）
    if (filters[key]) return filters[key];
    var mapUrl = makeMap(w, h, rad);
    var id = 'lg-f' + (++SEQ);
    var svg = svgHost();
    var f = el3('filter', {
      id: id, x: '0', y: '0', width: '100%', height: '100%',
      filterUnits: 'objectBoundingBox', 'color-interpolation-filters': 'sRGB'
    });
    var im = el3('feImage', {
      href: mapUrl,
      // ⚠️ 关键：这里必须用**像素**宽高，不能写 "100%"！
      // filter 的 primitiveUnits 默认是 userSpaceOnUse，里面的百分比是相对【宿主 <svg> 的视口】解析的，
      // 而我们的宿主 svg 是 0x0（只放滤镜、不上屏）→ "100%" 解析成 0 → feImage 变成一张空图 →
      // 位移图根本不参与运算（画面退化成"整块背景被 -0.5*scale 均匀平移"，左右上下都看不出边缘折射）。
      // 2026-10-03 发现并修正：改成像素宽高后，位移图才真正生效（厚度/边缘折射立即可见）。
      // 每个尺寸一个滤镜，所以这里的 w/h 就是元素尺寸（4px 取整，外面再加一圈余量防止盖不满）。
      x: '0', y: '0', width: String(w + MAPFEATHER), height: String(h + MAPFEATHER),
      preserveAspectRatio: 'none', result: 'm'
    });
    im.setAttributeNS('http://www.w3.org/1999/xlink', 'xlink:href', mapUrl);
    f.appendChild(im);
    // 视角跟随：先按鼠标位置把背景整体平移一点点（feOffset），再做折射 —— 没有白色高光也能有"看玻璃角度在变"的感觉
    var off = el3('feOffset', { 'in': 'SourceGraphic', dx: String(viewDx()), dy: String(viewDy()), result: 'v' });
    f.appendChild(off);
    var dms = [];
    function disp(factor, result) {
      var dm = el3('feDisplacementMap', {
        'in': 'v', in2: 'm', scale: scaleStr(factor),
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
    var rec = { id: id, node: f, dms: dms, off: off };
    filters[key] = rec;
    order.push(rec);
    return rec;
  }

  /// 色散/厚度变化时，旧的滤镜链作废（结构或位移图变了）
  /// ⚠️ 不要重置 SEQ：滤镜 id 复用（lg-f1 → 又建一个 lg-f1）会让元素的 backdrop-filter 字符串一字不变，
  /// Chromium 认为没有样式变化就不重新光栅化 → 页面看起来"设置不生效"。新 id 才能可靠触发失效。
  function resetFilters() {
    for (var k in filters) {
      var n = filters[k].node;
      if (n && n.parentNode) n.parentNode.removeChild(n);
    }
    filters = {}; order = [];
  }

  /// 把折射滤镜写到元素上（高光贴图整层已删除，画面里没有白色装饰）
  function apply(el) {
    if (!el || !el.isConnected) return;
    var r = el.getBoundingClientRect();
    if (r.width < 24 || r.height < 12) return;
    var w = Math.max(24, Math.round(r.width / 4) * 4);        // 4px 取整：尺寸微抖不重画位移图
    var h = Math.max(12, Math.round(r.height / 4) * 4);
    var rad = 0;                                              // 元素实际圆角：SDF 斜面/法线要用它
    try { rad = parseFloat(getComputedStyle(el).borderTopLeftRadius) || 0; } catch (e) { rad = 0; }
    var rec = makeFilter(w, h, Math.max(0, Math.min(rad, 60)));
    el.setAttribute('data-lg', rec.id);
    paint(el, rec);
    applied.push(el);
  }

  function paint(el, rec) {
    var bf = filterCss(rec.id);
    // 判断"是否需要写"直接读**内联样式**（el.style）—— 不要用自己缓存的字符串：
    // clear() 会把内联样式抹掉，缓存若还记着旧值就会跳过写入，元素就永远失去 backdrop-filter
    // （画面静默退回 main.css 里的 blur(6px)，看着像"设置不生效"）。这个坑 2026-10-03 踩过一次。
    if (el.style.backdropFilter !== bf) {
      // 换滤镜时先落到 none、强制一次布局再写新值：只把 url(#A) 改成 url(#B) 有时不会让
      // Chromium 重新光栅化 backdrop-filter（表现同样是"设置改了但画面没变"）。
      if (el.style.backdropFilter && el.style.backdropFilter !== 'none') {
        el.style.backdropFilter = 'none';
        el.style.webkitBackdropFilter = 'none';
        void el.offsetHeight;
      }
      el.style.backdropFilter = bf;
      el.style.webkitBackdropFilter = bf;
    }
  }

  function clear(el) {
    el.style.backdropFilter = '';
    el.style.webkitBackdropFilter = '';
    el.removeAttribute('data-lg');
  }

  /// 立刻整铺一遍（尺寸变化 / 材质切换 / 改设置后调用）
  function applyAll() {
    var list = document.querySelectorAll('[data-lg]');
    for (var i = 0; i < list.length; i++) clear(list[i]);
    applied = [];
    if (!enabled()) return;
    TARGETS.forEach(function (sel) {
      var els = document.querySelectorAll(sel);
      for (var k = 0; k < els.length; k++) apply(els[k]);
    });
    refreshRects();          // 液滴判断"指针是否在玻璃上"用这份矩形
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

  // ---- 流体质感（A 指针液滴 / B 环境流动 / C 点击涟漪）----
  // 原则：**绝不每帧重画位移图**。液滴/涟漪都是"另叠一个带透镜滤镜的小元素"，只改 transform/透明度；
  // 环境流动只改既有 feOffset 的 dx/dy（和"视角跟随"共用一条通道）。所以代价和改一个属性同级。
  var FLUID = 0.5;                 // ui_liquid_fluid / 100，0 = 全关
  var AMB_X = 0, AMB_Y = 0;        // 环境流动当前偏移（px）
  var dropEl = null, dropDm = null, dropFilterDm = null, dropX = 0, dropY = 0, dropTX = 0, dropTY = 0, dropShow = 0, dropRect = false;
  var fluidRaf = 0, fluidT0 = 0, fluidFrame = 0;

  /// 径向透镜位移图：中心不动 → 中段最强 → 边缘归零（凸透镜），R/G = 沿半径朝内的位移
  /// 分辨率给到 256：128 时位移场比屏幕像素还粗，相邻像素会跳到不同采样点 → 明显的块状锯齿。
  function makeLensMap(size) {
    var key = 'lens' + size;
    if (maps[key]) return maps[key];
    var c = document.createElement('canvas'); c.width = c.height = size;
    var ctx = c.getContext('2d');
    var img = ctx.createImageData(size, size), d = img.data;
    var R = size / 2;
    for (var y = 0; y < size; y++) {
      for (var x = 0; x < size; x++) {
        var dx = (x + 0.5 - R) / R, dy = (y + 0.5 - R) / R;
        var rr = Math.sqrt(dx * dx + dy * dy), m = 0;
        if (rr > 0.002 && rr < 1) m = Math.sin(Math.PI * Math.pow(rr, 1.6));
        var cx = rr > 0.002 ? dx / rr : 0, cy = rr > 0.002 ? dy / rr : 0;
        var i = (y * size + x) * 4;
        d[i] = 128 + Math.round(Math.max(-1, Math.min(1, -cx * m)) * 127);
        d[i + 1] = 128 + Math.round(Math.max(-1, Math.min(1, -cy * m)) * 127);
        d[i + 2] = 128; d[i + 3] = 255;
      }
    }
    ctx.putImageData(img, 0, 0);
    var url = c.toDataURL('image/png');
    maps[key] = url;
    return url;
  }

  /// 造/重建一个"透镜滤镜"：sizePx 必须写**像素**（宿主 svg 是 0x0，百分比会被解析成 0 → 空图）
  function makeLensFilter(id, sizePx, scalePx) {
    var svg = svgHost();
    var old = document.getElementById(id); if (old && old.parentNode) old.parentNode.removeChild(old);
    var f = el3('filter', { id: id, x: '0', y: '0', width: '100%', height: '100%', filterUnits: 'objectBoundingBox', 'color-interpolation-filters': 'sRGB' });
    var url = makeLensMap(256);
    var im = el3('feImage', { href: url, x: '0', y: '0', width: String(sizePx), height: String(sizePx), preserveAspectRatio: 'none', result: 'm' });
    im.setAttributeNS('http://www.w3.org/1999/xlink', 'xlink:href', url);
    f.appendChild(im);
    f.appendChild(el3('feDisplacementMap', { 'in': 'SourceGraphic', in2: 'm', scale: String(scalePx), xChannelSelector: 'R', yChannelSelector: 'G' }));
    svg.appendChild(f);
    if (id === 'lg-drop-f') dropFilterDm = f.querySelector('feDisplacementMap');
    return f;
  }

  function ensureEl(id) {
    var e = document.getElementById(id);
    if (!e) { e = document.createElement('div'); e.id = id; e.setAttribute('aria-hidden', 'true'); document.body.appendChild(e); }
    return e;
  }

  /// 每个玻璃面一个"裁切容器"（overflow:hidden + 同样的圆角），液滴和涟漪都放进对应容器里。
  /// 这样它们的透镜效果**只作用在该玻璃面自己的范围里**，不会去扭曲旁边的非玻璃区域
  /// （用户报过：点击涟漪会波动到非液态玻璃面上）。
  var rects = [], clips = [], lastPX = -1, lastPY = -1;
  function refreshRects() {
    rects = []; clips = [];
    for (var i = 0; i < applied.length; i++) {
      var el = applied[i], r = el.getBoundingClientRect();
      if (r.width <= 24 || r.height <= 12) continue;
      var rad = 0, z = 0;
      try { var cs = getComputedStyle(el); rad = parseFloat(cs.borderTopLeftRadius) || 0; z = parseInt(cs.zIndex, 10) || 0; } catch (e) { rad = 0; z = 0; }
      var cid = 'lg-clip-' + clips.length;
      var c = document.getElementById(cid);
      if (!c) { c = document.createElement('div'); c.id = cid; c.className = 'lg-clip'; c.setAttribute('aria-hidden', 'true'); document.body.appendChild(c); }
      c.style.left = Math.round(r.left) + 'px';
      c.style.top = Math.round(r.top) + 'px';
      c.style.width = Math.round(r.width) + 'px';
      c.style.height = Math.round(r.height) + 'px';
      c.style.borderRadius = Math.round(rad) + 'px';
      c.style.zIndex = String(z + 1);          // 必须盖在这个玻璃面之上，否则液滴会被玻璃自己挡住
      rects.push(r); clips.push(c);
    }
    // 多余的裁切容器（元素消失/尺寸过小）收掉，别留垃圾节点
    var all = document.querySelectorAll('.lg-clip');
    for (var k = 0; k < all.length; k++) {
      var used = false;
      for (var j = 0; j < clips.length; j++) if (clips[j] === all[k]) used = true;
      if (!used && all[k].id !== 'lg-clip-keep') all[k].remove();
    }
    // ⚠️ 不要把液滴从文档里摘下来（removeChild 会让它离开 DOM，getElementById 就找不到它了）。
    // 按最后一次指针位置，把它放进"当前该在的那个裁切容器"；不在玻璃上就放回 body（反正透明度是 0）。
    if (dropEl) {
      var di = glassIndexAt(lastPX, lastPY);
      var want = di >= 0 ? clips[di] : document.body;
      if (want && dropEl.parentNode !== want) { want.appendChild(dropEl); if (di < 0) dropEl.style.opacity = '0'; }
    }
  }
  /// 指针落在第几个玻璃面上（-1 = 不在任何玻璃面上）
  function glassIndexAt(x, y) {
    for (var i = 0; i < rects.length; i++) {
      var r = rects[i];
      if (x >= r.left && x <= r.right && y >= r.top && y <= r.bottom) return i;
    }
    return -1;
  }
  function overGlass(x, y) { return glassIndexAt(x, y) >= 0; }

  /// C 点击涟漪：放进该玻璃面的裁切容器里（坐标相对容器）。
  /// 动画用 rAF 自己推进，不用 CSS transition —— 涟漪会换父节点、还要强制一次布局，
  /// 那条 transition 在某些时序下会被吞掉（用户报过"淡入淡出没了"）。
  var ripEl = null, ripT0 = 0, ripX = 0, ripY = 0, ripD = 0, ripRaf = 0;
  function rippleTick() {
    if (!ripEl) { ripRaf = 0; return; }
    var k = Math.min(1, (performance.now() - ripT0) / 620);
    var ease = 1 - Math.pow(1 - k, 3);                    // 先快后慢地铺开
    var sc = 0.35 + 1.55 * ease;
    ripEl.style.transform = 'translate(' + (ripX - ripD / 2) + 'px,' + (ripY - ripD / 2) + 'px) scale(' + sc.toFixed(3) + ')';
    ripEl.style.opacity = String(Math.max(0, (0.55 + 0.35 * FLUID) * (1 - ease)));   // 线性淡出
    if (k < 1) ripRaf = requestAnimationFrame(rippleTick);
    else { ripEl.style.opacity = '0'; ripRaf = 0; }
  }
  function spawnRipple(x, y, idx) {
    if (FLUID <= 0.02 || !enabled() || idx < 0 || !clips[idx]) return;
    var c = clips[idx], r = rects[idx];
    var d = 40 + 260 * FLUID;                       // 涟漪直径
    var scale = 3 + 4 * FLUID;                     // 透镜强度（px）
    makeLensFilter('lg-rip-f', Math.round(d), scale);
    var el = ensureEl('lg-ripple');
    if (el.parentNode !== c) c.appendChild(el);
    ripEl = el; ripD = d; ripX = x - r.left; ripY = y - r.top; ripT0 = performance.now();
    el.style.width = el.style.height = Math.round(d) + 'px';
    el.style.backdropFilter = 'url(#lg-rip-f) blur(1.2px)';
    el.style.webkitBackdropFilter = 'url(#lg-rip-f) blur(1.2px)';
    el.style.transition = 'none';
    el.style.opacity = String(0.55 + 0.35 * FLUID);  // 起始必须不透明，否则整段看不见
    el.style.transform = 'translate(' + (ripX - d / 2) + 'px,' + (ripY - d / 2) + 'px) scale(0.35)';
    if (!ripRaf) ripRaf = requestAnimationFrame(rippleTick);
  }

  /// 主循环：环境流动（B）+ 液滴跟手（A）。FLUID=0 时完全停下并把偏移归零。
  function fluidLoop() {
    fluidRaf = requestAnimationFrame(fluidLoop);
    if (!enabled() || FLUID <= 0.02) {
      if (AMB_X !== 0 || AMB_Y !== 0) { AMB_X = AMB_Y = 0; pushParallax(); }
      if (dropEl && dropShow !== 0) { dropShow = 0; dropEl.style.opacity = '0'; }
      return;
    }
    var t = (performance.now() - fluidT0) / 1000;
    var amp = 1.2 * FLUID;
    // 环境流动是"很慢的漂移"，没必要每帧都改 feOffset（每改一次都会让 backdrop-filter 重新光栅化）——
    // 隔帧更新，空闲帧时间比每帧更新低一截。
    if ((fluidFrame++ & 1) === 0) {
      AMB_X = amp * Math.sin(t * 0.53) + 0.45 * amp * Math.sin(t * 1.21 + 1.7);
      AMB_Y = amp * Math.sin(t * 0.41 + 2.1) + 0.45 * amp * Math.sin(t * 0.97);
      pushParallax();
    }
    // 每约 0.5s 校正一次裁切容器几何：页面重排（面板开合/滚动条出现）会让缓存的矩形偏掉，
    // 偏掉就会出现"裁切框和玻璃面对不齐 → 有一小条跑到外面去"。两个元素的 rect 取一次，开销可忽略。
    if (fluidFrame % 30 === 0) refreshRects();
    // 液滴：缓动跟手。**不做 scale 挤压** —— 元素一旦被 transform 放大，就是把它已经栅格化的
    // backdrop-filter 结果重新采样放大，会出现明显的块状像素（用户报过"液滴里好模糊还有像素点"）。
    // 速度改成调制透镜强度（feDisplacementMap 的 scale 属性，只重算那张小图，不放大位图）。
    var k = 0.16;
    dropX += (dropTX - dropX) * k; dropY += (dropTY - dropY) * k;
    var vx = dropTX - dropX, vy = dropTY - dropY;
    var sp = Math.sqrt(vx * vx + vy * vy);
    if ((fluidFrame & 3) === 0 && dropFilterDm) {
      var sc = (5 + 9 * FLUID) * (1 + Math.min(0.45, sp / 260));
      var sv = sc.toFixed(2);
      if (dropFilterDm.getAttribute('scale') !== sv) dropFilterDm.setAttribute('scale', sv);
    }
    if (dropEl) {
      dropShow += ((dropRect ? 1 : 0) - dropShow) * 0.18;
      var size = (70 + 60 * FLUID);
      dropEl.style.width = dropEl.style.height = Math.round(size) + 'px';
      dropEl.style.opacity = String(Math.max(0, dropShow * (0.55 + 0.45 * FLUID)));
      dropEl.style.transform = 'translate(' + Math.round(dropX - size / 2) + 'px,' + Math.round(dropY - size / 2) + 'px)';
    }
  }

  /// 给液滴/涟漪挂事件（只挂一次）
  function initFluid() {
    ensureEl('lg-drop');
    dropEl = document.getElementById('lg-drop');
    window.addEventListener('pointermove', function (e) {
      if (!enabled() || FLUID <= 0.02) { dropRect = false; return; }
      var idx = glassIndexAt(e.clientX, e.clientY);
      dropRect = idx >= 0;
      lastPX = e.clientX; lastPY = e.clientY;
      if (idx < 0) return;
      var r = rects[idx];
      dropTX = e.clientX - r.left; dropTY = e.clientY - r.top;   // 容器内坐标
      if (dropEl && clips[idx] && dropEl.parentNode !== clips[idx]) clips[idx].appendChild(dropEl);
      if (!dropRect) { dropX = dropTX; dropY = dropTY; }
    }, { passive: true });
    window.addEventListener('pointerdown', function (e) {
      if (!enabled() || FLUID <= 0.02) return;
      var idx = glassIndexAt(e.clientX, e.clientY);
      if (idx < 0) return;
      makeLensFilter('lg-drop-f', Math.round(70 + 60 * FLUID), 2.5 + 3.5 * FLUID);
      if (dropEl) { dropEl.style.backdropFilter = 'url(#lg-drop-f) blur(1.2px)'; dropEl.style.webkitBackdropFilter = 'url(#lg-drop-f)'; }
      spawnRipple(e.clientX, e.clientY, idx);
    }, { passive: true });
    // 初始化液滴滤镜（尺寸/强度随 FLUID 变，setOptions 里会重建）
    makeLensFilter('lg-drop-f', Math.round(70 + 60 * FLUID), 5 + 9 * FLUID);
    if (dropEl) { dropEl.style.backdropFilter = 'url(#lg-drop-f)'; dropEl.style.webkitBackdropFilter = 'url(#lg-drop-f)'; dropEl.style.opacity = '0'; }
    fluidT0 = performance.now();
    if (!fluidRaf) fluidRaf = requestAnimationFrame(fluidLoop);
  }

  // ---- 视角跟随（+ 环境流动共用这条通道）：鼠标位置当作"看玻璃的角度"，平移折射取样的背景（feOffset） ----
  var LX = 0, LY = 0, paraPending = false;
  function pushParallax() {
    var dx = String(Math.round((viewDx() + AMB_X) * 100) / 100), dy = String(Math.round((viewDy() + AMB_Y) * 100) / 100);
    for (var i = 0; i < order.length; i++) {
      var off = order[i].off;
      if (!off) continue;
      if (off.getAttribute('dx') !== dx) off.setAttribute('dx', dx);
      if (off.getAttribute('dy') !== dy) off.setAttribute('dy', dy);
    }
  }
  function onMove(e) {
    if (PARA <= 0 || !enabled() || paraPending) return;
    paraPending = true;
    var cx = e.clientX, cy = e.clientY;
    requestAnimationFrame(function () {
      paraPending = false;
      var nx = Math.max(-1, Math.min(1, (cx / Math.max(1, window.innerWidth)) * 2 - 1));
      var ny = Math.max(-1, Math.min(1, (cy / Math.max(1, window.innerHeight)) * 2 - 1));
      if (Math.abs(nx - LX) < 0.012 && Math.abs(ny - LY) < 0.012) return;   // 变化太小不重排
      LX = nx; LY = ny;
      pushParallax();
    });
  }
  window.addEventListener('mousemove', onMove, { passive: true });
  window.addEventListener('mouseleave', function () { LX = 0; LY = 0; pushParallax(); });

  /// 设置入口：{ power: 0~200, ca: 0~100, thickness: 0~100, parallax: 0~100 }
  /// 强度只改 feDisplacementMap 的 scale（不重画位移图）；色散变了重建滤镜链；厚度变了重画位移图
  function setOptions(opt) {
    opt = opt || {};
    if (opt.power !== undefined) {
      var p = Number(opt.power);
      if (!isFinite(p)) p = 100;
      POWER = clamp(p, 0, 200) / 100;
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
    if (opt.thickness !== undefined) {
      var th = Number(opt.thickness);
      if (!isFinite(th)) th = 60;
      var nt = clamp(th, 0, 100) / 100;
      if (Math.abs(nt - THICK) > 0.0005) {
        THICK = nt;
        maps = {};          // 位移图依赖厚度 → 丢缓存重画
        resetFilters();     // ⚠️ 滤镜里 feImage 指向的是旧的位移图 dataURL，必须连滤镜一起重建
      }
    }
    if (opt.fluid !== undefined) {
      var fl = Number(opt.fluid);
      if (!isFinite(fl)) fl = 50;
      var nf = clamp(fl, 0, 100) / 100;
      if (Math.abs(nf - FLUID) > 0.0005) {
        FLUID = nf;
        // 液滴尺寸/强度跟着变 → 重建透镜滤镜；涟漪每次点击时按当时的 FLUID 现建
        makeLensFilter('lg-drop-f', Math.round(70 + 60 * FLUID), 2.5 + 3.5 * FLUID);
        if (dropEl) {
          dropEl.style.backdropFilter = 'url(#lg-drop-f) blur(1.2px)';
          dropEl.style.webkitBackdropFilter = 'url(#lg-drop-f) blur(1.2px)';
        }
      }
    }
    if (opt.parallax !== undefined) {
      var pa = Number(opt.parallax);
      if (!isFinite(pa)) pa = 50;
      PARA = clamp(pa, 0, 100) / 100;
      pushParallax();
    }
    applyAll();
    return window.neGlassDebug();
  }

  /// 开发期自检：设置是不是真的落到了渲染上（"没报错"不算过）
  window.neGlassDebug = function () {
    var svg = document.getElementById('lg-svg');
    var els = document.querySelectorAll('[data-lg]');
    var e = edgeProfile();
    var out = {
      enabled: enabled(), power: POWER, ca: CA,
      thickness: THICK, parallax: PARA, lx: LX, ly: LY,
      view: [Math.round((viewDx() + AMB_X) * 100) / 100, Math.round((viewDy() + AMB_Y) * 100) / 100],
      fluid: FLUID,
      fluidState: {
        ambient: [Math.round(AMB_X * 100) / 100, Math.round(AMB_Y * 100) / 100],
        drop: dropEl ? {
          over: dropRect, opacity: Math.round(dropShow * 1000) / 1000,
          size: dropEl.style.width, transform: dropEl.style.transform.slice(0, 60),
          filter: (dropEl.style.backdropFilter || '').slice(0, 22)
        } : null,
        ripple: !!document.getElementById('lg-ripple'),
        lensFilter: !!document.getElementById('lg-drop-f')
      },
      scale: Math.round(scaleNow() * 10) / 10,
      edges: {
        bez: [e.bezT, e.bezB, e.bezL, e.bezR].map(function (v) { return Math.round(v * 100) / 100; }),
        gain: [e.gainT, e.gainB, e.gainL, e.gainR].map(function (v) { return Math.round(v * 1000) / 1000; })
      },
      filters: svg ? svg.querySelectorAll('filter').length : 0,
      applied: els.length, items: []
    };
    for (var j = 0; j < els.length; j++) {
      var el = els[j], cs = getComputedStyle(el);
      var f = el.getAttribute('data-lg');
      var node = f ? document.getElementById(f) : null;
      var dms = node ? node.querySelectorAll('feDisplacementMap') : [];
      var off = node ? node.querySelector('feOffset') : null;
      var scales = [];
      for (var k = 0; k < dms.length; k++) scales.push(dms[k].getAttribute('scale'));
      out.items.push({
        id: el.id || el.className,
        w: Math.round(el.getBoundingClientRect().width),
        bf: cs.backdropFilter || cs.webkitBackdropFilter,
        prims: node ? node.childNodes.length : 0,
        dmScales: scales,
        offset: off ? [off.getAttribute('dx'), off.getAttribute('dy')] : null
      });
    }
    return out;
  };

  window.neGlassSet = setOptions;
  window.neGlassPower = function (pct) { return setOptions({ power: pct }); };
  window.neGlassRefresh = refresh;
  window.addEventListener('resize', function () { if (enabled()) refresh(); });
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', function () { refresh(); initFluid(); }); else { refresh(); initFluid(); }
})();

