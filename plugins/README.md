# netHEmusic 插件开发文档

> 本文档面向想给 netHEmusic 写插件的人（也方便 AI 直接读）。
> 插件是**纯 JavaScript**，不需要编译、不需要改本软件源码。

---

## 1. 插件是什么

插件就是一个文件夹，放进插件目录后，netHEmusic 会在页面启动时**把它的 JS 注入到界面里执行**，
从而修改界面、监听播放事件、加自己的设置项、读写自己的数据、发网络请求。

插件目录：

| 平台 | 路径 |
|---|---|
| Windows | `%APPDATA%\netHEmusic\plugins\<插件id>\` |

在软件里打开：**侧边栏 → 插件 → 打开插件文件夹**。

---

## 2. 最小插件

```
plugins/
└── hello/
    ├── manifest.json
    └── main.js
```

`manifest.json`

```json
{
  "manifest_version": 1,
  "name": "Hello 插件",
  "version": "1.0.0",
  "author": "你的名字",
  "description": "一个最小示例",
  "permissions": ["ui", "events"],
  "injects": { "Main": [ { "file": "./main.js" } ] }
}
```

`main.js`

```js
nethe.log('hello from ' + nethe.id);
nethe.toast('Hello 插件已加载');

// 改界面（需要 ui 权限）
var style = nethe.css.add('#topbar { border-bottom: 2px solid ' + '#66ccff' + '; }');

// 监听切歌（需要 events 权限）
nethe.on('track', function (song) {
  nethe.toast('正在播放：' + song.Title + ' - ' + song.Artist);
});
```

放好文件夹后，在「插件」页点 **重新加载全部**（或重开软件）。

---

## 3. manifest.json 字段

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `manifest_version` | number | 是 | 目前固定 `1` |
| `name` | string | 是 | 显示名 |
| `version` | string | 是 | 版本号，建议语义化版本 |
| `author` | string | 否 | 作者 |
| `description` | string | 否 | 一句话说明，显示在插件卡片上 |
| `homepage` | string | 否 | 项目主页 |
| `permissions` | string[] | 否 | 需要的权限，**没声明的能力调用会被拒绝** |
| `injects` | object | 是 | 注入哪些文件。形如 `{ "Main": [ { "file": "./main.js" } ] }`，按顺序拼接执行 |

> `injects` 的结构与 BetterNCM 保持一致，方便迁移已有插件。
> `file` 是相对插件目录的路径，**不允许跳出插件目录**。

---

## 4. 权限

| 权限 | 允许做什么 | 拒绝时 |
|---|---|---|
| `ui` | `nethe.css.add/remove`、`nethe.dom.add`、`nethe.page.register` | 调用返回空 / 不注册，并在日志里报错 |
| `events` | `nethe.on(...)` 监听播放、切歌、歌词等事件 | 不注册监听 |
| `settings` | `nethe.settings.add(...)` 往设置页加设置项 | 不加 |
| `filesystem` | `nethe.data.read/write` 读写插件自己的数据目录 | Promise 直接 reject |
| `network` | `nethe.http.get/post` 发请求（走宿主代理，**绕开 CORS**） | Promise 直接 reject |

> **诚实说明**：插件代码是跑在界面页面里的，权限系统管住的是「宿主提供的 API」。
> 它拦不住插件自己去碰 `document`、`window`、`localStorage`。
> 所以 **装插件 = 信任插件**，只装你信得过的。

---

## 5. API 参考

所有 API 都挂在注入代码的全局对象 `nethe` 上。

### 5.1 基本信息（无需权限）

| 成员 | 类型 | 说明 |
|---|---|---|
| `nethe.id` | string | 插件 id（就是文件夹名） |
| `nethe.name` | string | 插件名 |
| `nethe.version` | string | 插件版本 |
| `nethe.author` | string | 作者 |
| `nethe.dir` | string | 插件目录绝对路径 |
| `nethe.permissions` | string[] | 本插件声明的权限 |
| `nethe.has(perm)` | (string)=>boolean | 是否拥有某权限 |
| `nethe.log(...args)` | (...any)=>void | 写进软件日志（`[plugin]` 前缀），排错用 |
| `nethe.toast(text)` | (string)=>void | 弹一个底部提示 |

### 5.2 界面 —— 权限 `ui`

| 方法 | 返回 | 说明 |
|---|---|---|
| `nethe.css.add(cssText)` | `HTMLStyleElement` | 注入一段 CSS，返回 style 元素 |
| `nethe.css.remove(node)` | void | 移除之前注入的 style |
| `nethe.dom.q(selector)` | `Element \| null` | 查一个元素 |
| `nethe.dom.qa(selector)` | `Element[]` | 查一堆元素 |
| `nethe.dom.add(parent, tag, className, html)` | `Element` | 往某处插一个元素 |
| `nethe.page.register({id, title, render})` | void | 注册自己的页面，会出现在「插件」页里；`render(container)` 里随便画 |

### 5.3 事件 —— 权限 `events`

```js
nethe.on('track', function (song) { /* song: {Id,Title,Artist,Album,Pic,Duration} */ });
```

| 事件名 | payload | 触发时机 |
|---|---|---|
| `track` | Song 对象 | 切歌 |
| `play` | `{ playing: true }` | 开始播放 |
| `pause` | `{ playing: false }` | 暂停 |
| `lyric` | `{ index, time, text, next }` | 歌词当前行变化 |
| `volume` | `{ volume: 0-100 }` | 音量变化 |
| `theme` | `{ dark, vars }` | 主题/配色变化 |

### 5.4 设置项 —— 权限 `settings`

```js
nethe.settings.add({
  key: 'my_switch',       // 必填，插件内唯一
  label: '我的开关',       // 显示名
  type: 'switch'          // 'switch' | 'text' | 'number'
});
```

### 5.5 私有数据 —— 权限 `filesystem`

沙箱在 `%APPDATA%\netHEmusic\plugin-data\<插件id>\`，**不能读写这个目录之外的文件**。

| 方法 | 返回 | 说明 |
|---|---|---|
| `nethe.data.read(name)` | `Promise<string>` | 读文本文件，不存在返回空串 |
| `nethe.data.write(name, content)` | `Promise<void>` | 写文本文件（自动建目录） |

`name` 支持子路径（如 `config/a.json`），但不允许 `..`。

### 5.6 网络 —— 权限 `network`

| 方法 | 返回 | 说明 |
|---|---|---|
| `nethe.http.get(url)` | `Promise<string>` | GET，返回响应正文 |
| `nethe.http.post(url, body)` | `Promise<string>` | POST，body 是字符串或对象（对象会转 JSON） |

只允许 `http://` 和 `https://`。请求由宿主发出，所以不受浏览器跨域限制。

