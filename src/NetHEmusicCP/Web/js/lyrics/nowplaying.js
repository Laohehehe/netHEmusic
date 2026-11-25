// lyrics/nowplaying.js —— 播放页歌词/封面渲染（自 app.js 原样搬出）
// 这些函数不引用 app.js 主闭包内的任何状态，故可直接放在全局作用域；
// 主闭包内的调用点通过裸标识符解析到这里，行为与拆分前一致。

  function npEl(id) { return document.getElementById(id); }

  // ================= 歌词页背景：三态（歌词封面 / 纯色 / 动态） =================
  // 纯色/动态需要从当前封面取 4 个主色（k-means 聚类），动态再叠一层慢速流动动画。
  var NP_BG_STYLE = 'cover';                 // cover | color | fluid
  var NP_BG_PIC = '';                        // 当前封面原图 URL（不带 param，取色时再拼缩略参数）
  var NP_BG_SMALL = '';                      // 当前封面 240px 缩略图（cover 模式直接用作背景）
  var npPaletteCache = {};                   // url -> [[r,g,b] x4]
  var npPaletteWait = {};                    // url -> [resolve...]
  var NP_FLUID_SLOW_S = 36;                  // 流动速度 0 → 36s 一个来回
  var NP_FLUID_FAST_S = 7;                   // 流动速度 100 → 7s 一个来回

  function npSetBgStyle(style, ease, speed) {
    NP_BG_STYLE = (style === 'color' || style === 'fluid') ? style : 'cover';
    var b = npEl('np-bg'); if (!b) return;
    b.classList.toggle('bg-cover', NP_BG_STYLE === 'cover');
    b.classList.toggle('bg-color', NP_BG_STYLE === 'color');
    b.classList.toggle('bg-fluid', NP_BG_STYLE === 'fluid');
    // 流动速度 = 滑块(0~100) 决定基础周期，再乘动画曲线档位系数
    var sp = Math.max(0, Math.min(100, Number(speed === undefined ? 40 : speed) || 0));
    var base = NP_FLUID_SLOW_S - (sp / 100) * (NP_FLUID_SLOW_S - NP_FLUID_FAST_S);   // 慢 36s ~ 快 7s
    var k = ({ smooth: 1, sharp: 0.8, gentle: 1.3, easeout: 1.1 })[ease] || 1;
    b.style.setProperty('--np-fluid-dur', (base * k).toFixed(1) + 's');
    if (NP_BG_STYLE === 'cover') {
      if (NP_BG_SMALL) b.style.backgroundImage = 'url("' + NP_BG_SMALL + '")';
    } else {
      b.style.backgroundImage = '';   // 清空内联，交给 .bg-color/.bg-fluid 的 CSS 渐变
      npBgPaletteApply(NP_BG_PIC);
    }
  }

  // ---- 取色：封面 → 48x48 画布 → k-means(k=4) ----
  function npExtractPalette(url) {
    return new Promise(function (resolve) {
      if (!url) return resolve(null);
      if (npPaletteCache[url]) return resolve(npPaletteCache[url]);
      var list = npPaletteWait[url] = npPaletteWait[url] || [];
      list.push(resolve);
      if (list.length > 1) return;    // 同一张图只发一次请求
      var img = new Image();
      img.crossOrigin = 'anonymous';
      img.onload = function () {
        var pal = null;
        try { pal = npKMeans(img); } catch (e) { }
        if (pal) npPaletteCache[url] = pal;
        var arr = npPaletteWait[url] || []; delete npPaletteWait[url];
        arr.forEach(function (fn) { try { fn(pal); } catch (e2) { } });
      };
      img.onerror = function () {
        var arr = npPaletteWait[url] || []; delete npPaletteWait[url];
        arr.forEach(function (fn) { try { fn(null); } catch (e3) { } });
      };
      img.src = url;
    });
  }

  function npKMeans(img) {
    var W = 48, H = 48;
    var c = document.createElement('canvas'); c.width = W; c.height = H;
    var g = c.getContext('2d');
    g.drawImage(img, 0, 0, W, H);
    var px = g.getImageData(0, 0, W, H).data;
    var pts = [];
    for (var i = 0; i < px.length; i += 4) {
      var r = px[i], gg = px[i + 1], b = px[i + 2], a = px[i + 3];
      if (a < 200) continue;
      var mx = Math.max(r, gg, b), mn = Math.min(r, gg, b);
      if (mn > 240) continue;                 // 纯白
      if (mx < 12) continue;                  // 纯黑
      pts.push([r, gg, b]);
    }
    if (pts.length < 8) return null;
    var K = 4, centers = [];
    for (var n = 0; n < K; n++) centers.push(pts[Math.floor(pts.length * (n + 0.5) / K)].slice());
    for (var iter = 0; iter < 10; iter++) {
      var sums = [], cnt = [];
      for (var j = 0; j < K; j++) { sums.push([0, 0, 0]); cnt.push(0); }
      for (var p = 0; p < pts.length; p++) {
        var pt = pts[p], bi = 0, bd = Infinity;
        for (var j2 = 0; j2 < K; j2++) {
          var dr = pt[0] - centers[j2][0], dg = pt[1] - centers[j2][1], db = pt[2] - centers[j2][2];
          var d = dr * dr + dg * dg + db * db;
          if (d < bd) { bd = d; bi = j2; }
        }
        sums[bi][0] += pt[0]; sums[bi][1] += pt[1]; sums[bi][2] += pt[2]; cnt[bi]++;
      }
      for (var j3 = 0; j3 < K; j3++) {
        if (!cnt[j3]) continue;
        centers[j3] = [sums[j3][0] / cnt[j3], sums[j3][1] / cnt[j3], sums[j3][2] / cnt[j3]];
      }
    }
    return centers.map(npBgBoost);
  }

  // 背景色要更「有颜色」：提饱和度、修亮度，避免灰扑扑
  function npBgBoost(c) {
    var r = c[0] / 255, g = c[1] / 255, b = c[2] / 255;
    var mx = Math.max(r, g, b), mn = Math.min(r, g, b), l = (mx + mn) / 2;
    var s = mx === mn ? 0 : (l > .5 ? (mx - mn) / (2 - mx - mn) : (mx - mn) / (mx + mn));
    s = Math.min(1, s * 1.35 + 0.05);
    l = Math.min(.72, Math.max(.2, l * 1.02));
    var h = 0;
    if (mx !== mn) {
      if (mx === r) h = ((g - b) / (mx - mn)) % 6;
      else if (mx === g) h = (b - r) / (mx - mn) + 2;
      else h = (r - g) / (mx - mn) + 4;
      h *= 60; if (h < 0) h += 360;
    }
    var q = l < .5 ? l * (1 + s) : l + s - l * s, p2 = 2 * l - q;
    function hue2rgb(pp, qq, t) {
      if (t < 0) t += 1; if (t > 1) t -= 1;
      if (t < 1 / 6) return pp + (qq - pp) * 6 * t;
      if (t < 1 / 2) return qq;
      if (t < 2 / 3) return pp + (qq - pp) * (2 / 3 - t) * 6;
      return pp;
    }
    var hh = h / 360;
    return [hue2rgb(p2, q, hh + 1 / 3) * 255, hue2rgb(p2, q, hh) * 255, hue2rgb(p2, q, hh - 1 / 3) * 255];
  }

  function npBgPaletteApply(pic) {
    var b = npEl('np-bg'); if (!b) return;
    if (NP_BG_STYLE === 'cover') return;
    if (!pic) { npBgPaletteFallback(); return; }
    if (npPaletteCache[pic]) { npBgPaletteSet(npPaletteCache[pic]); return; }
    // C# 下载封面并取色（WebView 里 canvas 读跨域封面时好时坏），结果走 cover_palette 回传
    try { NE.post({ type: 'cover_palette', url: pic }); } catch (e) { npBgPaletteFallback(); }
  }
  function npBgPaletteSet(cols) {
    var b = npEl('np-bg'); if (!b) return;
    if (NP_BG_STYLE === 'cover') return;
    if (!cols || !cols.length) { npBgPaletteFallback(); return; }
    var arr = cols.map(function (c) { return [c.r, c.g, c.b]; });
    arr = npSpreadColors(arr);          // 单色封面时把 4 个主色拉开，否则背景糊成一片
    for (var i = 0; i < 4; i++) {
      var cc = arr[i] || arr[0];
      b.style.setProperty('--np-c' + (i + 1), 'rgb(' + Math.round(cc[0]) + ',' + Math.round(cc[1]) + ',' + Math.round(cc[2]) + ')');
    }
  }

  // 4 个主色色相太接近时（纯色/单色封面常见），按固定偏移拉开层次
  function npSpreadColors(cols) {
    if (!cols || cols.length < 2) return cols;
    var base = cols[0];
    var baseHue = npHue(base);
    var offsets = [0, 26, -30, 54];
    var out = [base.slice()];
    for (var i = 1; i < 4; i++) {
      var src = cols[i] || base;
      var diff = Math.abs(((npHue(src) - baseHue) % 360 + 360) % 360);
      var near = diff < 22 || diff > 338;
      out.push(near ? npHueShift(src, offsets[i]) : src.slice());
    }
    return out;
  }
  function npHue(rgb) {
    var r = rgb[0] / 255, g = rgb[1] / 255, b = rgb[2] / 255;
    var mx = Math.max(r, g, b), mn = Math.min(r, g, b), d = mx - mn;
    if (!d) return 0;
    var h;
    if (mx === r) h = ((g - b) / d) % 6;
    else if (mx === g) h = (b - r) / d + 2;
    else h = (r - g) / d + 4;
    h *= 60;
    return h < 0 ? h + 360 : h;
  }
  function npHueShift(rgb, deg) {
    var rad = deg * Math.PI / 180, cos = Math.cos(rad), sin = Math.sin(rad);
    var r = rgb[0], g = rgb[1], b = rgb[2];
    var m00 = 0.213 + cos * 0.787 - sin * 0.213, m01 = 0.715 - cos * 0.715 - sin * 0.715, m02 = 0.072 - cos * 0.072 + sin * 0.928;
    var m10 = 0.213 - cos * 0.213 + sin * 0.143, m11 = 0.715 + cos * 0.285 + sin * 0.140, m12 = 0.072 - cos * 0.072 - sin * 0.283;
    var m20 = 0.213 - cos * 0.213 - sin * 0.787, m21 = 0.715 - cos * 0.715 + sin * 0.715, m22 = 0.072 + cos * 0.928 + sin * 0.072;
    return [
      Math.max(0, Math.min(255, m00 * r + m01 * g + m02 * b)),
      Math.max(0, Math.min(255, m10 * r + m11 * g + m12 * b)),
      Math.max(0, Math.min(255, m20 * r + m21 * g + m22 * b))
    ];
  }
  function npBgPaletteFallback() {
    var b = npEl('np-bg'); if (!b) return;
    var acc = (getComputedStyle(document.documentElement).getPropertyValue('--md-accent-color') || '#5b6ee1').trim();
    for (var i = 0; i < 4; i++) b.style.setProperty('--np-c' + (i + 1), acc);
  }
  try {
    NE.on('cover_palette', function (d) {
      if (!d || !d.url) return;
      if (d.colors && d.colors.length) npPaletteCache[d.url] = d.colors;
      if (d.url === NP_BG_PIC && NP_BG_STYLE !== 'cover') npBgPaletteSet(d.colors);
    });
  } catch (eCp) { }

  function npRenderSong(ns) {
    if (!ns) return;
    var cover = npEl('np-cover'), bg = npEl('np-bg');
    var wrap = document.querySelector('#np .np-cover-wrap');
    var pic = ns.Pic ? ns.Pic.replace(/\^\d+\^/, '') : '';
    if (pic) {
      // 封面只用于 300px 上下的展示，取 400y400 缩略图即可：原来直接用 2400x2400 原图，
      // 单张解码就要 ~23MB，是“内存只涨不降”的一大来源
      var q = pic.indexOf('?') >= 0 ? '&' : '?';
      cover.src = pic + q + 'param=400y400';
      // 背景用 240px 缩略图放大 —— 比整屏 72px 高斯模糊便宜一个数量级
      var small = pic + q + 'param=240y240';
      NP_BG_PIC = pic;
      NP_BG_SMALL = small;
      if (NP_BG_STYLE === 'cover') {
        bg.style.backgroundImage = 'url("' + small + '")';
      } else {
        // 纯色 / 动态：背景不再是封面图，而是从封面取出的 4 主色渐变。
        // 注意这里要用空串清掉内联样式，否则内联的 background-image 会压过 CSS 类的渐变
        bg.style.backgroundImage = '';
        npBgPaletteApply(pic);
      }
    }
    else { cover.removeAttribute('src'); bg.style.backgroundImage = 'none'; NP_BG_PIC = ''; NP_BG_SMALL = ''; }
    // 有封面时隐藏占位音符（原来用 img ~ i 兄弟选择器，但 <i> 在 <img> 前面，根本没生效）
    if (wrap) wrap.classList.toggle('has-cover', !!pic);
    npEl('np-title').textContent = ns.Title || '';
    npEl('np-artist').textContent = ns.Artist || '';
    npEl('np-album').textContent = ns.Album || '';
  }

  function npBarHide() {
    var b = document.querySelector('#np .np-bar'); if (b) b.classList.add('bar-away');
    // 齿轮（歌词显示设置）跟着控制条一起收起（#np 上的 bar-away 只作用于齿轮）；
    // 设置面板开着时不收，否则就没法再点齿轮把它收起来了
    var s = document.getElementById('np-settings');
    if (s && s.classList.contains('show')) return;
    var n = document.getElementById('np'); if (n) n.classList.add('bar-away');
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
    // 过渡曲线在 CSS（.np-fly-in）：这里只负责 FLIP 起始帧。
    // 用 class 开关过渡、不再写内联 transition，避免覆盖设置里的动画速率（--anim-k）。
    wrap.classList.remove('np-fly-out', 'np-no-trans');
    wrap.classList.add('np-no-trans');
    wrap.style.transform = 'translate(' + dx + 'px,' + dy + 'px) scale(' + s + ')';
    wrap.style.opacity = '.85';
    void wrap.offsetWidth;                       // 强制回流，保证起始帧生效
    requestAnimationFrame(function () {
      wrap.classList.remove('np-no-trans');
      wrap.classList.add('np-fly-in');
      wrap.style.transform = 'none';
      wrap.style.opacity = '1';
    });
  }
