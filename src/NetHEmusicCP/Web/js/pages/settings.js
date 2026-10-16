// pages/settings.js —— 设置页：应用字体（自 app.js 原样搬出）
// 这些函数不引用 app.js 主闭包内的任何状态，故可直接放在全局作用域；
// 主闭包内的调用点通过裸标识符解析到这里，行为与拆分前一致。

  // ---------- 设置（内嵌主窗口的网页设置页） ----------
  // 界面字体：把选中的字体写进 CSS 变量 --app-font，空值就撤掉变量用默认字体
  function applyAppFont(name) {
    try {
      var root = document.documentElement, v = String(name || '').trim();
      if (v) root.style.setProperty('--app-font', '"' + v.replace(/["\\]/g, '') + '"');
      else root.style.removeProperty('--app-font');
    } catch (e) { }
  }
