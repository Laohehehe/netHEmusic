// lyrics/nowplaying.js —— 播放页歌词/封面渲染（自 app.js 原样搬出）
// 这些函数不引用 app.js 主闭包内的任何状态，故可直接放在全局作用域；
// 主闭包内的调用点通过裸标识符解析到这里，行为与拆分前一致。

  function npEl(id) { return document.getElementById(id); }

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
