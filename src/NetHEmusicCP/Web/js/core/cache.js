// core/cache.js —— 缓存窗口计算（自 app.js 原样搬出）
// 这些函数不引用 app.js 主闭包内的任何状态，故可直接放在全局作用域；
// 主闭包内的调用点通过裸标识符解析到这里，行为与拆分前一致。

  /** 网易云「每日推荐」的刷新边界：每天早上 6:00。当前若还没到 6:00，则边界是昨天 6:00。 */
  function dailyBoundary(nowMs) {
    var d = new Date(nowMs || Date.now());
    if (d.getHours() < 6) d.setDate(d.getDate() - 1);
    d.setHours(6, 0, 0, 0);
    return d.getTime();
  }
