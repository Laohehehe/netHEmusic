// lyrics/render.js —— 歌词行缩放/透明度曲线（自 app.js 原样搬出）
// 这些函数不引用 app.js 主闭包内的任何状态，故可直接放在全局作用域；
// 主闭包内的调用点通过裸标识符解析到这里，行为与拆分前一致。

  // ===== 歌词排版引擎（照 refined-now-playing-netease 的思路重写）=====
  // 每行绝对定位；按与当前行的“行距 offset”分别算 缩放/模糊/透明度 与位置，
  // 旋转时把各行放到以左侧远处为圆心的大圆弧上（而不是简单地按行号叠加角度）。
  function lyScale(off) {
    var t = Math.max(1 - Math.abs(off) * 0.2, 0);
    return t * t * t * 0.3 + 0.7;          // 0→1.0  ±1→0.854  ±2→0.765  ±5 以外→0.7
  }

  function lyOpacity(off) {
    var a = Math.abs(off);
    if (a <= 1) return 1;                  // 相邻行保持清晰
    return Math.max(1 - 0.4 * (a - 1), 0);
  }
