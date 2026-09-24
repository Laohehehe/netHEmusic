// Hello 插件（示例）—— 演示插件 API 的基本用法
nethe.log('示例插件已加载，权限: ' + nethe.permissions.join(','));

// UI：往搜索框下面加一条提示带（需要 ui 权限）
nethe.css.add('.hello-tip{position:fixed;left:50%;transform:translateX(-50%);bottom:96px;padding:4px 12px;border-radius:20px;background:rgba(102,204,255,.16);color:#66ccff;font-size:12px;pointer-events:none;opacity:0;transition:opacity .3s}');
var tip = nethe.dom.add(document.body, 'div', 'hello-tip', '');
var style = document.createElement('style');
style.textContent = '.hello-tip.show{opacity:1}';
document.head.appendChild(style);

var count = 0;

// 事件：切歌（需要 events 权限）
nethe.on('track', function (song) {
  count++;
  tip.textContent = '第 ' + count + ' 首：' + (song.Title || '') + ' - ' + (song.Artist || '');
  tip.classList.add('show');
  clearTimeout(tip._t);
  tip._t = setTimeout(function () { tip.classList.remove('show'); }, 3000);
  nethe.data.write('count.txt', String(count));
});

// 私有数据：恢复计数（需要 filesystem 权限）
nethe.data.read('count.txt').then(function (t) {
  count = parseInt(t || '0', 10) || 0;
  nethe.log('历史切歌次数: ' + count);
}).catch(function (e) { nethe.log('读数据失败: ' + e.message); });

// 设置项（需要 settings 权限）
nethe.settings.add({ key: 'hello_tip', label: 'Hello 插件的提示带', type: 'switch' });
