// ui/icons.js —— 统一图标集（自 app.js 原样搬出）
// 这些函数不引用 app.js 主闭包内的任何状态，故可直接放在全局作用域；
// 主闭包内的调用点通过裸标识符解析到这里，行为与拆分前一致。

  // ===== 统一图标集：全部描边风格（stroke 2 / round 端点），同一功能只用同一个图标 =====
  function ico(inner) {
    return '<svg viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" '
      + 'stroke-width="2" stroke-linecap="round" stroke-linejoin="round">' + inner + '</svg>';
  }

  // 品牌标（GitHub / Gitee）是实心填充图形，不能用描边那套
  function icoFill(inner) {
    return '<svg viewBox="0 0 24 24" aria-hidden="true" fill="currentColor" stroke="none">' + inner + '</svg>';
  }
