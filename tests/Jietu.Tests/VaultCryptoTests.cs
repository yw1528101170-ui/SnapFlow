using System.Security.Cryptography;
using System.Text;

namespace Jietu.Tests;

/// <summary>
/// 加解密与存储的**行为**断言（字节布局与 AAD 篡改矩阵在 VaultFormatV2Tests）：
/// ① 派生密钥的确定性与参数护栏；
/// ② 密钥生命周期：Dispose 必须真的清零，且之后访问要抛而不是静默返回全 0；
/// ③ 认证失败/结构不合法必须分类，不能一律 null——调用方要靠它决定"再试一次"还是"别再试"；
/// ④ 存储层的原子性与副作用边界：坏文件可备份、v1 与认证失败**零写盘**。
/// </summary>
public sealed class VaultCryptoTests
{
    [Fact]
    public void DeriveKey_IsDeterministicAndSaltSensitive()
    {
        var header = VaultTestKit.SmallHeader();
        using var first = VaultCrypto.DeriveKey("pw"u8, header);
        using var second = VaultCrypto.DeriveKey("pw"u8, header);

        Assert.Equal(first.Bytes.ToArray(), second.Bytes.ToArray());
        Assert.Equal(VaultKey.Length, first.Bytes.Length);

        // 换盐必须换出不同密钥：盐是"同口令不同库"的唯一区分来源。
        var otherSalt = header with { Salt = RandomNumberGenerator.GetBytes(VaultFormat.SaltLength) };
        using var third = VaultCrypto.DeriveKey("pw"u8, otherSalt);
        Assert.NotEqual(first.Bytes.ToArray(), third.Bytes.ToArray());
    }

    [Fact]
    public void DeriveKey_RejectsOutOfBoundsParametersBeforeDeriving()
    {
        // 这些值只可能来自被篡改的头；派生之前必须挡住，否则就是 DoS 放大器。
        var badIterations = VaultTestKit.SmallHeader() with { Iterations = 0 };
        Assert.Throws<ArgumentOutOfRangeException>(() => VaultCrypto.DeriveKey("pw"u8, badIterations));

        var badMemory = VaultTestKit.SmallHeader() with { MemoryKiB = VaultFormat.MaxMemoryKiB + 1 };
        Assert.Throws<ArgumentOutOfRangeException>(() => VaultCrypto.DeriveKey("pw"u8, badMemory));

        var badSalt = VaultTestKit.SmallHeader() with { Salt = new byte[8] };
        Assert.Throws<ArgumentException>(() => VaultCrypto.DeriveKey("pw"u8, badSalt));

        Assert.Throws<ArgumentException>(() => VaultCrypto.DeriveKey(ReadOnlySpan<byte>.Empty, VaultTestKit.SmallHeader()));
    }

    [Fact]
    public void VaultKey_DisposeZeroesBufferAndBlocksAccess()
    {
        var key = VaultKey.FromBytes(RandomNumberGenerator.GetBytes(VaultKey.Length));
        Assert.False(key.IsDisposed);
        Assert.Contains(key.Bytes.ToArray(), b => b != 0);

        key.Dispose();

        Assert.True(key.IsDisposed);
        // 静默返回全 0 会让"密钥已失效"在加密库里才暴露，堆栈更难查，所以必须抛。
        Assert.Throws<ObjectDisposedException>(() => _ = key.Bytes.Length);
        Assert.All(key.SnapshotForDiagnostics().ToArray(), b => Assert.Equal(0, b));

        key.Dispose(); // 幂等：重复释放不抛
    }

    [Fact]
    public void VaultKey_CloneIsIndependent()
    {
        using var original = VaultKey.Random();
        using var clone = original.Clone();
        Assert.Equal(original.Bytes.ToArray(), clone.Bytes.ToArray());

        clone.Dispose();
        Assert.False(original.IsDisposed);
        Assert.Contains(original.Bytes.ToArray(), b => b != 0);
    }

    [Fact]
    public void SealOpen_RoundTrip()
    {
        using var sample = VaultTestKit.Seal();
        var result = VaultTestKit.OpenFile(sample.File);

        Assert.True(result.Ok);
        Assert.Equal(VaultOpenStatus.Ok, result.Status);
        Assert.Equal("示例站点", result.Data!.Entries[0].Title);
        Assert.Equal("P@ssw0rd-机密", result.Data.Entries[0].Password);
    }

    [Fact]
    public void Open_WrongPassword_ReportsAuthenticationFailed()
    {
        using var sample = VaultTestKit.Seal();
        var result = VaultTestKit.OpenFile(sample.File, "definitely the wrong password");

        // 密码错必须可重试：分类是 AuthenticationFailed，不是 StructureInvalid。
        Assert.False(result.Ok);
        Assert.Equal(VaultOpenStatus.AuthenticationFailed, result.Status);
        Assert.Null(result.Data);
    }

