// media/audio.js —— 音频定位（自 app.js 原样搬出）
// 这些函数不引用 app.js 主闭包内的任何状态，故可直接放在全局作用域；
// 主闭包内的调用点通过裸标识符解析到这里，行为与拆分前一致。

  // 续播定位：el.load() 之后 metadata 未就绪时直接写 currentTime 会被丢弃 → 补一个一次性监听
  function auSeekTo(p, ms) {
    if (!p || !(ms > 0)) return;
    var set = function () { try { p.el.currentTime = ms / 1000; } catch (e) { } };
    set();
    if (!(p.el.readyState >= 1)) {
      var once = function () { p.el.removeEventListener('loadedmetadata', once); set(); };
      p.el.addEventListener('loadedmetadata', once);
    }
  }
