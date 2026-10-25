using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Jietu;

/// <summary>
/// RFC 6238 TOTP（SHA1/SHA256/SHA512，Base32 密钥），兼容 Google Authenticator 风格的
/// otpauth:// 配置。纯函数、全单测（用 RFC 6238 附录 B 官方测试向量锁行为）。
/// </summary>
public static class Totp
{
    public const int DefaultPeriodSeconds = 30;
    public const int DefaultDigits = 6;

    /// <summary>计算当前（或指定时刻）的验证码。算法按密钥长度自动选择，与主流验证器一致。</summary>
    public static string Compute(string base32Secret, DateTimeOffset? at = null, int digits = DefaultDigits, int period = DefaultPeriodSeconds)
    {
        var key = DecodeBase32(base32Secret);
        if (key.Length == 0)
        {
            throw new ArgumentException("TOTP 密钥不是合法的 Base32。");
        }

        return Compute(key, at?.ToUnixTimeSeconds() ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(), digits, period);
    }

    public static string Compute(byte[] key, long unixSeconds, int digits, int period)
    {
        // RFC 6238：计数器 = floor(unixTime / period)，大端 8 字节。
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, unixSeconds / period);

        HMAC hmac = key.Length >= 64 ? new HMACSHA512(key) : key.Length >= 32 ? new HMACSHA256(key) : new HMACSHA1(key);
        using (hmac)
        {
            var hash = hmac.ComputeHash(counter.ToArray());
            // 动态截断（RFC 4226 §5.3）。
            var offset = hash[^1] & 0x0F;
            var binary = ((hash[offset] & 0x7F) << 24)
                         | ((hash[offset + 1] & 0xFF) << 16)
                         | ((hash[offset + 2] & 0xFF) << 8)
                         | (hash[offset + 3] & 0xFF);
            return (binary % (int)Math.Pow(10, digits)).ToString(new string('0', digits));
        }
    }

    /// <summary>当前验证码剩余有效秒数，验证器倒计时 UI 用。</summary>
    public static int RemainingSeconds(DateTimeOffset? at = null, int period = DefaultPeriodSeconds)
        => (int)(period - (at?.ToUnixTimeSeconds() ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()) % period);

    /// <summary>RFC 4648 Base32 解码：忽略空格、连字符与填充；出现非法字符返回空数组。</summary>
    public static byte[] DecodeBase32(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0;
        var value = 0;
        var output = new List<byte>(input.Length * 5 / 8 + 1);
        foreach (var ch in input.ToUpperInvariant())
        {
            if (ch is ' ' or '-' or '=')
            {
                continue;
            }

            var index = alphabet.IndexOf(ch);
            if (index < 0)
            {
                return [];
            }

            value = (value << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((value >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return output.ToArray();
    }

    /// <summary>把 otpauth://totp/... URI 解析为 Base32 密钥；不是合法 URI 返回 null。</summary>
    public static string? SecretFromOtpAuthUri(string uri)
    {
        if (!uri.StartsWith("otpauth://totp/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var pair in uri[(uri.IndexOf('?') + 1)..].Split('&'))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0].Equals("secret", StringComparison.OrdinalIgnoreCase))
            {
                var secret = Uri.UnescapeDataString(kv[1]).Trim();
                return DecodeBase32(secret).Length > 0 ? secret : null;
            }
        }

        return null;
    }
}
