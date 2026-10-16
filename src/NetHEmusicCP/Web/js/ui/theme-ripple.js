// ui/theme-ripple.js —— 主题切换水波（自 app.js 原样搬出）
// 这些函数不引用 app.js 主闭包内的任何状态，故可直接放在全局作用域；
// 主闭包内的调用点通过裸标识符解析到这里，行为与拆分前一致。

  function rippleRadius(x, y) {
    var dx = Math.max(x, window.innerWidth - x), dy = Math.max(y, window.innerHeight - y);
    return Math.ceil(Math.sqrt(dx * dx + dy * dy)) + 32;
  }

  function rippleRing(x, y) {
    var r = rippleRadius(x, y);
    var d = document.createElement('div');
    d.className = 'nm-ripple-ring';
    d.style.width = d.style.height = (r * 2) + 'px';
    d.style.left = x + 'px'; d.style.top = y + 'px';
    document.body.appendChild(d);
    setTimeout(function () { if (d.parentNode) d.parentNode.removeChild(d); }, 900);
  }
