# v1 密码库二进制样本

这两个文件是 **SnapFlow 1.1.x 版本的密码库容器样本**，用于验证新版本对旧格式的处理行为。
它们**必须永久保留在仓库里**。

## 文件

| 文件 | 说明 |
| :--- | :--- |
| `legacy-vault-v1.spv` | v1 库文件样本（52 字节明文头 + AES-256-GCM 密文） |
| `legacy-vault-v1-backup.spvb` | v1 加密备份样本，内容与上面相同（当年的"加密备份"就是整库副本） |
| `LegacyV1Fixture.cs` | 样本的构造器，字节可复现 |

两个文件的 SHA-256 都是：

```
7AE46AFEDC05BEF64786B3C99B533DFA3792469B9AC1C5BDA7BDFC91C0A2F738
```

## 为什么必须保留

v2 升级**彻底移除了 v1 读取能力**，代码里不再有任何能生成 v1 容器的实现。这两个文件是唯一还能
证明"旧库长什么样"的东西，一旦丢失就再也造不出来（需要回到 1.1.4 的构建）。

依赖它们的断言：

- `Store_LegacyV1File_IsClassifiedAndNeverTouched`：v1 库必须被识别、且**字节与目录内容零变化**
- `OpenEncryptedBackup_RejectsLegacyV1Backup`：从备份恢复时，v1 备份必须被明确拒绝
- `LegacySample_IsRecognizedAsV1AndNotAsV2`：版本分派必须先于一切结构解析

## 样本内容

样本里的凭据全部是**构造值，不是真实凭据**：

| 字段 | 值 |
| :--- | :--- |
| 主密码（原文） | `fixture-legacy-master-password-2026` |
| 条目 1 | 标题 `example.com`，用户名 `test-user-01`，网址 `https://example.com` |
| 条目 2 | 标题 `example.org`，用户名 `test-user-01`，网址 `https://example.org` |
| 备注 | `fixture` |
| 口令字段 | `fixture-only-not-a-real-credential` |

KDF 参数固定为 Argon2id / 1 迭代 / 8 MiB / 1 并行，salt 与 nonce 为固定常量，
时间戳固定为 `2025-01-01T00:00:00Z`——因此每次构造的字节完全相同。

## 重新生成

仅在确需重建时（例如 `LegacyV1Fixture.cs` 本身被有意修改）：

```bash
SNAPSHOT_REGENERATE_FIXTURES=1 dotnet test tests/Jietu.Tests --filter "FullyQualifiedName~LegacyV1FixtureTests"
```

重新生成后**必须同步更新** `LegacyV1FixtureTests.cs` 里的两个 `Expected*Sha256` 常量，
否则 `LegacySamples_ExistWithExpectedBytes` 会失败——这是刻意设计的摩擦，防止样本被悄悄换掉
而测试仍然"全部通过"。
