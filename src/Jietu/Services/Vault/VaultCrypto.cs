using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Isopoh.Cryptography.Argon2;
using Isopoh.Cryptography.SecureArray;

namespace Jietu;

/// <summary>
/// 库文件头：magic + 版本 + KDF 参数 + nonce。明文段（任何拿到文件的人都能读），
/// 敏感信息全部在 AES-GCM 密文里；头的职责是让解密方知道怎么派生密钥。
/// </summary>
public readonly struct VaultHeader
{
    public const int SaltLength = 16;
    public const int NonceLength = 12;
    public const int KeyLength = 32;
    public const int TagLength = 16;
    /// <summary>8 magic + 4 version + 16 salt + 4×3 参数 + 12 nonce。</summary>
    public const int HeaderLength = 8 + 4 + SaltLength + 12 + NonceLength;
    public const int CurrentVersion = 1;

    /// <summary>Argon2id 合法参数边界：内存 8MiB～1GiB，迭代 1～16，并行 1～16。</summary>
    public const int MinMemoryKiB = 8 * 1024;
    public const int MaxMemoryKiB = 1024 * 1024;
    public const int MinIterations = 1;
    public const int MaxIterations = 16;
    public const int MinParallelism = 1;
    public const int MaxParallelism = 16;

    /// <summary>新库默认参数：64MiB / 4 迭代 / 4 并行，桌面端解一次约半秒。</summary>
    public const int DefaultIterations = 4;
    public const int DefaultMemoryKiB = 64 * 1024;
    public const int DefaultParallelism = 4;

    public int Version { get; init; }
    public byte[] Salt { get; init; }
    public int Iterations { get; init; }
    public int MemoryKiB { get; init; }
    public int Parallelism { get; init; }
    public byte[] Nonce { get; init; }

    public static VaultHeader NewHeader()
    {
        return new VaultHeader
        {
            Version = CurrentVersion,
            Salt = RandomNumberGenerator.GetBytes(SaltLength),
            Iterations = DefaultIterations,
            MemoryKiB = DefaultMemoryKiB,
            Parallelism = DefaultParallelism,
            Nonce = RandomNumberGenerator.GetBytes(NonceLength),
        };
    }

    /// <summary>序列化为固定 52 字节头；写文件时拼在密文前。</summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[HeaderLength];
        Encoding.ASCII.GetBytes("SPVAULT\0").AsSpan().CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), (uint)Version);
        Salt.AsSpan().CopyTo(bytes.AsSpan(12, SaltLength));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28, 4), Iterations);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(32, 4), MemoryKiB);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(36, 4), Parallelism);
        Nonce.AsSpan().CopyTo(bytes.AsSpan(40, NonceLength));
        return bytes;
    }

    /// <summary>
    /// 从文件数据解析头。magic、版本、参数边界全部校验；不合法返回 false——
    /// 校验必须在派生密钥之前完成，防止畸形参数（比如 1GiB 内存）变成 DoS 放大器。
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out VaultHeader header)
    {
        header = default;
        if (data.Length < HeaderLength)
        {
            return false;
        }

        if (!data[..8].SequenceEqual("SPVAULT\0"u8))
        {
            return false;
        }

        var version = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[8..12]);
        if (version is < 1 or > CurrentVersion)
        {
            return false;
        }

        var salt = data.Slice(12, SaltLength).ToArray();
        var iterations = BinaryPrimitives.ReadInt32LittleEndian(data[28..]);
        var memoryKiB = BinaryPrimitives.ReadInt32LittleEndian(data[32..]);
        var parallelism = BinaryPrimitives.ReadInt32LittleEndian(data[36..]);
        if (iterations is < MinIterations or > MaxIterations
            || memoryKiB is < MinMemoryKiB or > MaxMemoryKiB
            || parallelism is < MinParallelism or > MaxParallelism)
        {
            return false;
        }

        header = new VaultHeader
        {
            Version = version,
            Salt = salt,
            Iterations = iterations,
            MemoryKiB = memoryKiB,
            Parallelism = parallelism,
            Nonce = data.Slice(40, NonceLength).ToArray(),
        };
        return true;
    }
}

/// <summary>
/// 库容器的加解密：Argon2id 派生密钥 + AES-256-GCM。纯函数、无 UI 依赖、全单测。
/// 解密失败（密码错误 / 篡改 / 损坏）一律返回 null，由调用方区分提示。
/// </summary>
public static class VaultCrypto
{
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Argon2id 派生 32 字节密钥。参数来自头，桌面端 64MiB/4 次约半秒。</summary>
    public static byte[] DeriveKey(ReadOnlySpan<byte> password, VaultHeader header)
    {
        var config = new Argon2Config
        {
            Type = Argon2Type.HybridAddressing, // HybridAddressing 即 Argon2id
            Version = Argon2Version.Nineteen,
            TimeCost = header.Iterations,
            MemoryCost = header.MemoryKiB,
            Lanes = header.Parallelism,
            Threads = header.Parallelism,
            Password = password.ToArray(),
            Salt = header.Salt,
            HashLength = VaultHeader.KeyLength,
            ClearPassword = false,
            SecureArrayCall = new DefaultWindowsSecureArrayCall(),
        };
        using var argon2 = new Argon2(config);
        using var secureHash = argon2.Hash();
        return secureHash.Buffer.ToArray();
    }

    /// <summary>加密整份库载荷，返回「密文+16 字节 tag」，调用方负责拼在头后面写盘。</summary>
    public static byte[] Seal(VaultHeader header, byte[] key, VaultData data)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(data, PayloadOptions);
        var output = new byte[payload.Length + VaultHeader.TagLength];
        using var aes = new AesGcm(key, VaultHeader.TagLength);
        aes.Encrypt(header.Nonce, payload, output.AsSpan(..^VaultHeader.TagLength), output.AsSpan(^VaultHeader.TagLength..));
        return output;
    }

    /// <summary>解密并反序列化；任何失败返回 null（密码错误、篡改、格式损坏走同一条路）。</summary>
    public static VaultData? Open(VaultHeader header, byte[] key, ReadOnlySpan<byte> body)
    {
        if (body.Length < VaultHeader.TagLength)
        {
            return null;
        }

        try
        {
            var payload = new byte[body.Length - VaultHeader.TagLength];
            using var aes = new AesGcm(key, VaultHeader.TagLength);
            aes.Decrypt(header.Nonce, body[..^VaultHeader.TagLength], body[^VaultHeader.TagLength..], payload);
            return JsonSerializer.Deserialize<VaultData>(payload, PayloadOptions);
        }
        catch (Exception)
        {
            // AuthenticationFailure / JsonException 都归为「解不开」：不区分原因可避免
            // 给攻击者一个篡改探测的侧信道，用户侧统一提示主密码错误。
            return null;
        }
    }
}
