using System.Text;

namespace Jietu.Tests;

public sealed class TotpTests
{
    // RFC 6238 附录 B 官方测试向量：密钥为 ASCII "12345678901234567890"（SHA1）。
    private const string RfcSecretBase32 = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    [Theory]
    [InlineData(59, "94287082")]
    [InlineData(1111111109, "07081804")]
    [InlineData(1111111111, "14050471")]
    [InlineData(1234567890, "89005924")]
    [InlineData(2000000000, "69279037")]
    [InlineData(20000000000, "65353130")]
    public void Compute_MatchesRfc6238Vectors(long unixTime, string expected)
    {
        var key = Encoding.ASCII.GetBytes("12345678901234567890");
        Assert.Equal(expected, Totp.Compute(key, unixTime, digits: 8, period: 30));
    }

    [Fact]
    public void Compute_Base32Secret_MatchesRawKey()
    {
        var fromBase32 = Totp.Compute(RfcSecretBase32, DateTimeOffset.FromUnixTimeSeconds(59), digits: 8);
        Assert.Equal("94287082", fromBase32);
    }

    [Fact]
    public void Compute_IsSixDigitsByDefaultAndStableWithinPeriod()
    {
        // 基准时刻必须对齐 30 秒周期边界（1700000010 / 30 整除），否则 +29 秒已跨周期。
        var at = DateTimeOffset.FromUnixTimeSeconds(1_700_000_010);
        var code = Totp.Compute(RfcSecretBase32, at);
        Assert.Matches("^\\d{6}$", code);
        Assert.Equal(code, Totp.Compute(RfcSecretBase32, at.AddSeconds(29)));
        Assert.NotEqual(code, Totp.Compute(RfcSecretBase32, at.AddSeconds(31)));
    }

    [Fact]
    public void Compute_Sha256AndSha512KeysAreAccepted()
    {
        // 32 字节密钥走 SHA256，64 字节走 SHA512；只需验证可计算且为 6 位数字。
        var sha256Key = new byte[32];
        var sha512Key = new byte[64];
        Random.Shared.NextBytes(sha256Key);
        Random.Shared.NextBytes(sha512Key);
        Assert.Matches("^\\d{6}$", Totp.Compute(sha256Key, 59, 6, 30));
        Assert.Matches("^\\d{6}$", Totp.Compute(sha512Key, 59, 6, 30));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY======")]
    [InlineData("fo", "MZXQ====")]
    [InlineData("foo", "MZXW6===")]
    [InlineData("foob", "MZXW6YQ=")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI======")]
    public void DecodeBase32_MatchesRfc4648Vectors(string ascii, string base32)
    {
        Assert.Equal(Encoding.ASCII.GetBytes(ascii), Totp.DecodeBase32(base32));
        Assert.Equal(Encoding.ASCII.GetBytes(ascii), Totp.DecodeBase32(base32.ToLowerInvariant()));
    }

    [Fact]
    public void DecodeBase32_RejectsGarbage()
    {
        Assert.Equal([], Totp.DecodeBase32("abc1")); // 1 不在 RFC 4648 字母表
        Assert.Equal([], Totp.DecodeBase32("!!!"));
        Assert.Equal([], Totp.DecodeBase32(""));
    }

    [Fact]
    public void SecretFromOtpAuthUri_ParsesAndRejectsGarbage()
    {
        var secret = Totp.SecretFromOtpAuthUri("otpauth://totp/GitHub:alice?secret=JBSWY3DPEHPK3PXP&issuer=GitHub");
        Assert.Equal("JBSWY3DPEHPK3PXP", secret);

        Assert.Null(Totp.SecretFromOtpAuthUri("https://example.com/?secret=JBSW"));
        Assert.Null(Totp.SecretFromOtpAuthUri("otpauth://totp/x?secret=!!!"));
    }

    [Fact]
    public void RemainingSeconds_CountsDownWithinPeriod()
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(30);
        Assert.Equal(30, Totp.RemainingSeconds(at));
        Assert.Equal(1, Totp.RemainingSeconds(at.AddSeconds(29)));
    }
}
