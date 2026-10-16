// ui/theme-accent.js —— 主题色计算（自 app.js 原样搬出）
// 这些函数不引用 app.js 主闭包内的任何状态，故可直接放在全局作用域；
// 主闭包内的调用点通过裸标识符解析到这里，行为与拆分前一致。

  function shade(rgb, k) { return "rgb(" + rgb.map(function (n) { return Math.round(n * k); }).join(",") + ")"; }

  function boostColor(r, g, b) {
    var mx = Math.max(r, g, b), mn = Math.min(r, g, b);
    var l = (mx + mn) / 2 / 255;
    var s = mx === mn ? 0 : (mx - mn) / (l > .5 ? (510 - mx - mn) : (mx + mn));
    s = Math.min(1, s * 1.45 + .12);                 // 提高饱和度，避免灰扑扑
    l = Math.min(.82, Math.max(.52, l * .95 + .18)); // 压到中亮度，深色底上更清楚
    function hue2rgb(p2, q2, t) { if (t < 0) t += 1; if (t > 1) t -= 1; if (t < 1/6) return p2 + (q2 - p2) * 6 * t; if (t < 1/2) return q2; if (t < 2/3) return p2 + (q2 - p2) * (2/3 - t) * 6; return p2; }
    var r2, g2, b2;
    if (s === 0) { r2 = g2 = b2 = l; }
    else {
      var q = l < .5 ? l * (1 + s) : l + s - l * s, p2 = 2 * l - q;
      var h = 0;
      var rr = r, gg = g, bb = b;                      // 统一用 0~255，别和归一化混用
      if (mx === r) h = (gg - bb) / (mx - mn);
      else if (mx === g) h = 2 + (bb - rr) / (mx - mn);
      else h = 4 + (rr - gg) / (mx - mn);
      h = (h / 6 + 1) % 1;
      r2 = hue2rgb(p2, q, h + 1/3); g2 = hue2rgb(p2, q, h); b2 = hue2rgb(p2, q, h - 1/3);
    }
    return "#" + [r2, g2, b2].map(function (n) { return ("0" + Math.round(n * 255).toString(16)).slice(-2); }).join("");
  }
