# netHEmusic

> **一个更好看的网易云音乐桌面客户端。** 外壳用 WinUI 3 + Fluent，播放器跑在 WebView2 里。
> 支持在线播放、无损下载、全窗口歌词页、音乐律动频谱、扫码登录、自动更新。

<p align="center"><img src="resources/logo.png" width="120" alt="netHEmusic"></p>

---

## ✨ 功能总览

### 🎵 播放
- **双播放内核**：默认由前端 `<audio>` + Web Audio 播放（可拿到真实频谱），可在设置里一键切回系统原生播放器
- **交叉淡化**：切歌时新曲淡入、旧曲淡出，0–12 秒可调
- **系统媒体控件（SMTC）**：支持键盘多媒体键 / Win11 媒体浮出卡片
- 播放模式：顺序 / 列表循环 / 单曲循环 / 随机播放
- 三首内存环预取 + 磁盘缓存（容量可配）
- 音量记忆、静音开关、进度条点击 / 拖动跳转

### 🌊 全窗口歌词页（点播放条封面进入）
- 沉浸式界面：**模糊封面背景** + 大封面 + 歌名 / 歌手 / 专辑 + 逐行高亮歌词 + 底部进度与控制
- 排版引擎按行距计算 **缩放 / 模糊 / 透明度 / 旋转**，支持「竖向」与「旋转弧形（曲率可调）」两种排列
- **同时显示翻译 + 罗马音**，可各自开关
- **背景音乐律动频谱**：柱状 / 环形绕封面 / 波形，强度和灵敏度可调（真实 FFT，非模拟）
- 歌词显示设置面板（右上角齿轮，**改完即时生效**）：字体辉光 / 阴影（互斥）、描边、字体大小、逐字动画、非当前行模糊 + 程度、动画曲线 4 种（平滑 / 急促 / 温和 / 缓出）
- 换行平滑滑动、进入 / 收起有过渡动画（封面从播放条飞入）

### 📚 音乐库
- **我的歌单**：拉取账号下创建与收藏的全部歌单，点开即看曲目列表
- **当前播放列表**：dock 最右侧按钮弹出，支持右键 **播放 / 下一首播放 / 删除 / 分享（复制网易云分享链接）**、一键清空
- **播放列表持久化**：关闭软件后再次打开仍是上次那份队列，并且停在上次播放的那一首

### 🔐 账号
- 扫码登录（账号页自动获取二维码，实时提示 等待扫码 / 已扫码待确认 / 已过期，可刷新）
- **登录凭据非对称加密保存**：RSA-2048 密钥对（私钥经 Windows DPAPI 二次保护）+ AES-256-GCM 正文，RSA-OAEP 只包裹 AES 密钥；旧格式自动迁移

### ⌨️ 快捷键（设置里可自定义）
| 按键 | 功能 |
|---|---|
| `空格` | 播放 / 暂停 |
| `Ctrl + Alt + →` / `←` | 下一首 / 上一首 |
| `Ctrl + Alt + ↑` / `↓` | 音量 + / − |
| `M` | 静音开关 |
| `F` | 打开 / 收起歌词页 |
| `Esc` | 收起歌词页 |

录制时按 `Backspace` 可**关闭**某一项快捷键，`Esc` 取消录制。

### 🎨 界面与交互
- WinUI 3 + Fluent：圆角、Mica 材质、深浅色自适应
- **40+ 套内置配色方案**（深色 / 浅色自适应），切换时带水波纹扩散动画
- 无底色描边风图标体系（同一功能全应用统一图标）
- 鼠标特效：光标拖尾、光晕、点击波纹、火花、按钮抖动 —— 长度 / 粗细 / 大小 / 强度 / 颜色全部可调
- 隐藏标题栏系统菜单（右键 / Alt+Space），界面更像原生应用
- 设置项：主题外观 / 歌词页 / 性能 / 播放 / 下载 / 网络代理 / 语言

### ⚡ 性能
- 可调项：歌词页背景模糊开关、播放态动画开关、频谱帧率（15 / 24 / 33 / 60）
- 空闲时几乎不占 CPU；歌词列表只渲染当前行附近内容

### 🔄 自动更新
- 启动时检测 GitHub Release，有新版弹出更新公告 + 更新日志
- 下载安装包并做 **SHA-256 完整性校验**，校验通过后自动运行安装程序
- GitHub 不可达时自动切换镜像源、多线程下载

---

## 🚀 安装