    [Fact]
    public void Open_TamperedCiphertextOrTag_ReportsAuthenticationFailed()
    {
        using var sample = VaultTestKit.Seal();

        var tamperedBody = (byte[])sample.File.Clone();
        tamperedBody[VaultFormat.HeaderLength] ^= 0xFF;
        Assert.Equal(VaultOpenStatus.AuthenticationFailed, VaultTestKit.OpenFile(tamperedBody).Status);

        var tamperedTag = (byte[])sample.File.Clone();
        tamperedTag[^1] ^= 0x01;
        Assert.Equal(VaultOpenStatus.AuthenticationFailed, VaultTestKit.OpenFile(tamperedTag).Status);
    }

    [Fact]
    public void Open_BodyShorterThanTag_ReportsStructureInvalid()
    {
        var header = VaultTestKit.SmallHeader().WithPlaintextLength(8);
        using var key = VaultKey.Random();

        var result = VaultCrypto.Open(header, key, new byte[VaultFormat.TagLength - 1]);

        Assert.False(result.Ok);
        Assert.Equal(VaultOpenStatus.StructureInvalid, result.Status);
    }

    [Fact]
    public void Open_CipherLengthMismatch_ReportsStructureInvalid()
    {
        using var sample = VaultTestKit.Seal();
        var read = VaultStore.ClassifyBytes(sample.File);
        var lyingHeader = read.Header with { CipherLength = read.Header.CipherLength + 1 };

        var result = VaultCrypto.Open(lyingHeader, sample.Key, read.Body!);

        // 长度在派生密钥前就该判掉；走到这里也必须拒绝，不能把畸形长度喂给 AesGcm。
        Assert.Equal(VaultOpenStatus.StructureInvalid, result.Status);
    }

    [Fact]
    public void SealFile_AlwaysProducesFreshNonceAndMatchingCipherLength()
    {
        using var sample = VaultTestKit.Seal();
        var resealed = VaultCrypto.SealFile(VaultTestKit.SampleData(), sample.Header, sample.Key, out var newHeader);

        // 同密钥复用 nonce 会毁掉 GCM：每次写盘必须换新的。
        Assert.NotEqual(sample.Header.Nonce, newHeader.Nonce);
        Assert.Equal(resealed.Length, VaultFormat.HeaderLength + (int)newHeader.CipherLength);
        Assert.True(VaultTestKit.OpenFile(resealed).Ok);
    }

    [Fact]
    public void SealPayload_RejectsHeaderWithStaleCipherLength()
    {
        var header = VaultTestKit.SmallHeader(); // CipherLength = 0
        using var key = VaultKey.Random();

        // AAD 用的是实际写盘的头；长度对不上就说明调用方要写一个"自己解不开"的文件，必须当场拒绝。
        Assert.Throws<ArgumentException>(() => VaultCrypto.SealPayload(header, key, "{}"u8));
    }

    [Fact]
    public void Store_SaveThenRead_RoundTripsThroughPipeline()
    {
        var directory = VaultTestKit.NewTempDir();
        try
        {
            var path = Path.Combine(directory, "vault.spv");
            using var sample = VaultTestKit.Seal();

            VaultStore.Save(path, sample.File);

            var read = VaultStore.Read(path);
            Assert.Equal(VaultFileKind.V2, read.Kind);
            Assert.NotNull(read.Body);

            var opened = VaultOpenPipeline.Open(read, Encoding.UTF8.GetBytes(VaultTestKit.Password));
            Assert.True(opened.Ok);
            Assert.Equal("示例站点", opened.Data!.Entries[0].Title);

            // 落盘后目录里只该有库文件本体：tmp 必须被清掉，不能留半截文件。
            Assert.Equal([Path.GetFileName(path)], VaultTestKit.Snapshot(directory));
        }
        finally
        {
            VaultTestKit.Cleanup(directory);
        }
    }

    [Fact]
    public void Store_ReadOfMissingFile_IsMissingAndCreatesNothing()
    {
        var directory = VaultTestKit.NewTempDir();
        try
        {
            var path = Path.Combine(directory, "vault.spv");

            var read = VaultStore.Read(path);

            Assert.Equal(VaultFileKind.Missing, read.Kind);
            Assert.Empty(read.Raw);
            Assert.Null(read.Body);
            Assert.Empty(VaultTestKit.Snapshot(directory));
        }
        finally
        {
            VaultTestKit.Cleanup(directory);
        }
    }

    /// <summary>
    /// SEC-03 的核心反向断言：遇到 v1 库，**字节与目录内容都不许变**。
    /// 这类 bug 的典型形态是"提示写得很正确，但顺手落了个备份文件"。
    /// </summary>
    [Fact]
    public void Store_LegacyV1File_IsClassifiedAndNeverTouched()
    {
        var directory = VaultTestKit.NewTempDir();
        try
        {
            var path = Path.Combine(directory, "vault.spv");
            File.WriteAllBytes(path, LegacyV1Fixture.Create());

            var hashBefore = VaultTestKit.Sha256Of(path);
            var snapshotBefore = VaultTestKit.Snapshot(directory);

            var read = VaultStore.Read(path);

            Assert.Equal(VaultFileKind.LegacyV1, read.Kind);
            // 非 V2 一律不给 Body：调用方在类型上就拿不到"可解密的体"。
            Assert.Null(read.Body);

            // v1 不在损坏备份白名单里，即使被显式要求也不许写盘。
            Assert.Null(VaultStore.TryCreateCorruptBackup(path, read.Kind));

            // 重复触发（用户连点解锁）同样不许产生任何产物。
            for (var i = 0; i < 10; i++)
            {
                _ = VaultStore.Read(path);
                _ = VaultStore.TryCreateCorruptBackup(path, VaultFileKind.LegacyV1);
            }

            Assert.Equal(hashBefore, VaultTestKit.Sha256Of(path));
            Assert.Equal(snapshotBefore, VaultTestKit.Snapshot(directory));
        }
        finally
        {
            VaultTestKit.Cleanup(directory);
        }
    }

