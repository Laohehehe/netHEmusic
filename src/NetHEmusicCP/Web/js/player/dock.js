// player/dock.js —— 底部播放条小工具（自 app.js 原样搬出）
// 这些函数不引用 app.js 主闭包内的任何状态，故可直接放在全局作用域；
// 主闭包内的调用点通过裸标识符解析到这里，行为与拆分前一致。

  function pop(el) { if(!el) return; el.classList.remove('fx-pop'); void el.offsetWidth; el.classList.add('fx-pop'); }

  // 给插件用的事件广播（宿主不在就什么也不做）
  function plugEmit(evt, payload) { try { if (window.PLUGHOST) window.PLUGHOST.emit(evt, payload); } catch (e) { } }

  function qualClose() { var m = document.getElementById("qual-menu"); if (m) m.classList.remove("show"); }
