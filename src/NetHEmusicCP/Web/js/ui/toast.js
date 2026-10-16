// ui/toast.js —— 轻提示相关 DOM 片段（自 app.js 原样搬出）
// 这些函数不引用 app.js 主闭包内的任何状态，故可直接放在全局作用域；
// 主闭包内的调用点通过裸标识符解析到这里，行为与拆分前一致。

  // 加载图标：底轨 + 进度弧的双圆环，弧长往复伸缩同时整体匀速旋转，
  // 观感即「一根小圆条绕着圈跑」（同 Windows 11 开机动画）。
  // 动画关键帧与尺寸见 main.css 的 ._spinner_1i3xo_* / @keyframes spinner-rot|spinner-dash。
  function spinHTML(text) {
    return '<div class="win-spin-wrap">'
      + '<svg class="_spinner_1i3xo_37" data-state="ongoing" width="14" height="14" viewBox="0 0 24 24" aria-hidden="true">'
      + '<g class="_spinnerMotion_1i3xo_42">'
      + '<circle class="_spinnerTrack_1i3xo_47" cx="12" cy="12" r="9.5"></circle>'
      + '<circle class="_spinnerArc_1i3xo_48" cx="12" cy="12" r="9.5"></circle>'
      + '</g></svg>'
      + (text ? '<div class="win-spin-text">' + esc(text) + '</div>' : '') + '</div>';
  }
