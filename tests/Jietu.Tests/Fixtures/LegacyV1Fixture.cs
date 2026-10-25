using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Isopoh.Cryptography.Argon2;
using Isopoh.Cryptography.SecureArray;

namespace Jietu.Tests;

/// <summary>
/// v1 容器样本的**自建**构造器。
///
/// 为什么自己写而不是从 git 历史恢复旧代码：本版本已彻底移除 v1 读取能力，
/// 拿旧实现来生成样本会把"已删掉的能力"重新引回工程。v1 的字节布局是公开的
/// （见下方），照布局拼一遍更小、更可控，也不会被未来的重构带走。
///
/// v1 布局（共 52 字节头，随后是 AES-GCM 密文 + 16 字节 tag）：
/// <code>
/// 0..8    magic      "SPVAULT\0"
/// 8..12   version    uint32 LE = 1
/// 12..28  salt       16 字节
/// 28..32  iterations int32 LE
/// 32..36  memoryKiB  int32 LE
/// 36..40  parallelism int32 LE
/// 40..52  nonce      12 字节
/// </code>
/// v1 **没有 AAD**，这正是本次升级要修的核心缺陷：头不参与认证，改 KDF 参数不会被发现。
///
/// 确定性：salt / nonce / 内容 / 时间戳全部固定，因此 <see cref="Create"/> 的输出是逐字节
/// 可复现的——<c>Fixtures</c> 目录里那两个二进制文件就是它的输出（见同目录 README.md）。
/// </summary>
internal static class LegacyV1Fixture
{
    /// <summary>样本主密码原文（仅用于文档与人工复现，测试不需要用它解密）。</summary>
    public const string Password = "fixture-legacy-master-password-2026";

    public const string FileName = "legacy-vault-v1.spv";

    public const string BackupFileName = "legacy-vault-v1-backup.spvb";

    private const int HeaderLength = 52;

    private static readonly byte[] Salt = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");

    private static readonly byte[] Nonce = Convert.FromHexString("0102030405060708090A0B0C");

    /// <summary>固定时间戳：让样本字节可复现（DateTimeOffset.UtcNow 会让每次生成都不同）。</summary>
    private static readonly DateTimeOffset FixedUtc = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>构造一个真实的 v1 容器（旧版 SnapFlow 1.1.4 能打开的那种）。</summary>
    public static byte[] Create(string password = Password)
    {
        var header = BuildHeader();
        var payload = BuildPayload();
        var body = new byte[payload.Length + 16];
        using (var aes = new AesGcm(DeriveV1Key(password, Salt), 16))
        {
            // v1 没有 associatedData 参数：头不参与认证，这是本次格式升级要修的缺陷本身。
            aes.Encrypt(Nonce, payload, body.AsSpan(..^16), body.AsSpan(^16..));
        }

        CryptographicOperations.ZeroMemory(payload);
        var file = new byte[header.Length + body.Length];
        header.CopyTo(file, 0);
        body.CopyTo(file, header.Length);
        return file;
    }

    /// <summary>同内容、但扩展名为 .spvb 的"备份样本"（模拟用户当年导出的加密备份）。</summary>
    public static byte[] CreateBackup(string password = Password) => Create(password);

    /// <summary>把样本写到指定目录（仅在需要重新生成 fixtures 时调用）。</summary>
    public static void WriteTo(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, FileName), Create());
        File.WriteAllBytes(Path.Combine(directory, BackupFileName), CreateBackup());
    }

    private static byte[] BuildHeader()
    {
        var header = new byte[HeaderLength];
        "SPVAULT\0"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), 1); // version = 1
        Salt.CopyTo(header.AsSpan(12, 16));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28, 4), 1);      // iterations
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(32, 4), 8192);    // memoryKiB
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(36, 4), 1);      // parallelism
        Nonce.CopyTo(header.AsSpan(40, 12));
        return header;
    }

    /// <summary>v1 载荷：只有 entries / trustedBrowsers / clipboardClearSeconds，没有 schemaVersion。</summary>
    private static byte[] BuildPayload()
    {
        var payload = new
        {
            entries = new object[]
            {
                new
                {
                    id = "fixture-1",
                    kind = "login",
                    title = "example.com",
                    username = "test-user-01",
                    password = "fixture-only-not-a-real-credential",
                    url = "https://example.com",
                    notes = "fixture",
                    tags = new[] { "fixture" },
                    totpSecret = "",
                    createdUtc = FixedUtc,
                    updatedUtc = FixedUtc,
                },
                new
                {
                    id = "fixture-2",
                    kind = "login",
                    title = "example.org",
                    username = "test-user-01",
                    password = "fixture-only-not-a-real-credential",
                    url = "https://example.org",
                    notes = "fixture",
                    tags = new[] { "fixture" },
                    totpSecret = "",
                    createdUtc = FixedUtc,
                    updatedUtc = FixedUtc,
                },
            },
            trustedBrowsers = Array.Empty<object>(),
            clipboardClearSeconds = 30,
        };

        // 属性名已经是 camelCase，不需要命名策略；不让任何"格式化"选项进来，保证字节可复现。
        return JsonSerializer.SerializeToUtf8Bytes(payload);
    }

    private static byte[] DeriveV1Key(string password, byte[] salt)
    {
        var utf8 = System.Text.Encoding.UTF8.GetBytes(password);
        try
        {
            var config = new Argon2Config
            {
                Type = Argon2Type.HybridAddressing,
                Version = Argon2Version.Nineteen,
                TimeCost = 1,
                MemoryCost = 8192,
                Lanes = 1,
                Threads = 1,
                Password = utf8.ToArray(),
                Salt = salt,
                HashLength = 32,
                ClearPassword = true,
                SecureArrayCall = new DefaultWindowsSecureArrayCall(),
            };
            using var argon2 = new Argon2(config);
            using var secureHash = argon2.Hash();
            var key = new byte[32];
            secureHash.Buffer.AsSpan(0, 32).CopyTo(key);
            CryptographicOperations.ZeroMemory(secureHash.Buffer);
            return key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(utf8);
        }
    }
}
