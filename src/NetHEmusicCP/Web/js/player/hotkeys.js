// player/hotkeys.js —— 快捷键按键名/输入态判断（自 app.js 原样搬出）
// 这些函数不引用 app.js 主闭包内的任何状态，故可直接放在全局作用域；
// 主闭包内的调用点通过裸标识符解析到这里，行为与拆分前一致。

  function evKeyName(e) {
    var p = [];
    if (e.ctrlKey) p.push('Ctrl');
    if (e.altKey) p.push('Alt');
    if (e.shiftKey) p.push('Shift');
    var code = e.code || '';
    if (!code) {                                   // 某些输入源不带 code，退回到 key
      var k = e.key || '';
      if (k === ' ' || k === 'Spacebar') code = 'Space';
      else if (k.length === 1) code = k.toUpperCase();
      else code = k;
    }
    if (code.indexOf('Key') === 0) code = code.slice(3);
    else if (code.indexOf('Digit') === 0) code = code.slice(5);
    // 单独的修饰键不作为快捷键
    if (!code || code === 'Control' || code === 'Alt' || code === 'Shift' || code === 'Meta' ||
        code === 'ControlLeft' || code === 'ControlRight' || code === 'AltLeft' || code === 'AltRight' ||
        code === 'ShiftLeft' || code === 'ShiftRight') return '';
    p.push(code);
    return p.join('+');
  }

  function isTyping(el) {
    return !!el && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.tagName === 'SELECT' || el.isContentEditable);
  }
