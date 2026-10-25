# SnapFlow (瞬流)

<p align="center">
  <b>轻量、现代且高效率的 Windows 智能截图、贴图、屏幕监控与剪贴板工作流增强工具</b>
</p>

<p align="center">
  <a href="https://github.com/yw1528101170-ui/SnapFlow/actions/workflows/ci.yml"><img src="https://github.com/yw1528101170-ui/SnapFlow/actions/workflows/ci.yml/badge.svg" alt="CI"/></a>
  <a href="https://github.com/yw1528101170-ui/SnapFlow/releases/latest"><img src="https://img.shields.io/badge/release-v1.1.4-blue" alt="release"/></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-green" alt="license"/></a>
</p>

---

## 📥 下载

前往 **[Releases 发布页](https://github.com/yw1528101170-ui/SnapFlow/releases/latest)** 下载最新版本：

| 文件 | 适合 |
| :--- | :--- |
| `SnapFlow-Setup-v<x.y.z>.exe` | 推荐安装版：向导式安装、开机自启、可一键卸载 |
| `SnapFlow-v<x.y.z>-win-x64-Portable.zip` | 绿色免安装版：解压即用 |
| `SHA256SUMS.txt` | 完整性校验清单（下载后可用 `Get-FileHash` / `sha256sum` 比对） |

---

## 🌟 核心特性

- **现代 Fluent Design 风格**：原生 Windows 11 / Windows 10 现代视觉，极光微质感图标，自适应浅色与深色模式。
- **智能选区与元素吸附**：鼠标悬停自动吸附窗口、按钮或面板边界，支持像素级精细调节与放大镜辅助。
- **专业标注工具箱**：矩形、椭圆、直线、箭头、铅笔、荧光笔、文字、马赛克/模糊、步骤序号。
- **贴图置顶 (Pin)**：截图后一键钉在桌面最前，支持缩放、拖动、复制或快捷关闭。
- **离线 OCR 文字识别**：毫秒级提取图片文字，支持选中文本复制与翻译。
- **取色器**：放大镜下精准定位像素，显示 RGB 与 HEX 格式，按 `C` 键直接复制色值。
- **滚动长截图**：智能向下或向上拼接长网页、聊天记录与长图表。
- **剪贴板历史**：独立管理最近复制的文字与图像，支持快速回溯与搜索。
- **本地密码库**：Argon2id + AES-256-GCM 整库加密，主密码本机存储不上云；Windows Hello 指纹/PIN 快速解锁、TOTP 动态验证码、随机密码/密码短语生成器（实时熵估算）、加密备份与恢复、Chrome/Edge/Bitwarden CSV 导入；窗口防截屏、机密复制不进剪贴板历史且定时自动清除、空闲/锁屏/睡眠自动锁定；配套浏览器扩展（`ext/snapflow-vault`）支持 Chrome/Edge 站点自动填充。
- **延时截图**：右键托盘提供 1~10 秒延时截图，方便截取悬浮菜单和临时状态。
- **屏幕性能监控悬浮窗 (OSD)**：极简轻量置顶挂件，鼠标全穿透零干扰，实时监控 NVIDIA GPU（核心利用率/显存/温度）、CPU、内存与网络速率；支持右上角/左上角停靠及截图时智能避让。
- **Apple 风格浮层动效**：OSD 贴顶「灵动岛」以阻尼弹簧展开/收起，数值指数缓动不跳变；系统级 Toast 为深色玻璃胶囊，入场缩放上浮淡入、退场快速下沉，文案切换时胶囊宽度弹性形变，并带 Apple 系统色徽章与处理中指示环。
- **开箱即用**：自带完整运行时，不需要额外安装 .NET，随解压随用，常驻系统托盘，内存与 CPU 占用极低。

---

## ⌨️ 快捷键速查

| 功能 | 默认快捷键 | 说明 |
| :--- | :--- | :--- |
| **区域截图** | `Alt + A` | 激活截图遮罩，可框选、标注、贴图或长截图 |
| **剪贴板历史** | `Alt + V` | 唤出剪贴板历史面板，直接回溯历史内容 |
| **密码库** | `Alt + P` | 打开/锁定本地加密密码库（未创建时进入引导） |
| **复制颜色值** | `C` | 取色器激活时，复制光标像素的 HEX 颜色 |
| **撤销标注** | `Ctrl + Z` | 撤销上一次的画笔或形状绘制 |
| **完成并复制** | `Enter` / 双击选区 | 将截图与标注复制到剪贴板并退出遮罩 |
| **取消 / 关闭** | `Esc` | 退出截图或关闭当前活动窗口 |

> 快捷键可在软件“设置”界面中自由修改（右键托盘图标 -> 设置）。

---

## 🛠️ 本地构建发布包

运行 `.\installer\build.ps1` 生成到 `dist/` 目录（文件名带具体版本号，以目录内实际版本为准）：

1. **安装版 (`SnapFlow-Setup-v<x.y.z>.exe`)**：
   - 现代 Inno Setup 向导安装程序。
   - 默认安装到当前用户专属目录（无需管理员提权，安全规范）。
   - 支持勾选创建桌面快捷方式与开机自启动。
   - 控制面板 / Windows 设置中可一键卸载；卸载时可选择是否同时删除历史/截图等用户数据（默认保留，重装可找回）。
2. **绿色免安装版 (`SnapFlow-v<x.y.z>-win-x64-Portable.zip`)**：
   - 免安装、解压即用，双击 `SnapFlow.exe` 即可运行；数据保存在用户目录（`%LOCALAPPDATA%\SnapFlow`），不随压缩包或 U 盘迁移。
   - 仅在你主动开启开机自启动时写入一条自启项，适合无安装权限的办公环境或临时使用。

---

## 🔥 开发调试：两条流程，按场景选

WinUI 3 有两条「改完马上看到」的路子，按你在改什么选：

| 你在改什么 | 用哪条 | 延迟 |
| :--- | :--- | :--- |
| 布局 / 颜色 / 间距 / Style / DataTemplate | **VS F5 + XAML 热重载** | 约 1 秒，界面原地变 |
| C# 逻辑、新窗口、事件处理器、`x:Class`、x:Bind | **`scripts\dev.ps1`** 自动重启 | 约 17 秒（构建 ~15s + 重启 1~2s） |

两条流程**互斥**，别同时用：`dev.ps1` 检测到 VS 正在调试会自动避让（不构建、不抢进程）。

### 流程 A：VS 按 F5（调 UI 用这条）

```powershell
# 双击即开（.slnx 已关联 VS 2026），或：
& "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe" E:\jietu\SnapFlow.slnx
```

1. 工具栏平台必须是 **`x64`**（项目只声明了 x64；选 `Any CPU` 会报「无法启动启动项目」，
   下拉里没有 x64 就去「配置管理器 → 活动解决方案平台 → 新建 → x64」）；
2. 启动档选 **`SnapFlow (dev)`**（`Properties/launchSettings.json` 里带的 `--dev`），按 **F5** ——
   **不能 Ctrl+F5**，热重载依赖调试器附加；
3. SnapFlow 是托盘应用，**F5 后不弹窗口**：`--dev` 会**自动把设置窗口打开**，托盘图标在右下角 `^` 里；
4. 改任意 `.xaml` → `Ctrl+S` → 窗口 1 秒内更新；窗口顶部会浮出热重载工具条。

> 「XAML 实时预览不可用」是另一个功能（Live Preview，要求窗口可见），不影响热重载，忽略即可。

### 流程 B：`scripts\dev.ps1` 自动重启（改逻辑 / 不想开 VS 时用）

```powershell
.\scripts\dev.ps1            # 监视 + 自动构建 + 自动重启（默认）
.\scripts\dev.ps1 -Mode run  # 只构建并启动，不监视
.\scripts\dev.ps1 -NoBuild   # 只重启，用外部构建产物（IDE / CI）
.\scripts\dev.ps1 -Mode build # 只构建
```

- **改了 `.cs` / `.xaml` / `.csproj` 保存即生效**：检测到改动 → `dotnet build` → 请旧进程优雅退出
  （热键、托盘、SQLite、密码库会话都正常清理）→ 同步产物到 `.dev-run\app` → 带 `--dev` 重新拉起。
- **构建期间旧版本一直可用**：产物写 `bin/`，程序跑 `.dev-run/app`，两者互不锁文件；
  构建失败也不会打断正在运行的实例，改好保存会自动重来。只有「同步 + 重启」约 1~2 秒是空窗。
- **窗口自动还原**：重启后会把刚才开着的「设置 / 剪贴板历史 / 密码库」重新打开，肉眼几乎无感。
- **不与正式版互相拦截**：`--dev` 用独立单实例通道（互斥体 / 激活事件加 `.dev` 后缀），
  启动时不会被托盘里的正式版挡在门外；优雅退出靠命名管道 `SnapFlow.Dev.<pid>` 完成。
  但两者共用 `%LOCALAPPDATA%\SnapFlow` 的数据与全局快捷键，开发时建议先退出正式版。
- 监视循环内按 `r` 手动触发重建，`q` 或 `Ctrl+C` 只停监视、不关程序。

> 热重载桥（`src/Jietu/DevBridge.cs`）只在 `Debug` 编译里存在，`--dev` 也只在该配置下生效，
> 正式发布包完全不受影响。

### 热重载能改什么、不能改什么

- **能热更**：增删/重排元素、改属性（颜色/边距/尺寸/文本）、改 Style 与资源字典、改 DataTemplate。
- **要重启**（VS 工具条会提示）：新增类、新增 code-behind 事件处理器、改 `x:Class`、
  x:Bind 表达式引用了新属性 —— 这些交给流程 B。
- 另外 Debug → 窗口 里还有「实时可视化树」「实时属性资源管理器」，运行时直接拖属性、找到满意值再写回 XAML。

---

## 📁 项目结构

```text
jietu/
├─ src/Jietu/                 # 主程序源码（WinUI 3 / .NET 10，x64）
│  ├─ Services/               # 抓屏、OCR、剪贴板、历史库、OSD、打包自测等服务
│  │  └─ Pipeline/            # 截图后的处理流水线（OCR / 翻译 / AI / 分享等）
│  ├─ Models/                 # 数据模型与设置
│  └─ Assets/                 # 应用图标等资源
├─ installer/                 # 安装与打包
│  ├─ build.ps1               # 一键打包：发布 → 安装包 → 便携包 → 自测 → 校验
│  ├─ Jietu.iss               # Inno Setup 安装脚本
│  ├─ RELEASE_NOTES.md        # 发布说明模板（打包时写入 dist/）
│  └─ 使用说明.txt            # 便携版随包说明
├─ tools/IconGen/             # 应用图标生成小工具
├─ scripts/fetch-nupkgs.ps1   # 离线 NuGet 依赖抓取脚本（填充 .nupkg-cache）
├─ scripts/dev.ps1            # 开发热重载循环：保存 → 构建 → 优雅重启 → 还原窗口
├─ dist/                      # 最终发布产物（安装包 / 便携 ZIP / SHA256 / 发布说明）
└─ .nupkg-cache/              # 离线 NuGet 缓存（构建依赖，勿提交）
```

> 三个产物目录的分工，只看这里就够了：
> - `src/Jietu/bin/` —— 日常开发编译输出（`dotnet build`；csproj 已固定 RID，
>   Debug/Release 都落在 `.../win-x64/` 一处），仅供开发调试，不要手动分发；
> - `.dev-run/app/` —— `scripts/dev.ps1` 的运行目录（产物镜像，见上节「开发调试」，
>   可随时删除，不含用户数据）；
> - `artifacts/publish/` —— `build.ps1` 的自包含发布中间产物（打包后可删）；
> - `dist/` —— 唯一对外分发的目录：安装包、便携 ZIP、SHA256SUMS、发布说明。
>
> 想直接运行最新代码：解压 `dist/` 里的便携 ZIP 双击 `SnapFlow.exe` 即可。

> 构建默认从本地 `.nupkg-cache/` 还原依赖（离线可用）；换机器或缓存缺失时，
> 先联网执行 `.\scripts\fetch-nupkgs.ps1` 重新抓取依赖包。

---

## 🛠️ 编译与打包

### 环境需求
- Windows 10 (19041+) 或 Windows 11 (x64)
- [.NET 10 SDK](https://dotnet.microsoft.com/)
- [Inno Setup 6](https://jrsoftware.org/isdl.php)

### 一键打包发布
在 PowerShell 中运行打包脚本：
```powershell
.\installer\build.ps1
```
打包脚本将自动完成：
1. 编译 Release x64 自包含程序 (`SnapFlow.exe`)
2. 剥离调试符号以缩减体积
3. 生成现代化安装程序向导 (`SnapFlow-Setup-v<x.y.z>.exe`)
4. 制作免安装绿色便携包 (`SnapFlow-v<x.y.z>-win-x64-Portable.zip`)
5. 自动运行质量与单元自测
6. 生成 SHA256SUMS 校验清单与 Release Notes

> 版本号唯一来源是 `src/Jietu/Jietu.csproj` 的 `<Version>`：安装包、便携包文件名、发布说明、随包说明与设置「关于」页均由打包脚本或运行时自动同步，升版只改这一处。

---

## 📄 开源许可

本项目基于 [MIT License](LICENSE) 开源发布。