---

## 6. 完整示例

```js
// manifest: {"permissions":["ui","events","settings","filesystem"],"injects":{"Main":[{"file":"./main.js"}]}}

var songCount = 0;

// 1) 加一个设置项
nethe.settings.add({ key: 'show_counter', label: '显示切歌计数器', type: 'switch' });

// 2) 在标题栏右侧塞一个计数
var badge = nethe.dom.add(nethe.dom.q('#topbar'), 'div', 'my-badge', '0');
badge.style.cssText = 'margin-left:auto;padding:2px 10px;border-radius:20px;background:rgba(102,204,255,.2);color:#66ccff;font-size:12px';

// 3) 切歌就 +1，并记到插件自己的数据目录
nethe.on('track', function (song) {
  songCount++;
  badge.textContent = String(songCount);
  nethe.data.write('count.txt', String(songCount));
});

// 4) 启动时恢复
nethe.data.read('count.txt').then(function (t) {
  songCount = parseInt(t || '0', 10) || 0;
  badge.textContent = String(songCount);
});
```

---

## 7. 调试

1. **打开开发者工具**：设置 → 高级 → 开发者工具（F12 打开），然后按 F12 或右键「检查」。
   插件里的 `console.log` 会出现在这里，报错也会。
2. **看软件日志**：`%APPDATA%\netHEmusic\logs\log.txt`，插件相关都会带 `[plugin]` 前缀，
   包括「没有声明某权限，已拒绝」这类信息。
3. **改完怎么生效**：插件是启动时注入的，改完代码点「插件」页的 **重新加载全部** 即可（不用重开软件）。

---

## 8. 发布到插件市场

市场是**话题聚合**的 —— 不用给谁提 PR，也不用注册什么账号：

1. 把插件做成一个**公开仓库**
2. 仓库**根目录**放 `manifest.json`（就是插件自己用的那个文件，格式见第 3 节）
3. 给仓库加 **`nethe-plugin`** 话题（仓库页右上角 ⚙ About → Topics）

之后就完事了。[**netHEmusic-plugins**](https://github.com/netHEmusic/netHEmusic-plugins)
（插件商店仓库）里的 GitHub Actions 每天（UTC 03:00）会把所有带这个话题的仓库聚合成
`plugins.json`，软件里「插件 → 插件市场」直接能看到并一键安装。
想立刻看到效果，可以在那个仓库的 Actions 页手动跑一次 **update**。

**打包方式**（二选一）：

- **什么都不做**：直接用仓库的源码 zip（GitHub 自动生成），客户端能认
- **在 Release 里传一个 `.zip` 资源**：有的话优先用它，你可以只把要发布的东西打进去

不管哪种，zip 里必须满足下面之一，否则客户端解压后找不到 manifest：

- 根目录直接有 `manifest.json`
- 或者只有**一层**子目录，`manifest.json` 在那层目录里

**清单里用到的字段**（从你的 `manifest.json` 里读）：

| 字段 | 说明 |
|---|---|
| `name` / `version` / `author` / `description` / `homepage` / `permissions` | 展示用 |
| `id`（可选） | 安装后的文件夹名；不写就用仓库名 |

### 想自己控制市场清单

不想走话题聚合的话，也可以自己写一份清单 JSON 自己托管，然后在 `config.ini` 里指过去：

```ini
[Plugins]
market_url = https://你的地址/market.json
```

清单格式：

```json
{
  "manifest_version": 1,
  "plugins": [
    {
      "id": "hello",
      "name": "Hello 插件",
      "version": "1.0.0",
      "author": "你的名字",
      "description": "一句话说明",
      "permissions": ["ui", "events"],
      "download": "https://example.com/hello-plugin.zip"
    }
  ]
}
```

| 字段 | 必填 | 说明 |
|---|---|---|
| `id` | 是 | 安装后的文件夹名，只能含字母数字下划线短横 |
| `name` / `version` / `author` / `description` | 否 | 展示用 |
| `permissions` | 否 | 展示用，安装前会展示给用户确认 |
| `download` | 是 | **https** 直链，指向一个 zip；zip 里要么根目录直接有 `manifest.json`，要么只有一层子目录 |

---

## 9. 限制与注意事项

- 插件**不能**调用网易云 API（宿主没有开放这个能力），所以拿不到账号数据。
- 插件在页面加载后执行；此时界面元素已就绪，但**歌单/歌曲数据可能还没到**，
  需要数据时请用 `nethe.on('track', ...)` 这类事件，而不是在加载时就读。
- 单个插件里的 JS 出错不会影响其它插件和主程序（宿主会捕获并记日志）。
- 卸载插件会把它移到 `plugins\.trash\`，不会被直接删掉。
- 插件目录里以 `.` 开头的目录（如 `.trash`）不会被当成插件。
