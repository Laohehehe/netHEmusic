// core/dom.js — 与宿主渲染无关的 DOM 小工具（滑块填充进度 + 音量气泡）
// 自 app.js 原样迁出（原 app.js:29-87），纯移动，代码未改动一个字节。
// 注意：这两段都自执行（挂监听 + 立即 boot）；syncRangeFill 仍导出到 window.neSyncRangeFill。

(function () {
  // ---- 滑块「已填充」进度：轨道由 CSS 自绘（见 main.css），Chromium 自绘后不再画填充段，
  //      这里把进度写进 --range-p，任何 input[type=range]（含动态渲染出来的）都自动生效。
  function syncRangeFill(i) {
    var mn = Number(i.min === '' || i.min === undefined ? 0 : i.min);
    var mx = Number(i.max === '' || i.max === undefined ? 100 : i.max);
    var v = Number(i.value);
    if (!isFinite(mn)) mn = 0; if (!isFinite(mx)) mx = 100; if (!isFinite(v)) v = mn;
    var p = mx > mn ? Math.max(0, Math.min(100, (v - mn) / (mx - mn) * 100)) : 0;
    i.style.setProperty('--range-p', p + '%');
  }
  function syncRangeFillAll() {
    var l = document.querySelectorAll('input[type=range]');
    for (var k = 0; k < l.length; k++) syncRangeFill(l[k]);
  }
  window.neSyncRangeFill = syncRangeFill;
  document.addEventListener('input', function (e) { if (e.target && e.target.type === 'range') syncRangeFill(e.target); }, true);
  document.addEventListener('change', function (e) { if (e.target && e.target.type === 'range') syncRangeFill(e.target); }, true);
  var pending = false;
  function queue() {
    if (pending) return; pending = true;
      requestAnimationFrame(function () {
        pending = false;
        try { syncRangeFillAll(); } catch (x) { }
        // 任何 DOM 变动后都补一次离线置灰：列表渲染路径很多，靠每条路径自己记得调用太脆（实测主页列表没灰、面板却灰了）
        try { if (window.neOfflineMark) window.neOfflineMark(); } catch (x) { }
      });
  }
  function boot() {
    queue();
    try { new MutationObserver(queue).observe(document.body, { childList: true, subtree: true }); } catch (x) { }
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot); else boot();
})();

(function () {
  // ---- 音量滑块：鼠标放上去/拖动时显示当前具体音量 ----
  var vol = document.getElementById('pl-volume');
  if (!vol) return;
  var tip = document.createElement('div');
  tip.className = 'vol-tip';
  document.body.appendChild(tip);
  function pct() { var v = Math.round(Number(vol.value)); return isFinite(v) ? v : 0; }
  function place() {
    var r = vol.getBoundingClientRect();
    if (!r.width) return;
    var p = Math.max(0, Math.min(1, pct() / 100));
    tip.style.left = Math.round(r.left + 7 + (r.width - 14) * p) + 'px';
    tip.style.top = Math.round(r.top - 8) + 'px';
    tip.textContent = '音量 ' + pct() + '%';
  }
  function show() { place(); tip.classList.add('on'); }
  function hide() { tip.classList.remove('on'); }
  vol.addEventListener('mouseenter', show);
  vol.addEventListener('mousemove', place);
  vol.addEventListener('mouseleave', hide);
  vol.addEventListener('input', function () { if (tip.classList.contains('on')) place(); });
  vol.addEventListener('change', function () { if (tip.classList.contains('on')) place(); });
})();

// ---- 以下为自 app.js 搬出的纯函数（无闭包依赖）----

  function el(tag, cls, html) { const e = document.createElement(tag); if (cls) e.className = cls; if (html !== undefined) e.innerHTML = html; return e; }

  function fmt(ms) { if (!ms || ms <= 0) return '00:00'; const s = Math.floor(ms/1000); return String(Math.floor(s/60)).padStart(2,'0')+':'+String(s%60).padStart(2,'0'); }

  function esc(s){ return String(s||'').replace(/[&<>"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c])); }

  // 没有封面时不要输出 src="" —— 空 src 会让浏览器去请求页面自己(index.html)，控制台/日志里全是无谓的加载失败
  function imgSrc(u, param) { u = String(u || '').replace(/\^\d+\^/, ''); return u ? ' src="' + esc(u + (param || '')) + '"' : ''; }

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
      AlbumId: al.id !== undefined ? al.id : (s.AlbumId || 0),
      Pic: pic,
      Duration: s.dt || s.duration || s.Duration || 0
    };
  }
