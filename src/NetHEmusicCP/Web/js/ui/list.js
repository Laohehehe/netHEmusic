// ui/list.js —— 列表跑马灯工具（自 app.js 原样搬出）
// 这些函数不引用 app.js 主闭包内的任何状态，故可直接放在全局作用域；
// 主闭包内的调用点通过裸标识符解析到这里，行为与拆分前一致。

  function mqCells(scope) { return scope ? scope.querySelectorAll('.sr-title,.sr-artist,.sr-album,#pl-title,#pl-artist') : []; }

  // 量文字的真实宽度：拿同 class 的空元素塞进同一个父级（继承同样的字体/变量），
  // 绝对定位 + width:auto → 收缩到内容宽度，用 getBoundingClientRect 取亚像素精度。
  // （不能用 scrollWidth：ellipsis 元素在 Chromium 上会报成 clientWidth）
  function mqTextWidth(c, txt) {
    var clone = c.cloneNode(false);
    clone.textContent = txt;
    clone.removeAttribute('data-mq-text');
    clone.style.cssText = 'position:absolute;left:-99999px;top:0;width:auto;max-width:none;min-width:0;flex:none;visibility:hidden;pointer-events:none;';
    (c.parentNode || document.body).appendChild(clone);
    var w = clone.getBoundingClientRect().width;
    clone.parentNode.removeChild(clone);
    return w;
  }

  function mqStop(row) {
    if (!row) return;
    Array.prototype.forEach.call(mqCells(row), function (c) {
      if (!c.querySelector('.mq-track')) return;
      c.classList.remove('sr-mq');
      c.textContent = c.dataset.mqText || '';
    });
    row.dataset.mqOn = '';
  }
