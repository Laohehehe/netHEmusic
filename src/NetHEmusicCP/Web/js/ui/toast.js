// ui/toast.js —— 轻提示相关 DOM 片段（自 app.js 原样搬出）
// 这些函数不引用 app.js 主闭包内的任何状态，故可直接放在全局作用域；
// 主闭包内的调用点通过裸标识符解析到这里，行为与拆分前一致。

  // Windows 11 风格的小圆圈加载动画（纯 SVG + CSS，动画定义见 main.css 的 .win-spin）
  function spinHTML(text) {
    var dots = '';
    for (var i = 0; i < 8; i++) dots += '<circle cx="24" cy="7.5" r="3.1" style="--i:' + i + '"></circle>';
    return '<div class="win-spin-wrap"><svg class="win-spin" viewBox="0 0 48 48" aria-hidden="true">' + dots + '</svg>'
      + (text ? '<div class="win-spin-text">' + esc(text) + '</div>' : '') + '</div>';
  }