    /// <summary>认证失败可能只是一次手滑，绝不能生成 .corrupt 产物。</summary>
    [Fact]
    public void Store_AuthenticationFailure_ProducesNoBackup()
    {
        var directory = VaultTestKit.NewTempDir();
        try
        {
            var path = Path.Combine(directory, "vault.spv");
            using var sample = VaultTestKit.Seal();
            VaultStore.Save(path, sample.File);

            var hashBefore = VaultTestKit.Sha256Of(path);
            var snapshotBefore = VaultTestKit.Snapshot(directory);

            var read = VaultStore.Read(path);
            var result = VaultOpenPipeline.Open(read, "wrong password"u8);

            Assert.Equal(VaultOpenStatus.AuthenticationFailed, result.Status);
            // kind 仍是 V2，不在备份白名单内。
            Assert.Null(VaultStore.TryCreateCorruptBackup(path, read.Kind));
            Assert.Equal(hashBefore, VaultTestKit.Sha256Of(path));
            Assert.Equal(snapshotBefore, VaultTestKit.Snapshot(directory));
        }
        finally
        {
            VaultTestKit.Cleanup(directory);
        }
    }

    /// <summary>结构损坏的文件有保存价值：备份一次，且同一会话不重复堆积。</summary>
    [Fact]
    public void Store_CorruptBackup_IsCreatedOncePerSession()
    {
        var directory = VaultTestKit.NewTempDir();
        try
        {
            var path = Path.Combine(directory, "vault.spv");
            File.WriteAllBytes(path, "not a vault at all"u8.ToArray());
            VaultStore.ResetCorruptBackupTracking();

            var first = VaultStore.TryCreateCorruptBackup(path, VaultFileKind.Unknown);
            Assert.NotNull(first);
            Assert.True(File.Exists(first));
            Assert.Equal("not a vault at all"u8.ToArray(), File.ReadAllBytes(first!));

            // 用户反复点解锁不该在目录里堆出十份同样的垃圾。
            for (var i = 0; i < 10; i++)
            {
                Assert.Null(VaultStore.TryCreateCorruptBackup(path, VaultFileKind.Unknown));
            }

            Assert.Single(
                Directory.GetFiles(directory),
                f => f.Contains(".corrupt-", StringComparison.Ordinal));
            // 原文件留在原地，绝不覆盖、不删除。
            Assert.True(File.Exists(path));
        }
        finally
        {
            VaultStore.ResetCorruptBackupTracking();
            VaultTestKit.Cleanup(directory);
        }
    }

    [Fact]
    public void Store_CorruptBackup_IsCappedByCount()
    {
        var directory = VaultTestKit.NewTempDir();
        try
        {
            var path = Path.Combine(directory, "vault.spv");
            File.WriteAllBytes(path, "garbage"u8.ToArray());

            // 预置已达上限的旧备份：新备份必须让位，而不是无限增长。
            for (var i = 0; i < VaultLimits.MaxCorruptBackups; i++)
            {
                File.WriteAllBytes(path + ".corrupt-2024010" + i + "-000000", "old"u8.ToArray());
            }

            VaultStore.ResetCorruptBackupTracking();

            Assert.Null(VaultStore.TryCreateCorruptBackup(path, VaultFileKind.Unknown));
            Assert.Equal(
                VaultLimits.MaxCorruptBackups,
                Directory.GetFiles(directory).Count(f => f.Contains(".corrupt-", StringComparison.Ordinal)));
        }
        finally
        {
            VaultStore.ResetCorruptBackupTracking();
            VaultTestKit.Cleanup(directory);
        }
    }

    [Fact]
    public void Store_Save_RotatesRollingBackups()
    {
        var directory = VaultTestKit.NewTempDir();
        try
        {
            var path = Path.Combine(directory, "vault.spv");
            using var sample = VaultTestKit.Seal();

            // 连续保存多次：每版都换 nonce，所以文件内容确实不同。
            for (var i = 0; i < 6; i++)
            {
                var file = VaultCrypto.SealFile(VaultTestKit.SampleData(), sample.Header, sample.Key, out _);
                VaultStore.Save(path, file);
                Assert.True(VaultTestKit.OpenFile(file).Ok);
            }

            var backups = VaultStore.ListBackups(path);
            Assert.True(backups.Count <= 3, $"滚动备份应只留 3 份，实际 {backups.Count} 份");
            Assert.True(File.Exists(path));
        }
        finally
        {
            VaultTestKit.Cleanup(directory);
        }
    }
}
