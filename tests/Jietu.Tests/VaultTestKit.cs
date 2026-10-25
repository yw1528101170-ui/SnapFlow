using System.Security.Cryptography;
using System.Text;

namespace Jietu.Tests;

/// <summary>
/// 密码库测试的公共样本与工具：小 KDF 参数（8MiB/1 迭代）保证单测秒级完成，
/// 只锁行为正确性，不追求生产参数的耗时。
/// </summary>
internal static class VaultTestKit
{
    public const string Password = "correct horse battery staple";

    public static VaultHeader SmallHeader() => VaultHeader.NewV2(1, VaultFormat.MinMemoryKiB, 1);

    public static VaultData SampleData() => new()
    {
        Entries =
        [
            new VaultEntry
            {
                Id = "e1",
                Title = "示例站点",
                Username = "alice@example.com",
                Password = "P@ssw0rd-机密",
                Url = "https://example.com",
                Tags = ["work"],
            },
        ],
    };

    /// <summary>一份已加封的样本：头、密钥与完整文件字节。</summary>
    internal sealed class SealedSample : IDisposable
    {
        public required VaultHeader Header { get; init; }

        public required VaultKey Key { get; init; }

        public required byte[] File { get; init; }

        public void Dispose() => Key.Dispose();
    }

    /// <summary>用 <see cref="Password"/> 加封一份样本（返回值需 using 释放密钥）。</summary>
    public static SealedSample Seal(string password = Password, VaultData? data = null)
    {
        var header = SmallHeader();
        var key = VaultCrypto.DeriveKey(Encoding.UTF8.GetBytes(password), header);
        var file = VaultCrypto.SealFile(data ?? SampleData(), header, key, out var usedHeader);
        return new SealedSample { Header = usedHeader, Key = key, File = file };
    }

    /// <summary>用给定口令解锁一段文件字节（完整 11 步流水线）。</summary>
    public static VaultOpenResult OpenFile(byte[] file, string password = Password)
    {
        var read = VaultStore.ClassifyBytes(file);
        return VaultOpenPipeline.Open(read, Encoding.UTF8.GetBytes(password));
    }

    public static string NewTempDir()
    {
        var directory = Path.Combine(Path.GetTempPath(), "snapflow-vault-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    public static void Cleanup(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // 测试清理失败不该让测试变红。
        }
    }

    public static string Sha256Of(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    public static string[] Snapshot(string directory)
        => Directory.GetFiles(directory)
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray()!;
}
