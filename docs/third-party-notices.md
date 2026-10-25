# 第三方组件与数据来源声明

SnapFlow 使用的第三方组件与数据来源如下，此处仅做署名与版本锚定，完整许可文本见各项目官方仓库。
本文件随安装包分发（安装后位于安装目录，可在 设置 → 关于 SnapFlow 点「查看声明」打开）。

## 运行时依赖

| 组件 | 版本 | 许可证 | 用途 |
| :--- | :--- | :--- | :--- |
| [Microsoft.WindowsAppSDK](https://github.com/microsoft/WindowsAppSDK)（WinUI / Foundation / Runtime） | 2.3.x / 2.5.x | MIT | WinUI 3 界面框架与运行时 |
| [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite/) | 10.0.0 | MIT | 剪贴板/截图历史库 ADO.NET 提供程序 |
| [SQLitePCLRaw.bundle_e_sqlite3](https://github.com/ericsink/SQLitePCL.raw) | 2.1.14 | Apache-2.0 | SQLite 原生库绑定（内置的 e_sqlite3 为 SQLite 官方公开域代码） |
| [ZXing.Net](https://github.com/micjahn/ZXing.Net) | 0.16.10 | Apache-2.0 | 二维码/条形码识别 |
| [Isopoh.Cryptography.Argon2](https://github.com/mheyman/Isopoh.Cryptography.Argon2) | 2.0.0 | MIT | 密码库 KDF（Argon2id，含其内置的 Blake2b 实现） |
| .NET 运行时与 Windows SDK 投影（随包自包含分发） | .NET 10 | MIT | 运行时 |

## 数据来源

- **EFF 大型词表（EFF Large Wordlist）**：密码库「密码短语」生成器的词表来源为
  [Electronic Frontier Foundation 发布的 diceware 词表](https://www.eff.org/dice)（7776 词），
  依据 [CC BY 3.0](https://creativecommons.org/licenses/by/3.0/) 使用。
  嵌入文件：`src/Jietu/Services/Vault/EFFLargeWordlist.cs`。
  原始数据 © Electronic Frontier Foundation。

如你对署名或许可细节有疑问，请通过仓库 Issues 联系。
