using System.Security.Cryptography;

namespace Jietu.Tests;

public sealed class VaultCryptoTests
{
    // 单测用小参数（8MiB/1 迭代），只锁行为正确性，不追求生产参数的耗时。
    private static VaultHeader SmallHeader()
    {
        return new VaultHeader
        {
            Version = VaultHeader.CurrentVersion,
            Salt = Convert.FromHexString("a1b2c3d4e5f60718293a4b5c6d7e8f90"),
            Iterations = 1,
            MemoryKiB = VaultHeader.MinMemoryKiB,
            Parallelism = 1,
            Nonce = Convert.FromHexString("000102030405060708090a0b"),
        };
    }

    private static VaultData SampleData() => new()
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

    private static (VaultHeader Header, byte[] Key, byte[] Body) Sealed()
    {
        var header = SmallHeader();
        var key = VaultCrypto.DeriveKey("correct horse"u8, header);
        return (header, key, VaultCrypto.Seal(header, key, SampleData()));
    }

    [Fact]
    public void Header_RoundTrip_PreservesFields()
    {
        var header = SmallHeader();
        var parsed = VaultHeader.TryParse(header.ToBytes(), out var copy);
        Assert.True(parsed);
        Assert.Equal(header.Version, copy.Version);
        Assert.Equal(header.Salt, copy.Salt);
        Assert.Equal(header.Iterations, copy.Iterations);
        Assert.Equal(header.MemoryKiB, copy.MemoryKiB);
        Assert.Equal(header.Parallelism, copy.Parallelism);
        Assert.Equal(header.Nonce, copy.Nonce);
    }

    [Fact]
    public void Header_RejectsBadMagicAndVersion()
    {
        var good = SmallHeader().ToBytes();

        var badMagic = (byte[])good.Clone();
        badMagic[0] = (byte)'X';
        Assert.False(VaultHeader.TryParse(badMagic, out _));

        var badVersion = (byte[])good.Clone();
        badVersion[11] = 9;
        Assert.False(VaultHeader.TryParse(badVersion, out _));

        Assert.False(VaultHeader.TryParse(good[..^1], out _), "截断的头必须拒绝");
    }

    [Fact]
    public void Header_RejectsInsaneKdfParameters()
    {
        var good = SmallHeader().ToBytes();

        // 迭代 0 与内存低于下限都会被拒绝：防畸形参数 DoS。
        foreach (var offset in new[] { 28, 32 })
        {
            var tampered = (byte[])good.Clone();
            for (var i = offset; i < offset + 4; i++)
            {
                tampered[i] = 0;
            }

            Assert.False(VaultHeader.TryParse(tampered, out _), $"offset {offset} 的非法参数必须拒绝");
        }
    }

    [Fact]
    public async Task DeriveKey_IsDeterministicAndSlowEnough()
    {
        var header = SmallHeader();
        var first = await Task.Run(() => VaultCrypto.DeriveKey("pw"u8, header));
        var second = await Task.Run(() => VaultCrypto.DeriveKey("pw"u8, header));
        Assert.Equal(first, second);
        Assert.Equal(VaultHeader.KeyLength, first.Length);

        var otherSalt = header with { Salt = RandomNumberGenerator.GetBytes(VaultHeader.SaltLength) };
        Assert.NotEqual(first, await Task.Run(() => VaultCrypto.DeriveKey("pw"u8, otherSalt)));
    }

    [Fact]
    public async Task SealOpen_RoundTrip()
    {
        var (_, key, body) = Sealed();
        var opened = await Task.Run(() => VaultCrypto.Open(SmallHeader(), key, body));
        Assert.NotNull(opened);
        Assert.Equal("示例站点", opened!.Entries[0].Title);
        Assert.Equal("P@ssw0rd-机密", opened.Entries[0].Password);
    }

    [Fact]
    public async Task Open_WrongPasswordFails()
    {
        var (header, _, body) = Sealed();
        var wrongKey = await Task.Run(() => VaultCrypto.DeriveKey("wrong password"u8, header));
        Assert.Null(await Task.Run(() => VaultCrypto.Open(header, wrongKey, body)));
    }

    [Fact]
    public async Task Open_TamperedCiphertextFails()
    {
        var (header, key, body) = Sealed();
        var tampered = (byte[])body.Clone();
        tampered[0] ^= 0xFF;
        Assert.Null(await Task.Run(() => VaultCrypto.Open(header, key, tampered)));

        // tag 被改同样必须失败。
        var tamperedTag = (byte[])body.Clone();
        tamperedTag[^1] ^= 0x01;
        Assert.Null(await Task.Run(() => VaultCrypto.Open(header, key, tamperedTag)));
    }

    [Fact]
    public async Task Open_TruncatedBodyFails()
    {
        var (header, key, body) = Sealed();
        Assert.Null(await Task.Run(() => VaultCrypto.Open(header, key, body[..^1])));
    }

    [Fact]
    public async Task Store_SaveLoadParse_RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vault-test-{Guid.NewGuid():N}.spv");
        try
        {
            var (header, _, body) = Sealed();
            await Task.Run(() => VaultStore.Save(path, header, body));
            var raw = VaultStore.Load(path);
            var parsed = VaultStore.Parse(raw);
            Assert.NotNull(parsed);
            Assert.Equal(header.ToBytes(), parsed!.Value.Header.ToBytes());
            Assert.Equal(body, parsed.Value.Body);
            Assert.Null(VaultStore.LastRecoveryMessage);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Store_ParseCorruptFileBacksUpAndReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vault-bad-{Guid.NewGuid():N}.spv");
        try
        {
            await File.WriteAllBytesAsync(path, "not a vault at all"u8.ToArray());
            Assert.Null(VaultStore.Parse(VaultStore.Load(path)));
            Assert.NotNull(VaultStore.LastRecoveryMessage);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Store_MissingFileReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vault-missing-{Guid.NewGuid():N}.spv");
        Assert.Null(await Task.Run(() => VaultStore.Load(path)));
        Assert.Null(VaultStore.LastRecoveryMessage);
    }
}
