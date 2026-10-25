using System.Buffers.Binary;
using System.Text;

namespace Jietu.Tests;

/// <summary>
/// v2 容器格式规格的逐字节锁定：
/// ① 头 80 字节的每个字段都在正确偏移、正确字节序；
/// ② AAD 恒为 97 字节且前缀是域分隔标签；
/// ③ 分类矩阵（空/短/坏 magic/v1/v2/版本 9/版本 0/参数越界/reserved 非零/长度不符）；
/// ④ **AAD 篡改矩阵 ≥30 点全部失败**——这是 SEC-02 的核心断言，也是防"头部认证实现漏字段"的唯一门禁。
/// </summary>
public sealed class VaultFormatV2Tests
{
    [Fact]
    public void Header_ByteLayout_MatchesSpecification()
    {
        var header = VaultTestKit.SmallHeader().WithPlaintextLength(100);
        var bytes = header.ToBytes();

        Assert.Equal(80, bytes.Length);
        Assert.Equal(VaultFormat.HeaderLength, bytes.Length);
        Assert.Equal("SPVAULT\0"u8.ToArray(), bytes[..8].ToArray());
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4)));
        Assert.Equal(VaultFormat.KdfArgon2id, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4)));
        Assert.Equal(VaultFormat.AeadAes256Gcm, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16, 4)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(20, 4)));
        Assert.Equal(VaultFormat.MinMemoryKiB, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24, 4)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(28, 4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(32, 4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(36, 4)));
        Assert.Equal(header.Salt, bytes[40..56].ToArray());
        Assert.Equal(header.Nonce, bytes[56..68].ToArray());
        Assert.Equal(116u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(68, 4)));
        Assert.Equal(0ul, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(72, 8)));
    }

    [Fact]
    public void Header_RoundTrip_PreservesEveryField()
    {
        var header = VaultTestKit.SmallHeader().WithPlaintextLength(256);

        // TryReadHeader 的第 7 步要求 raw.Length == 80 + cipherLength，
        // 所以必须喂完整文件字节；只给 80 字节的头会被正确地判成"被截断"。
        var raw = new byte[VaultFormat.HeaderLength + (int)header.CipherLength];
        header.ToBytes().CopyTo(raw, 0);

        Assert.True(VaultFormat.TryReadHeader(raw, out var parsed, out var status));
        Assert.Equal(VaultOpenStatus.Ok, status);
        Assert.Equal(header.Version, parsed.Version);
        Assert.Equal(header.KdfAlgorithm, parsed.KdfAlgorithm);
        Assert.Equal(header.AeadAlgorithm, parsed.AeadAlgorithm);
        Assert.Equal(header.Iterations, parsed.Iterations);
        Assert.Equal(header.MemoryKiB, parsed.MemoryKiB);
        Assert.Equal(header.Parallelism, parsed.Parallelism);
        Assert.Equal(header.Flags, parsed.Flags);
        Assert.Equal(header.Reserved0, parsed.Reserved0);
        Assert.Equal(header.Reserved1, parsed.Reserved1);
        Assert.Equal(header.CipherLength, parsed.CipherLength);
        Assert.Equal(header.Salt, parsed.Salt);
        Assert.Equal(header.Nonce, parsed.Nonce);
    }

    [Fact]
    public void Aad_IsFixed97BytesWithDomainLabel()
    {
        // 必须用同一个 header 实例：SmallHeader() 每次都生成新的随机 salt/nonce。
        var header = VaultTestKit.SmallHeader();
        var aad = VaultFormat.BuildAad(header);
        Assert.Equal(97, aad.Length);
        Assert.Equal(97, VaultFormat.AadLength);
        Assert.Equal("SnapFlowVault/v2"u8.ToArray(), aad[..16].ToArray());
        Assert.Equal(0x00, aad[16]);
        // 头区必须与 ToBytes() 完全一致：AAD 里"重新序列化一遍"是自毁式实现。
        Assert.Equal(header.ToBytes(), aad[17..].ToArray());
    }

    [Fact]
    public void Classify_CoversAllFileKinds()
    {
        using var sample = VaultTestKit.Seal();
        Assert.Equal(VaultFileKind.V2, VaultFormat.Classify(sample.File));
        Assert.Equal(VaultFileKind.Unknown, VaultFormat.Classify([]));
        Assert.Equal(VaultFileKind.Unknown, VaultFormat.Classify("not a vault at all"u8));

        // v1：只改版本号，其余照旧（长度还是 v2 的，但版本分派优先，绝不进结构校验）。
        var legacy = (byte[])sample.File.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(legacy.AsSpan(8, 4), 1);
        Assert.Equal(VaultFileKind.LegacyV1, VaultFormat.Classify(legacy));

        // 版本 9：过新。
        var newer = (byte[])sample.File.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(newer.AsSpan(8, 4), 9);
        Assert.Equal(VaultFileKind.NewerVersion, VaultFormat.Classify(newer));

        // 版本 0：不是我们认识的格式。
        var zero = (byte[])sample.File.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(zero.AsSpan(8, 4), 0);
        Assert.Equal(VaultFileKind.Unknown, VaultFormat.Classify(zero));

        // 截断（长度与 cipherLength 不符）。
        Assert.Equal(VaultFileKind.StructurallyInvalid, VaultFormat.Classify(sample.File[..^1]));
        // 追加一个字节同样不合法。
        Assert.Equal(VaultFileKind.StructurallyInvalid, VaultFormat.Classify([.. sample.File, 0x00]));
        // 只有 magic 没有版本的超短文件。
        Assert.Equal(VaultFileKind.StructurallyInvalid, VaultFormat.Classify("SPVAULT\0"u8));
    }

    [Fact]
    public void TryReadHeader_RejectsUnknownSemanticsBeforeKeyDerivation()
    {
        using var sample = VaultTestKit.Seal();

        foreach (var (offset, value, label) in new (int Offset, uint Value, string Label)[]
                 {
                     (12, 2, "未知 kdfAlg"),
                     (16, 2, "未知 aeadAlg"),
                     (32, 1, "未知 flags"),
                     (36, 1, "reserved0 非零"),
                 })
        {
            var tampered = (byte[])sample.File.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(tampered.AsSpan(offset, 4), value);
            Assert.False(VaultFormat.TryReadHeader(tampered, out _, out var status), label);
            Assert.Equal(VaultOpenStatus.StructureInvalid, status);
            Assert.Equal(VaultFileKind.StructurallyInvalid, VaultFormat.Classify(tampered));
        }

        // reserved1（8 字节）非零同样必须拒绝。
        var reserved1 = (byte[])sample.File.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(reserved1.AsSpan(72, 8), 1);
        Assert.Equal(VaultFileKind.StructurallyInvalid, VaultFormat.Classify(reserved1));

        // KDF 参数越界（内存 0 / 迭代 0 / 并行 0）。
        foreach (var offset in new[] { 20, 24, 28 })
        {
            var tampered = (byte[])sample.File.Clone();
            BinaryPrimitives.WriteInt32LittleEndian(tampered.AsSpan(offset, 4), 0);
            Assert.Equal(VaultFileKind.StructurallyInvalid, VaultFormat.Classify(tampered));
        }
    }

    /// <summary>
    /// AAD 篡改矩阵：头部 13 个字段各取若干取样点，逐字节翻转后
    /// **没有任何路径能返回 Ok**。原文件字节必须保持不变（本测试只操作副本）。
    /// </summary>
    [Fact]
    public void TamperedHeaderByteMatrix_NeverOpens()
    {
        using var sample = VaultTestKit.Seal();
        var originalHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sample.File));

        // 取样点覆盖：magic / version / kdfAlg / aeadAlg / iterations / memory / parallelism /
        // flags / reserved0 / salt / nonce / cipherLength / reserved1。
        int[] offsets =
        [
            0, 7,                 // magic
            8, 9,                 // formatVersion
            12, 13,               // kdfAlg
            16, 17,               // aeadAlg
            20, 21,               // iterations
            24, 25,               // memoryKiB
            28, 29,               // parallelism
            32, 33,               // flags
            36, 37, 38, 39,       // reserved0
            40, 45, 47, 50, 55,   // salt
            56, 60, 63, 67,       // nonce
            68, 69, 70, 71,       // cipherLength
            72, 75, 78, 79,       // reserved1
        ];

        var failures = new List<string>();
        var checks = 0;
        foreach (var offset in offsets)
        {
            // 每个取样点翻两位：低位翻转常被"低位字段"接住，高位翻转更容易撞上越界判定。
            foreach (var mask in new byte[] { 0x01, 0x80 })
            {
                var tampered = (byte[])sample.File.Clone();
                tampered[offset] ^= mask;
                checks++;

                var read = VaultStore.ClassifyBytes(tampered);
                if (read.Kind != VaultFileKind.V2)
                {
                    // 结构级拒绝：根本没进 KDF，这是最强的失败形态。
                    continue;
                }

                var result = VaultOpenPipeline.Open(read, Encoding.UTF8.GetBytes(VaultTestKit.Password));
                if (result.Ok)
                {
                    failures.Add($"offset {offset} mask 0x{mask:X2} 竟然解开了");
                }
            }
        }

        Assert.True(checks >= 60, $"取样点太少（{checks}），不足以覆盖头部 13 个字段");
        Assert.Empty(failures);
        Assert.Equal(originalHash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sample.File)));
    }

    [Fact]
    public void TamperedSaltOrNonce_FailsAuthentication()
    {
        using var sample = VaultTestKit.Seal();

        foreach (var offset in new[] { 40, 55, 56, 67 })
        {
            var tampered = (byte[])sample.File.Clone();
            tampered[offset] ^= 0xFF;
            var read = VaultStore.ClassifyBytes(tampered);

            // salt/nonce 变化不破坏结构：必须一直走到 GCM 才失败，且失败原因必须是"认证失败"。
            Assert.Equal(VaultFileKind.V2, read.Kind);
            var result = VaultOpenPipeline.Open(read, Encoding.UTF8.GetBytes(VaultTestKit.Password));
            Assert.Equal(VaultOpenStatus.AuthenticationFailed, result.Status);
        }
    }

    [Fact]
    public void TamperedBody_FailsAuthentication()
    {
        using var sample = VaultTestKit.Seal();
        var read = VaultStore.ClassifyBytes(sample.File);

        foreach (var offset in new[] { VaultFormat.HeaderLength, sample.File.Length - 1 })
        {
            var tampered = (byte[])sample.File.Clone();
            tampered[offset] ^= 0x01;
            var tamperedRead = VaultStore.ClassifyBytes(tampered);
            Assert.Equal(VaultFileKind.V2, tamperedRead.Kind);
            var result = VaultOpenPipeline.Open(tamperedRead, Encoding.UTF8.GetBytes(VaultTestKit.Password));
            Assert.Equal(VaultOpenStatus.AuthenticationFailed, result.Status);
        }

        Assert.True(read.Body!.Length > 0);
    }

    [Fact]
    public void PayloadSchemaTooNew_IsRejectedNotSilentlyRead()
    {
        var payload = """{"schemaVersion":3,"entries":[],"recycleBin":[],"trustedBrowsers":[],"healthIgnores":[],"clipboardClearSeconds":30}"""u8;
        var result = VaultCrypto.ParsePayload(payload);
        Assert.Equal(VaultOpenStatus.PayloadTooNew, result.Status);
        Assert.Null(result.Data);
    }

    [Fact]
    public void KdfBoundsAreEnforcedByPolicyConstants()
    {
        var header = VaultTestKit.SmallHeader();
        Assert.False(VaultFormat.IsBelowCurrentKdfPolicy(header.WithPlaintextLength(1) with
        {
            Iterations = VaultKdfPolicy.Iterations,
            MemoryKiB = VaultKdfPolicy.MemoryKiB,
            Parallelism = VaultKdfPolicy.Parallelism,
        }));

        Assert.True(VaultFormat.IsBelowCurrentKdfPolicy(header));
        Assert.Equal(VaultFormat.CurrentFormatVersion, VaultKdfPolicy.NewVaultHeader().Version);
        Assert.Equal(VaultKdfPolicy.MemoryKiB, VaultKdfPolicy.NewVaultHeader().MemoryKiB);
    }
}
