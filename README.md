# SnapFlow (瞬流)

<p align="center">
  <b>轻量、现代且高效率的 Windows 智能截图、贴图、剪贴板、本地密码库、AI 视觉与屏幕性能监控工作流工具</b>
</p>

<p align="center">
  <a href="https://github.com/yw1528101170-ui/SnapFlow/actions/workflows/ci.yml"><img src="https://github.com/yw1528101170-ui/SnapFlow/actions/workflows/ci.yml/badge.svg" alt="CI"/></a>
  <a href="https://github.com/yw1528101170-ui/SnapFlow/releases/latest"><img src="https://img.shields.io/badge/release-v1.1.6-blue" alt="release"/></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-green" alt="license"/></a>
</p>

---

## 📥 下载

前往 **[Releases 发布页](https://github.com/yw1528101170-ui/SnapFlow/releases/latest)** 下载最新版本：

| 文件 | 适合 |
| :--- | :--- |
| `SnapFlow-Setup-v<x.y.z>.exe` | 推荐安装版：向导式安装、开机自启、可一键卸载 |
| `SnapFlow-v<x.y.z>-win-x64-Portable.zip` | 绿色免安装版：解压即用 |
| `SHA256SUMS.txt` | 完整性校验清单（`Get-FileHash` / `sha256sum` 比对） |

---

## 🌟 核心特性

- **智能选区与元素吸附**：鼠标悬停自动吸附窗口、按钮或面板边界，支持像素级微调与放大镜辅助；配矩形、椭圆、箭头、画笔、荧光笔、文字、马赛克、聚光灯、Emoji 表情与步骤序号标注。
- **贴图置顶 (Pin)**：截图一键钉在桌面最前，支持缩放、拖动、复制。
- **滚动长截图**：智能拼接长网页、聊天记录与长图表；托盘另提供 1~10 秒延时截图。
- **离线 OCR**：毫秒级提取图片文字，支持复制与翻译；另有一键解析屏幕二维码/条形码。
- **取色器**：放大镜下精准定位像素，显示 RGB/HEX，按 `C` 直接复制色值。
- **剪贴板历史**：`Alt+V` 唤出，统一管理最近的文字与图像，支持搜索回溯。
- **本地密码库**：Argon2id + AES-256-GCM 整库加密，主密码不出本机；Windows Hello 快速解锁、加密备份与恢复、Chrome/Edge/Bitwarden CSV 导入、回收站与密码体检；窗口防截屏、机密复制不进剪贴板历史且定时清除、空闲/锁屏/睡眠自动锁定。配套浏览器扩展（`ext/snapflow-vault`，随安装包分发）支持 Chrome/Edge 站点自动填充与强密码生成。
- **AI 视觉对话**：截图一键发给多模态大模型，流式回答、多轮追问、Markdown 渲染（在设置中配置 API 端点与密钥）。
- **屏幕性能监控悬浮窗 (OSD)**：极简置顶挂件，鼠标全穿透零干扰，实时监控 GPU（利用率/显存/温度，LUID 多卡适配）、CPU 实时频率与利用率、内存与网络速率，口径对齐任务管理器；支持左上/右上角停靠及截图时智能避让。
- **流畅动效**：OSD「灵动岛」阻尼弹簧展开收起、数值指数缓动；系统 Toast 玻璃胶囊上浮淡入、宽度弹性形变。
- **开箱即用**：自带完整 .NET 运行时，常驻托盘，内存与 CPU 占用极低；Fluent Design 深浅色自适应。

---

## ⌨️ 快捷键速查

| 功能 | 默认快捷键 | 说明 |
| :--- | :--- | :--- |
| **区域截图** | `Alt + A` | 激活截图遮罩，可框选、标注、贴图或长截图 |
| **剪贴板历史** | `Alt + V` | 唤出剪贴板历史面板 |
| **密码库** | `Alt + P` | 打开/锁定本地加密密码库（未创建时进入引导） |
| **复制颜色值** | `C` | 取色器激活时复制光标像素 HEX 色值 |
| **撤销标注** | `Ctrl + Z` | 撤销上一次画笔或形状 |
| **完成并复制** | `Enter` / 双击选区 | 复制到剪贴板并退出遮罩 |
| **取消 / 关闭** | `Esc` | 退出截图或关闭当前窗口 |

> 快捷键可在「设置」中自由修改（右键托盘图标 → 设置）。

---

## 🛠️ 构建与打包

环境：Windows 10 (19041+) / Windows 11（x64）、[.NET 10 SDK](https://dotnet.microsoft.com/)、[Inno Setup 6](https://jrsoftware.org/isdl.php)。

```powershell
.\installer\build.ps1
```

脚本自动完成：编译 Release x64 自包含 → 剥离调试符号 → 生成安装包与便携 ZIP → 运行单元测试 → 在 `dist/` 产出 SHA256SUMS 与 Release Notes。

- **安装版**：Inno Setup 向导，装到当前用户目录（无需管理员），可选桌面快捷方式与开机自启；卸载时可选择是否删除用户数据（默认保留，重装可找回）。
- **便携版**：解压即用，数据保存在 `%LOCALAPPDATA%\SnapFlow`，仅主动开启自启时写一条自启项。
- 版本号唯一来源是 `src/Jietu/Jietu.csproj` 的 `<Version>`：所有产物文件名、发布说明与「关于」页均自动同步，升版只改这一处。
- 构建默认从 `.nupkg-cache/` 离线还原依赖；换机器或缓存缺失时先联网执行 `.\scripts\fetch-nupkgs.ps1`。

---

## 🔥 开发调试

| 你在改什么 | 用哪条 | 延迟 |
| :--- | :--- | :--- |
| XAML 布局 / 颜色 / Style / DataTemplate | VS F5 + XAML 热重载 | ~1 秒 |
| C# 逻辑 / 新窗口 / 事件处理器 / `x:Class` / x:Bind | `scripts\dev.ps1` 自动重启 | ~17 秒 |

- **VS F5**：平台必须选 **x64**（`Any CPU` 无法启动），启动档选 **`SnapFlow (dev)`**，必须 **F5**（热重载依赖调试器附加）。托盘应用不弹窗口，`--dev` 会自动打开设置页。能热更属性/Style/DataTemplate；新增类、事件处理器、改 `x:Class` 走下一条。
- **`dev.ps1`**：保存即构建 → 旧进程优雅退出 → 在 `.dev-run\app` 重新拉起并还原窗口；构建期间旧版本持续可用，检测到 VS 正在调试会自动避让，`--dev` 独立单实例通道不与正式版冲突。`-Mode run/build`、`-NoBuild` 等用法与全部细节见脚本头部注释。

---

## 📁 项目结构

```text
jietu/
├─ src/Jietu/                 # 主程序源码（WinUI 3 / .NET 10，x64）
│  ├─ Services/               # 抓屏、OCR、剪贴板、历史库、OSD、密码库等服务
│  │  ├─ Pipeline/            # 截图后的处理流水线（OCR / 翻译 / AI / 分享等）
│  │  └─ Vault/               # 密码库：加密容器、会话、桥接、导入导出、体检
│  ├─ Models/                 # 数据模型与设置
│  └─ Assets/                 # 应用图标等资源
├─ ext/snapflow-vault/        # 浏览器自动填充扩展（MV3，随安装包分发）
├─ installer/                 # build.ps1（一键打包）、Jietu.iss、发布说明模板、随包说明
├─ docs/                      # 第三方组件与数据来源声明
├─ tools/IconGen/             # 应用图标生成小工具
├─ scripts/                   # dev.ps1（热重载循环）、fetch-nupkgs.ps1（离线依赖抓取）
├─ dist/                      # 唯一对外分发目录：安装包 / 便携 ZIP / SHA256 / 发布说明
└─ .nupkg-cache/              # 离线 NuGet 缓存（构建依赖，勿提交）
```

> 产物目录分工：`src/Jietu/bin/` 是开发编译输出（勿手动分发）；`.dev-run/app/` 是 `dev.ps1` 的运行镜像（可随时删）；`artifacts/publish/` 是打包中间产物（脚本自动清理）。想直接运行最新代码：解压 `dist/` 里的便携 ZIP 双击 `SnapFlow.exe`。

---

## 📄 开源许可

本项目基于 [MIT License](LICENSE) 开源发布。
