# Pi Agent Desktop

[![build](https://github.com/bigm0re/pi-agent-desktop/actions/workflows/build.yml/badge.svg)](https://github.com/bigm0re/pi-agent-desktop/actions/workflows/build.yml)
[![release](https://github.com/bigm0re/pi-agent-desktop/actions/workflows/release.yml/badge.svg)](https://github.com/bigm0re/pi-agent-desktop/actions/workflows/release.yml)
[![license](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**把原版 pi agent 装进 Windows 托盘。** 原生 Win32 桌面程序（C# / .NET 8 / WinForms + WebView2），
负责定位并拉起原版 [pi agent](https://github.com/earendil-works/pi) 服务，把
[pi-web](https://github.com/agegr/pi-web) 的界面放进原生窗口 —— 解压即用，不需要安装，也不需要任何命令行。

![外壳窗口与托盘菜单示意图](docs/preview.png)

*示意图：左侧为外壳窗口（加载页），右侧为托盘右键菜单。窗口内的智能体界面就是原版 pi-web 本身。*

- **不是** Electron / Tauri，**不打包**浏览器内核（宿主系统自带的 WebView2）
- **不重写**界面：窗口里就是原版 pi-web，功能与外观 1:1，与终端里的 pi 共用 `~/.pi/agent`
- **绿色版**：一个约 2 MB 的原生 exe，解压到任意目录即可运行，无安装程序、无安装目录
- 发布包是**自包含**构建（目标机无需预装 .NET 运行时）

```text
┌───────────────── PiAgentDesktop.exe （原生 Win32 / C#，单文件） ──────────────────┐
│  Program       单实例 Mutex + 命名管道激活；二次启动会提示而非静默退出             │
│  TrayContext   托盘图标 / 菜单 / 生命周期编排 / 睡眠与关机处理                      │
│    ├── PiWebServer    定位 node.exe 与 pi-web，拉起子进程，就绪探测，崩溃自愈        │
│    ├── AgentWindow    WinForms 窗口 + WebView2 控件（宿主系统 Edge WebView2）      │
│    ├── NpmEnvironment 修复 Windows 上 pi 的 npmCommand（插件更新必需）             │
│    └── AppConfig      %APPDATA%\PiAgentDesktop\config.json                       │
└──────────────────────────────────────────────────────────────────────────────────┘
                                  │ stdio / HTTP
                                  ▼
      node.exe  bin\pi-web.js --port 30141 --hostname 127.0.0.1 --no-open
                                  │
                       Next.js 服务 + 原版 pi agent 内核
```

## 特性

| 能力 | 说明 |
| --- | --- |
| 启动即后台 | 默认只留托盘图标，窗口懒加载；`PreloadWindow` 后台预建窗口，首次点击秒开 |
| 托盘一点即开 | 左键单击切换显示/隐藏，双击或气球提示点击直达 |
| 界面 = 原版 pi-web | 窗口内加载本机 pi-web 服务，会话 / 模型 / 插件配置与命令行版完全共享 |
| 自建服务 | 默认自己拉起 pi agent（**不依赖任何终端窗口**），端口被占用时自动顺延 |
| 单实例 | 已运行时二次启动会唤起已有窗口；唤不起则**弹出提示**，不会静默闪退 |
| 随 Windows 启动 | 托盘菜单开关，写一条 `HKCU\...\Run`，参数 `--hidden`（开机只进托盘） |
| 关闭/最小化进托盘 | 托盘菜单"退出"才真正结束 |
| 崩溃自愈 | 子进程异常退出按指数退避重启（默认最多 5 次） |
| 不残留僵尸进程 | Win32 Job 对象 `KILL_ON_JOB_CLOSE`：外壳被强杀时 node 进程树一并回收 |
| 睡眠/关机不误报 | 监听 `SessionEnding` / `PowerModeChanged`，系统下电时静默收尾，不记为崩溃、不做无意义重启 |
| 一键装/更新 pi-web | 托盘菜单"安装 / 更新 Pi Web…"，**先停服务再安装**（避免目录占用 EBUSY） |
| 自动修插件更新 | 修复 Windows 上 `execFile("npm")` 的 ENOENT（详见下文） |

## 环境要求

| 组件 | 要求 | 说明 |
| --- | --- | --- |
| Windows | 10 / 11 x64 | 依赖 Win32 Job 对象与 WebView2 |
| Node.js | **22.19+** | 运行 pi agent 服务（pi-web 的硬性要求，**发布包不自带**） |
| `@agegr/pi-web` | 最新版 | 提供 pi agent 运行时；可用托盘菜单一键安装 |
| WebView2 Runtime | Evergreen | Win10/11 通常已自带；缺失时程序会提示并可从浏览器打开降级 |
| .NET Runtime | **无需** | 发布包为自包含构建；仅自行编译时需要 .NET SDK 8 |

## 使用（绿色版）

1. 从 [Releases](../../releases) 下载 `PiAgentDesktop-<版本>-win-x64-portable.zip`
2. 解压到任意目录（例如 `D:\Tools\PiAgentDesktop`）
3. 双击 `PiAgentDesktop.exe` —— 窗口打开，托盘出现图标

没有安装程序，没有安装目录，删除文件夹即可卸载。

> 首次若未装 `@agegr/pi-web`，程序会提示并提供一键安装；若未装 Node.js，请先装
> [Node.js 22.19+](https://nodejs.org)。

**只写这些位置**（便于彻底清理）：

| 位置 | 内容 |
| --- | --- |
| `%APPDATA%\PiAgentDesktop\` | `config.json` 与 `logs\` |
| `%LOCALAPPDATA%\PiAgentDesktop\` | WebView2 用户数据目录 |
| `HKCU\...\Run` | 仅当你开启"随 Windows 启动"时写入一条 |

### 升级

把新版本的 zip 解压覆盖到同一目录即可（覆盖前先退出程序）。也可以让脚本代劳：

```powershell
# 下载好新版本 zip 之后：
.\tools\update-portable.ps1 -Zip "$env:USERPROFILE\Downloads\PiAgentDesktop-0.1.2-win-x64-portable.zip"
```

脚本会：停止运行中的实例 → 备份当前目录（带时间戳）→ 覆盖新文件 → 重新启动。

> ⚠️ **升级会结束被 App 托管的 agent 会话。** 外壳把 pi agent 服务作为子进程放在一个 Win32 Job
> 对象里，这是为了确保 node 不会残留。因此退出外壳 = 结束它托管的会话，升级前请先停下手头任务。

## 怎么确认在跑哪个版本

`<Version>` 会同时出现在 4 个地方，便于区分不同构建：

| 位置 | 内容 |
| --- | --- |
| 窗口标题 | `Pi Agent Desktop 0.1.1 — 127.0.0.1:30141` |
| 托盘菜单（"退出"上一行） | `版本 0.1.1` |
| 启动日志 | `=== Pi Agent Desktop 0.1.1 starting ===` |
| `smoke-result.json` | `"version": "0.1.1"` |

日志文件：`%APPDATA%\PiAgentDesktop\logs\desktop.log`（含 pi-web 子进程的 stdout/stderr，超 5 MB 轮转）。

## 配置

`%APPDATA%\PiAgentDesktop\config.json`（托盘菜单"打开配置文件"直达）。修改后重启应用生效。

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `Port` | `30141` | pi agent 服务端口 |
| `PortScanLimit` | `10` | 端口被占用时向后尝试的数量 |
| `HostName` | `127.0.0.1` | 监听地址；改 `0.0.0.0` 可局域网访问（务必配合 `PI_WEB_PASSWORD`） |
| `ReuseRunningServer` | `false` | 改为 `true` 则复用已在运行的 pi-web（注意：那样会依赖起它的那个终端） |
| `EnsureNpmCommand` | `true` | 自动修复 pi 的 `npmCommand`（Windows 插件更新必需） |
| `NodePath` / `PiWebDir` | `null` | 手工指定 `node.exe` / pi-web 目录（自动探测失败时用） |
| `PiAgentDir` | `null` | 等价 `PI_CODING_AGENT_DIR`，指定 pi agent 数据目录 |
| `Environment` | `{}` | 追加给 pi-web 子进程的环境变量（如 `HTTP_PROXY`） |
| `StartHidden` | `false` | `true` 则启动只进托盘；开机自启走 `--hidden`，与这项无关 |
| `CloseToTray` / `MinimizeToTray` | `true` | 关闭 / 最小化时进托盘 |
| `PreloadWindow` | `true` | 后台预建窗口并加载界面，首次点击即秒开 |
| `NotifyOnHide` | `true` | 首次隐藏时弹气泡提示 |
| `AutoStart` | `false` | 随 Windows 启动（托盘菜单也可切换） |
| `WindowWidth` / `WindowHeight` / `WindowMaximized` | `1280` / `840` / `false` | 窗口尺寸记忆 |
| `RestartOnCrash` / `MaxRestarts` | `true` / `5` | 崩溃重启策略 |
| `ReadyTimeoutMs` | `120000` | 等待服务就绪上限 |

## 命令行

```text
PiAgentDesktop.exe [--show] [--hidden] [--no-tray] [--port <n>]
                   [--node <path>] [--pi-web-dir <path>]
                   [--smoke-test] [--echo-log]
```

| 参数 | 说明 |
| --- | --- |
| `--show` / `-s` | 启动即显示窗口 |
| `--hidden` | 强制后台启动（开机自启使用） |
| `--no-tray` | 不建托盘图标（此时必定显示窗口） |
| `--port <n>` / `-p` | 临时指定端口；**不会写回配置文件** |
| `--node <path>` | 指定 `node.exe` |
| `--pi-web-dir <path>` | 指定 pi-web 安装目录 |
| `--smoke-test` | 非交互自检，结果写入 `smoke-result.json`，退出码 0/1 |
| `--echo-log` | 日志同时输出到 stdout |

## 托盘菜单

```text
状态：运行中 · 端口 30141
────────────────────────
打开 Pi Agent                  在浏览器中打开
复制访问地址                    重新加载界面
────────────────────────
重新启动服务                    打开日志文件          打开配置文件
────────────────────────
随 Windows 启动                 启动时最小化到托盘     关闭窗口时最小化到托盘
────────────────────────
检查插件更新环境（npmCommand）   安装 / 更新 Pi Web…
────────────────────────
版本 0.1.1
退出
```

## 为什么是原生，而不是 Electron / Tauri

| 方案 | 取舍 |
| --- | --- |
| Electron | 自带 Chromium + Node 双运行时，数百 MB，非原生 |
| Tauri | 需要 Rust 工具链；仍是自带 WebView 的框架层 |
| **WinForms + WebView2** | 宿主 Windows 自带的 WebView2 运行时；托盘、通知区域、单实例、注册表自启都是 Win32 一等能力；产物约 2 MB |

界面不去重写、也不复制，而是**继续由原版 pi-web 提供** —— 这样"界面与功能对齐 pi-web"天然成立，
且上游更新即时受益。WebView2 是 Windows 平台标准的原生 Web 宿主（Office、Windows 11 组件同款），
不是打包进应用的浏览器内核。

服务则**必须由真实 Node.js 进程承载**：pi agent 依赖 `node-pty` 等原生 npm 模块，
必须跑在与 npm 安装时一致的 Node ABI 下。

## 已知限制

- **退出外壳会结束它托管的 agent 会话**（Job 对象的必然结果；见上文"升级"的警告）。
- 发布包**不自带 Node.js 与 pi-web**，首次使用需目标机已有 Node 22.19+（pi-web 可一键装）。
- WebView2 不提供 Service Worker 推送，因此 pi-web 的 `web-push` 通知与"安装 PWA"不可用；
  SSE 流式输出、终端（xterm + node-pty）、文件预览、Git diff 等核心功能正常。
- 首次 WebView2 初始化约 0.5–1 s（`PreloadWindow` 已在后台抹平）。
- 外壳本身不解析会话数据，所有状态以 pi agent 服务为准。
- 配置与日志固定写在 `%APPDATA%`，不随程序目录移动（"绿色"指的是免安装与无残留，
  不是"配置随身携带"）。

## Windows 上的 npm 陷阱（已自动处理）

pi agent 的**插件更新检查**会执行 `execFile("npm", ["view", ...])`。Windows 上 npm 只有
`npm.cmd` / `npm.ps1` 包装，而 Node 在非 shell 场景**不查 `PATHEXT`**：

| 调用 | 结果 |
| --- | --- |
| `execFile("npm")` | `ENOENT`（没有 `npm.exe`） |
| `execFile("npm.cmd")` | `EINVAL`（CVE-2024-27980 禁止无 shell 执行批处理） |
| `execFile("cmd", ["/c","npm"])` | ✅ 正常 |

因此本程序会在启动时检查 pi 的 `settings.json`，缺失或**在本机不可用**时写入可移植写法：

```json
"npmCommand": ["cmd", "/c", "npm"]
```

只填补空缺或修复明显不可用的值（例如从别的机器拷来的绝对路径），你自己设的合理值不会被覆盖；
写入前自动备份，改完还会跑一次 `cmd /c npm --version` 自检。该设置由 **pi agent 内核**读取，
所以浏览器版 pi-web 与命令行的 pi 也会一并受益。

## 故障排查

日志：`%APPDATA%\PiAgentDesktop\logs\desktop.log`

| 现象 | 处理 |
| --- | --- |
| 双击后"闪退"、没有窗口 | 多半是已有实例在跑（单实例）。看托盘是否有图标；新版会弹提示而不是静默退出 |
| 托盘显示"启动失败" | 托盘菜单"打开日志文件"看 pi-web 报错；常见原因是未装 `@agegr/pi-web` 或 Node 版本过低 |
| 提示找不到 Node.js | 安装 Node 22.19+，或配置 `"NodePath": "C:\\Program Files\\nodejs\\node.exe"` |
| 提示找不到 pi-web | 托盘菜单"安装 / 更新 Pi Web…"，或 `npm install -g @agegr/pi-web` |
| 界面一直转圈 | 用"在浏览器中打开"确认服务本身正常；服务正常则是 WebView2 运行时问题 |
| 插件更新报 `spawn npm ENOENT` | 托盘菜单"检查插件更新环境（npmCommand）"，或手动加 `"npmCommand": ["cmd","/c","npm"]` |
| 端口被占用 | 自动顺延；也可在配置里改 `Port` |
| 想彻底清理 | 托盘"退出"后删 `%APPDATA%\PiAgentDesktop`、`%LOCALAPPDATA%\PiAgentDesktop` 和解压出来的目录 |

## 开发者：从源码构建

前置：.NET SDK 8（仅编译期需要）、Node.js 22+（生成图标）。

```powershell
powershell -File tools\install-dotnet-sdk.ps1   # 若没有 .NET SDK（用户级安装，免管理员）

.\build.ps1                                     # 生成图标 + 编译发布到 dist\
.\build.ps1 -Run                                # 编译并启动
.\build.ps1 -SmokeTest                          # 端到端自检，输出 smoke-result.json
.\build.ps1 -SelfContained                      # 自包含发布（对应发布包）
```

手动等价命令：

```powershell
node tools\make-icons.js
dotnet publish src\PiAgentDesktop\PiAgentDesktop.csproj -c Release -r win-x64 -o dist -p:PublishSingleFile=true
```

> **关于仓库里的 `NuGet.config`**
> 有些机器的 `%APPDATA%\NuGet\NuGet.Config` 里是**空的 `<packageSources>`**，会把默认包源清空，
> 于是还原报 `NU1100: 无法解析 … Microsoft.Web.WebView2` —— 看起来极像 TFM 不兼容，实际是"没有任何包源"。
> 仓库自带这份配置后构建不再受机器全局配置影响；走内网镜像时改其中地址即可。

### 自检（无界面验证）

```powershell
.\build.ps1 -SmokeTest
```

会走完整链路（定位 node/pi-web → 拉起或复用服务 → 就绪探测 → WebView2 初始化 → 导航），
把结果（版本、端口、进程号、WebView2 版本、导航结果、npm 自检）写入 `smoke-result.json`，
并以退出码 0/1 结束。该测试需要真实桌面会话与 WebView2，因此**不在 CI 中运行**。

### 发布流程

```powershell
# 1) 改 src/PiAgentDesktop/PiAgentDesktop.csproj 里的 <Version>
# 2) 提交
git commit -am "release 0.1.2"
# 3) 打标签并推送 —— 这会触发 release 工作流
git tag v0.1.2
git push origin main --tags
```

`.github/workflows/release.yml` 随后自动：读取 csproj 版本 → 自包含单文件发布 →
打包为 `PiAgentDesktop-<版本>-win-x64-portable.zip`（内含 exe、`WebView2Loader.dll`、README、LICENSE）
→ 创建 GitHub Release 并附上该 zip。`.github/workflows/build.yml` 则在每次 push / PR 上验证能否构建。

### 仓库结构

```text
src/PiAgentDesktop/
  Program.cs           入口：单实例与二次启动提示、异常兜底、消息循环
  TrayContext.cs       托盘图标 / 菜单 + 生命周期编排 + 睡眠关机处理 + 冒烟测试
  AgentWindow.cs       WinForms 窗口 + WebView2 宿主 + 加载页状态推送
  PiWebServer.cs       子进程管理：拉起 / 就绪探测 / 崩溃退避重启 / 进程树回收
  PiWebLocator.cs      定位 node.exe 与 pi-web（PATH / nvm / npm 全局前缀）
  NpmEnvironment.cs    修复 Windows 上的 npmCommand
  PiWebInstaller.cs    npm install -g 进度窗口
  JobObject.cs         Win32 Job 对象（KILL_ON_JOB_CLOSE）
  AutoStart.cs         HKCU Run 键
  SingleInstance.cs    Mutex + 命名管道激活
  AppConfig.cs         配置读写
  AppPaths.cs          路径与日志
  Assets.cs            内嵌图标 + 加载页 HTML
  AppInfo.cs           版本号（日志 / 托盘 / 标题栏共用）
tools/make-icons.js            纯 Node 生成多尺寸 ICO（DIB 格式，System.Drawing 友好）
tools/make-preview.ps1         生成 docs/preview.png 示意图
tools/check-cs-structure.js    字面量感知的括号平衡自检（编译前快速体检）
tools/install-dotnet-sdk.ps1   免管理员的 .NET SDK 安装（BITS 直接拉 zip + 解压）
tools/update-portable.ps1      停实例 → 备份 → 覆盖 → 重启（绿色版升级）
.github/workflows/             build.yml（CI 构建）/ release.yml（打标签自动发布 zip）
docs/preview.png               示意图
build.ps1                      build / -Run / -SmokeTest / -SelfContained / -DeployTo
NuGet.config                   固定 nuget.org 包源（见上文说明）
```

## 许可与致谢

[MIT](LICENSE)

- [pi-web](https://github.com/agegr/pi-web) —— 本项目的界面与 agent 运行时（MIT）
- [pi](https://github.com/earendil-works/pi) —— pi agent 内核
- [Microsoft Edge WebView2](https://learn.microsoft.com/microsoft-edge/webview2/) —— 系统 Web 宿主
