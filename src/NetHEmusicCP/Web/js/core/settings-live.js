// core/settings-live.js —— 设置读取 + 液态玻璃参数（自 app.js 原样搬出）
// 这些函数不引用 app.js 主闭包内的任何状态，故可直接放在全局作用域；
// 主闭包内的调用点通过裸标识符解析到这里，行为与拆分前一致。

function cfgGet(s, k, def) { var v = (s && s.app) ? s.app[k] : undefined; return (v === undefined || v === null || v === '') ? def : v; }

  function cfgBool(s, k, def) { var v = cfgGet(s, k, def); return String(v) !== 'false' && v !== false; }

  // ---- 液态玻璃开关 + 强度（顶部标题条已随原生标题栏一起取消，不再有标题栏设置项）----
  function glassPowerValue(s) {
    var v = Number(cfgGet(s, 'ui_liquid_power', 100));
    if (!isFinite(v)) v = 100;             // 0 是合法值（= 不折射），不能被 || 吃掉
    return Math.max(0, Math.min(200, v));
  }

  function glassCaValue(s) {
    var v = Number(cfgGet(s, 'ui_liquid_ca', 50));
    if (!isFinite(v)) v = 50;              // 0 也是合法值（= 无色散）
    return Math.max(0, Math.min(100, v));
  }

  function glassThickValue(s) {
    var v = Number(cfgGet(s, 'ui_liquid_thick', 60));
    if (!isFinite(v)) v = 60;              // 0 = 四周一样厚（改造前原样）
    return Math.max(0, Math.min(100, v));
  }

  function glassParaValue(s) {
    var v = Number(cfgGet(s, 'ui_liquid_para', 50));
    if (!isFinite(v)) v = 50;              // 0 = 光带不跟随鼠标
    return Math.max(0, Math.min(100, v));
  }

  function glassFluidValue(s) {
    var v = Number(cfgGet(s, 'ui_liquid_fluid', 50));
    if (!isFinite(v)) v = 50;              // 0 = 关掉流体质感（液滴/环境流动/涟漪）
    return Math.max(0, Math.min(100, v));
  }
