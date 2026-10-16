// pages/downloads.js —— 下载页工具（自 app.js 原样搬出）
// 这些函数不引用 app.js 主闭包内的任何状态，故可直接放在全局作用域；
// 主闭包内的调用点通过裸标识符解析到这里，行为与拆分前一致。

  function fmtSize(b) {
    b = Number(b) || 0;
    if (b >= 1073741824) return (b / 1073741824).toFixed(2) + ' GB';
    if (b >= 1048576) return (b / 1048576).toFixed(1) + ' MB';
    if (b >= 1024) return Math.round(b / 1024) + ' KB';
    return b + ' B';
  }

  function dlPic(p) { return p ? p.replace(/\^\d+\^/, '') + '?param=80y80' : ''; }

  function dlStatusText(t) {
    switch (t.status) {
      case 'queued': return '等待下载…';
      case 'downloading': return '正在下载 ' + Math.round((t.progress || 0) * 100) + '%';
      case 'paused': return t.done > 0 ? '已暂停（已下 ' + fmtSize(t.done) + '）' : '已暂停';
      case 'done': return '已完成';
      case 'failed': return '失败：' + (t.error || '未知原因');
      default: return t.status || '';
    }
  }