1. 到 [Releases](https://github.com/Laohehehe/netHEmusic/releases/latest) 下载最新的 `netHEmusic_Setup_*.exe`
2. 双击安装（默认装到 `D:\Program Files\netHEmusic`，可改路径）
3. 首次运行会弹出**用户协议**，输入协议结尾处显示的 6 位启用密码后点「同意并继续」才能进入；点「不同意并退出」不会加载主界面
4. 到账号页扫码登录，即可使用每日推荐 / 我的歌单等个性化内容

> 卸载：开始菜单「卸载 netHEmusic」，或系统设置 → 应用 → netHEmusic

---

## 🧩 技术栈

| 层 | 技术 |
|---|---|
| 外壳 / 窗口 | WinUI 3（Windows App SDK）+ Fluent，自包含 .NET 8 `net8.0-windows10.0.19041.0` |
| 前端界面 | WebView2 + 原生 HTML / CSS / JavaScript（无框架），JS ⇄ C# 通过 `postMessage` 桥接 |
| 播放 | 前端 `<audio>` + Web Audio（`AnalyserNode` 频谱）；可选原生 `MediaPlayer` + SMTC |
| 音乐接口 | NeteaseCloudMusicApi 实例，地址可在 `config.ini` 的 `[Network] api_base` 修改 |
| 打包 | Inno Setup（主用）、WiX Toolset（MSI） |
| 热重载 | 开发模式下监听 `Web/` 目录，改动 HTML / CSS / JS 即时刷新，无需重新编译 |

---

## 📁 目录结构
```
src/NetHEmusicCP       WinUI3 应用
  Core/                服务层：配置 / 网络 / 缓存 / 下载 / 播放 / 主题 / 更新 / 安全
  Windows/             窗口：主窗口、用户协议、证书、更新
  Web/                 前端界面（index.html + css/ + js/）
  Themes/              配色与主题资源
src/Setup              早期自解压安装器（已被 Inno 取代，保留）
installer/             Inno Setup 脚本（.iss）与 WiX 脚本（.wxs）
resources/             图标与 Logo
lang/                  语言包（zh_cn / en_US）
tools/                 自运行 / 自测试 / 自构建 / 自签名 / 自更新 / 自修复脚本
config/                默认配置模板与版本号
```

---

## 🔧 构建

```powershell
powershell -ExecutionPolicy Bypass -File tools/bootstrap.ps1   # 首次：准备 .NET SDK
powershell -ExecutionPolicy Bypass -File tools/build.ps1       # 编译自包含应用（Release）
powershell -ExecutionPolicy Bypass -File tools/sign.ps1        # 生成自签名证书并签名 exe
powershell -ExecutionPolicy Bypass -File tools/test.ps1        # 自测试
```

打包安装程序：
```powershell
# 1) 准备待打包目录（Release 产物）
# 2) 用 Inno Setup 编译 installer/netHEmusic.iss  → dist\netHEmusic_Setup_<版本>.exe
# 3) 用 signtool 给安装包签名
```

---

## 📌 常见问题

**Q：数据存在哪里？**
`%APPDATA%\netHEmusic` —— `config.ini`（全部设置）、`cookie.txt`（加密后的登录凭据）、`logs\`（5 份轮转日志，敏感信息脱敏）、`cache\`（音频缓存）、`webview\`（WebView2 数据）。

**Q：能换音乐接口服务器吗？**
可以。编辑 `config.ini` 的 `[Network] api_base`，填你自己的 NeteaseCloudMusicApi 实例地址即可。

**Q：如何开启调试日志？**
用 `NetHEmusicCP.exe -debugger` 启动，会分配控制台实时输出日志。

---

## 🙏 致谢
- 感谢 **BetterNCM 插件**提供的思路
- [NeteaseCloudMusicApi](https://github.com/Binaryify/NeteaseCloudMusicApi) —— 音乐接口
- [refined-now-playing-netease](https://github.com/solstice23/refined-now-playing-netease) —— 歌词排版思路参考
- [LibFrontendPlay](https://github.com/BetterNCM/LibFrontendPlay) —— 前端播放内核思路参考
- [SimpleAudioVisualizer](https://github.com/BetterNCM/SimpleAudioVisualizer) —— 频谱可视化思路参考

---

## 📄 开源协议
本项目采用 **[PolyForm Noncommercial License 1.0.0](https://polyformproject.org/licenses/noncommercial/1.0.0)**：
**允许非商业目的的使用、修改与分发**（需保留协议与版权声明），**禁止任何商业用途**。详见 [LICENSE](LICENSE)。

---

## ⚠️ 免责声明

**一、非官方声明**

本软件（netHEmusic）是由个人独立开发的第三方开源软件，与网易云音乐（杭州网易云音乐科技有限公司）及其关联方**没有任何关系**，未获得其任何形式的授权、许可、认可或赞助。软件名称中出现"网易云音乐"仅用于客观描述其内容来源，不代表任何官方身份。

**二、用途限制**

本软件仅供**个人学习、技术交流与研究**使用。不得用于任何商业用途；不得用于任何违反法律法规的用途；不得用于任何侵犯他人合法权益的用途；不得用于任何损害第三方利益的行为。

**三、内容与版权**

本软件本身不提供、不存储、不托管任何音乐作品。软件展示与下载的音乐、封面、歌词等全部内容，均来自第三方公开接口，**相关著作权及一切权利归原权利人所有**。使用者通过本软件获取的任何内容，仅可用于个人学习研究，并应在 **24 小时内删除**；不得传播、二次分发、公开播放或以任何方式用于营利性活动。

**四、风险自负**

本软件按"现状"提供，不附带任何明示或暗示的保证，包括但不限于对**可用性、稳定性、无错误、不中断、特定用途适用性**的保证。第三方接口可能随时变更、限流或失效，本软件可能因此无法正常使用。使用者应自行判断并承担使用本软件的全部风险。

**五、责任限制**

在适用法律允许的最大范围内，开发者**不对**因使用或无法使用本软件而产生的任何直接、间接、附带、特殊、惩罚性或后果性损失承担责任，包括但不限于数据丢失、设备损坏、账号异常、第三方索赔、利润损失或商誉损失——即使开发者已被告知此类损害发生的可能性。

**六、合规与责任承担**

使用者应确保其使用行为符合所在地法律法规及第三方服务条款。因使用者自身行为（包括但不限于下载、复制、存储、传播、分享通过本软件获取的内容）所产生的一切后果与法律责任，**由使用者自行承担**，与开发者无关。

**七、权利主张**

若任何权利人认为本软件或其相关内容侵犯了您的合法权益，请通过仓库 Issue 联系开发者，开发者将在核实后**立即删除相关内容或停止分发**。

**八、其他**

开发者保留随时修改、暂停或终止本软件及其任何功能的权利，恕不另行通知。本免责声明可能随时更新，继续使用即视为接受其最新版本。
