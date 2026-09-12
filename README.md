# netHEmusic 网易云音乐下载器（v26.9.12.20 — WinUI3 + Fluent 重写）

> 本版本为**完全重构**：将旧 Qt6 架构改为微软官方 **WinUI 3 + Fluent** 桌面应用（模块化元素架构）。

## 特性
- **WinUI3 + Fluent / Material You**：基于 Windows App SDK 的现代化圆角、Mica 材质、深浅色与 40+ 套 Material You 配色方案（移植自 material-you-theme-netease）。
- **模块化元素**：所有控件以「类别.位置.子位置」键名管理（`gui.main.index`、`button.main.headbar.search`、`dock.main.index` 等），支持「选定的元素窗口」鼠标悬停提示。
- **本地化**：`lang/zh_cn.lang`、`lang/en_US.lang` 双语言，语言包可扩展。
- **用户协议**：首次运行弹出协议，结尾显示随机 6 位启用密码，输入正确方可使用，否则退出。
- **更新**：GitHub Release 检测（镜像多源多线程下载 + SHA-256 校验），更新界面 / 进展条 / 自动重启；主界面含更新日志。
- **主界面 16:9**：顶栏（Logo/返回/前进/搜索/用户/VIP/主题/设置/最小化/最大化/关闭）、左侧边栏（搜索/发现/歌单/歌词/我喜欢/账号/设置）、浏览器（WebView2 材质化 UI）、底部 dock（封面/歌曲/进度/上一下一/播放/下载）。
- **播放**：内建 MediaPlayer + **SMTC**（系统媒体控件），三首内存环 + 1GB 磁盘缓存。
- **桌面歌词/歌曲信息**：强制置顶 + 鼠标穿透，可关闭置顶（黄色框可拖动）；横排/竖排；歌词含原文/中文翻译/罗马音切换。
- **设置**：主题、语言、下载、播放、缓存、主题方案、Mica、登录、更新、日志、高级（控制台、选定元素窗口）、多方案等。
- **日志**：5 份轮换（log.txt→log5.txt），详细记录 + 敏感信息脱敏（token→XlogintokenX）；`-debugger` 启动显示控制台。
- **自运行/自测试/自更新/自修复/自我迭代**：`tools/*.ps1` 提供 bootstrap/build/test/sign/update/selfrepair/iterate 全链路。
- **签名与启动证书校验**：软件由自签名证书 **Laohehehe**（签发机构 **LaoheTeam.top**，到期 **2100-06-17**）签名。程序启动时检测计算机是否存在该启动证书：若不存在则不启动主界面，弹出「安装证书」提示，安装并信任后方可继续。

## 构建
```
powershell -ExecutionPolicy Bypass -File tools/bootstrap.ps1   # 首次：本地安装 .NET SDK 8.0
powershell -ExecutionPolicy Bypass -File tools/build.ps1       # 构建自包含 exe
powershell -ExecutionPolicy Bypass -File tools/test.ps1        # 自测试
powershell -ExecutionPolicy Bypass -File tools/sign.ps1        # 自签名：Laohehehe / 签发机构 LaoheTeam.top / 到期 2100-06-17
powershell -ExecutionPolicy Bypass -File tools/iterate.ps1     # 自我迭代闭环
```

### 工具链要求（生成 resources.pri / 打包）
WinUI3 到最终 **打包 / 生成 resources.pri** 一步，依赖 Visual Studio 2022 Build Tools 的 **UWP / MSIX 打包组件**
（提供 `Microsoft.Build.AppxPackage.dll`、`Microsoft.Build.Packaging.Pri.Tasks.dll` 两个 MSBuild 任务程序集）。
- 本机若仅有 .NET SDK（缺该项），C# + XAML 编译可完整通过（生成 11 个 `.g.cs`、9 个 `.xbf`、编译 DLL），
  但打包步骤会报 `MSB4062`。
- 安装办法（管理员 PowerShell）：`powershell -ExecutionPolicy Bypass -File tools/install-msix-tooling.ps1`，然后重新 `tools/build.ps1`。

> 状态：代码已通过 **C# + XAML 编译层校验**；仅「最终打包 / resources.pri」受上述 VS Build Tools 组件限制。

## 目录
- `src/NetHEmusicCP`：WinUI3 应用（Core 服务 / Windows 窗口 / Web 前端 / Themes 主题）
- `lang`：语言包
- `resources`：Logo / 图标
- `tools`：自运行 / 自测试 / 自更新 / 自修复 / 自我迭代脚本
- `归档`：旧 Qt6 版本（不再使用）

## 免责声明
本软件为第三方独立开发软件，与网易云音乐官方无关，仅供学习交流，禁止商用及用于任何侵权用途。
