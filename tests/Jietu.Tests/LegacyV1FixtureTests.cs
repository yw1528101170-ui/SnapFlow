using System.Security.Cryptography;

namespace Jietu.Tests;

/// <summary>
/// v1 二进制样本的存在性与不可变性守卫。
///
/// 为什么这两个文件必须**永久入库**：本版本已彻底移除 v1 读取能力，一旦丢失就再也造不出来
/// （需要 1.1.4 的构建）。而"v1 零写盘""备份恢复拒绝旧格式"这些断言全都依赖真实的旧文件。
///
/// 重新生成只在显式设置 <c>SNAPSHOT_REGENERATE_FIXTURES=1</c> 时发生——否则任何一次
/// 误改都会让"旧格式"样本悄悄变成当前代码的产物，测试结论随之失真。
/// </summary>
public sealed class LegacyV1FixtureTests
{
    /// <summary>样本的预期 SHA-256：字节一旦变化就说明样本被动过，必须人工确认。</summary>
    private const string ExpectedVaultSha256 = "7AE46AFEDC05BEF64786B3C99B533DFA3792469B9AC1C5BDA7BDFC91C0A2F738";

    /// <summary>备份样本与库样本内容相同：v1 的"加密备份"就是当时整库的副本。</summary>
    private const string ExpectedBackupSha256 = "7AE46AFEDC05BEF64786B3C99B533DFA3792469B9AC1C5BDA7BDFC91C0A2F738";

    private static string FixtureDirectory
    {
        get
        {
            // 从测试输出目录向上找到仓库根（含 SnapFlow.slnx），避免把路径写死。
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "SnapFlow.slnx")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return Path.Combine(directory!.FullName, "tests", "Jietu.Tests", "Fixtures");
        }
    }

    [Fact]
    public void LegacySamples_ExistWithExpectedBytes()
    {
        var directory = FixtureDirectory;
        var vaultPath = Path.Combine(directory, LegacyV1Fixture.FileName);
        var backupPath = Path.Combine(directory, LegacyV1Fixture.BackupFileName);

        if (Environment.GetEnvironmentVariable("SNAPSHOT_REGENERATE_FIXTURES") == "1")
        {
            LegacyV1Fixture.WriteTo(directory);
        }

        Assert.True(File.Exists(vaultPath), $"缺少 v1 样本：{vaultPath}");
        Assert.True(File.Exists(backupPath), $"缺少 v1 备份样本：{backupPath}");

        // 样本必须是"能自洽重建"的：与构造器输出逐字节一致，避免有人手工改过。
        Assert.Equal(LegacyV1Fixture.Create(), File.ReadAllBytes(vaultPath));
        Assert.Equal(LegacyV1Fixture.CreateBackup(), File.ReadAllBytes(backupPath));

        if (ExpectedVaultSha256 != "__PLACEHOLDER__")
        {
            Assert.Equal(ExpectedVaultSha256, Sha256(vaultPath));
        }

        if (ExpectedBackupSha256 != "__PLACEHOLDER__")
        {
            Assert.Equal(ExpectedBackupSha256, Sha256(backupPath));
        }
    }

    [Fact]
    public void LegacySample_IsRecognizedAsV1AndNotAsV2()
    {
        var read = VaultStore.ClassifyBytes(LegacyV1Fixture.Create());

        Assert.Equal(VaultFileKind.LegacyV1, read.Kind);

        // v1 头 52 字节，若被当成 v2 读会得到荒谬的 salt/nonce——版本分派必须先于一切结构解析。
        Assert.Null(read.Body);
        Assert.Equal(default, read.Header);
    }

    private static string Sha256(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
