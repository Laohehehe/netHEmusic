// core/boot.js — 启动期全局补丁：前端异常上报 + 全局 rAF 限到 60fps
// 自 app.js 原样迁出（原 app.js:2-27），纯移动，代码未改动一个字节。

// ---- 前端异常上报：把 JS 错误写进应用日志，便于排查（以前前端报错在日志里完全看不到）----
window.addEventListener('error', function (e) {
  try { (window.NE && NE.post) ? NE.post({ type: 'log', msg: '[jserr] ' + (e.message || '') + ' @' + (e.lineno || 0) + ':' + (e.colno || 0) + ' ' + ((e.filename || '').split('/').pop() || '') }) : 0; } catch (x) { }
}, true);
window.addEventListener('unhandledrejection', function (e) {
  try { var r = e.reason; (window.NE && NE.post) ? NE.post({ type: 'log', msg: '[jserr] promise: ' + ((r && r.message) || r) }) : 0; } catch (x) { }
});


// ---- 全局把 requestAnimationFrame 限到 60fps ----
// 高刷屏（例如 180Hz）下浏览器按显示器刷新率出帧，界面里的 JS 动画没必要跑 180fps
(function () {
  var orig = window.requestAnimationFrame ? window.requestAnimationFrame.bind(window) : null;
  if (!orig) return;
  var MIN = 1000 / 62;
  window.requestAnimationFrame = function (cb) {
    var last = 0;                       // 每条调用链各自计时，互不抢额度
    function step(ts) {
      var now = performance.now();
      if (now - last < MIN) { orig(step); return; }
      last = now;
      cb(ts);
    }
    return orig(step);
  };
})();
