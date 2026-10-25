using System.Security.Cryptography;

namespace Jietu;

/// <summary>
/// 随机密码 / 密码短语生成。随机源用 CSPRNG（<see cref="RandomNumberGenerator"/>），绝不使用 Random。
///
/// 熵的口径（SEC-01）：密码短语的熵由**实际词表规模**实算——<c>log2(WordListSize) × 词数</c>。
/// 词表用 EFF 大词表 7776 词（6^5），每词 12.93 bit，6 词 = 77.5 bit ⇒ 界面显示 77。
/// 任何"4 词 ≈ 84 bit"式的结论都来自旧的 128 词小词表，已经作废。
/// </summary>
public static class PasswordGenerator
{
    public const int MinLength = 6;
    public const int MaxLength = 128;

    public const int MinWords = VaultLimits.MinPassphraseWords;
    public const int MaxWords = VaultLimits.MaxPassphraseWords;

    /// <summary>默认词数：6 词 ≈ 77 bit，是"能记住"和"够结实"的交点。</summary>
    public const int DefaultWords = VaultLimits.DefaultPassphraseWords;

    // 生成用的字符集刻意去掉易混淆字符（l/1/I/O/0），避免用户抄错。
    public const string LowerSet = "abcdefghijkmnopqrstuvwxyz";
    public const string UpperSet = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    public const string DigitSet = "23456789";
    public const string SymbolSet = "!@#$%^&*()-_=+[]{};:,.<>?";

    // 强度计用的字符类池：字母/数字取完整字符类（26/26/10，攻击者确实要试全量），
    // 符号取实际 SymbolSet 的长度，保证"生成器用什么"与"强度计算什么"不会悄悄漂移。
    // 宁可低估也不要高估——强度计高估会让人放心使用弱口令。
    public const int LowerPoolSize = 26;
    public const int UpperPoolSize = 26;
    public const int DigitPoolSize = 10;

    /// <summary>符号池大小（= 实际 <see cref="SymbolSet"/> 长度，不再写死的 33）。</summary>
    public static int SymbolPoolSize => SymbolSet.Length;

    /// <summary>词表（EFF 大词表 7776 词）。只读视图，供测试与体检公式复用。</summary>
    public static IReadOnlyList<string> WordList => EFFLargeWordlist.Words;

    /// <summary>词表大小（熵估算与测试锚点用；取自实际数组长度，不写死）。</summary>
    public static int WordListSize => EFFLargeWordlist.Words.Length;

    /// <summary>
    /// 生成随机密码。每个选中的字符类至少出现一次（满足站点策略），再整体洗牌。
    /// 长度不足「选中类数」时抛参数异常，由 UI 层在交互时限制。
    /// </summary>
    public static string Generate(int length, bool lower = true, bool upper = true, bool digits = true, bool symbols = true)
    {
        if (length is < MinLength or > MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        var pools = new List<string>(4);
        if (lower)
        {
            pools.Add(LowerSet);
        }

        if (upper)
        {
            pools.Add(UpperSet);
        }

        if (digits)
        {
            pools.Add(DigitSet);
        }

        if (symbols)
        {
            pools.Add(SymbolSet);
        }

        if (pools.Count == 0)
        {
            throw new ArgumentException("至少选择一个字符类。");
        }

        var allChars = string.Concat(pools);
        var chars = new char[length];
        for (var i = 0; i < pools.Count; i++)
        {
            chars[i] = pools[i][RandomNumberGenerator.GetInt32(pools[i].Length)];
        }

        for (var i = pools.Count; i < length; i++)
        {
            chars[i] = allChars[RandomNumberGenerator.GetInt32(allChars.Length)];
        }

        Shuffle(chars);
        return new string(chars);
    }

    /// <summary>生成「word-word-word-word」风格的密码短语。</summary>
    public static string GeneratePassphrase(int words = DefaultWords, string separator = "-")
    {
        if (words is < MinWords or > MaxWords)
        {
            throw new ArgumentOutOfRangeException(nameof(words));
        }

        var parts = new string[words];
        for (var i = 0; i < words; i++)
        {
            // NextInt32 是无偏的（内部做了拒绝采样），不要换成 % Length。
            parts[i] = EFFLargeWordlist.Words[RandomNumberGenerator.GetInt32(WordListSize)];
        }

        return string.Join(separator, parts);
    }

    /// <summary>粗略熵估计（bit），密码强度计用。</summary>
    public static double EstimateEntropyBits(int length, bool lower, bool upper, bool digits, bool symbols)
    {
        var pool = 0;
        if (lower)
        {
            pool += LowerSet.Length;
        }

        if (upper)
        {
            pool += UpperSet.Length;
        }

        if (digits)
        {
            pool += DigitSet.Length;
        }

        if (symbols)
        {
            pool += SymbolSet.Length;
        }

        return pool == 0 ? 0 : length * Math.Log2(pool);
    }

    /// <summary>密码短语熵（bit）= 词数 × log2(实际词表大小)。</summary>
    public static double EstimatePassphraseEntropyBits(int words) => words * Math.Log2(WordListSize);

    /// <summary>向下取整的整数熵，供界面显示（宁可少报不多报）。</summary>
    public static int PassphraseEntropyBits(int words) => (int)Math.Floor(EstimatePassphraseEntropyBits(words));

    /// <summary>界面上的熵标签（模板化，禁止写死 bit 数）。</summary>
    public static string PassphraseEntropyLabel(int words) => VaultMessages.PassphraseEntropy(words, PassphraseEntropyBits(words));

    /// <summary>
    /// 按实际出现的字符类估算一段已有密码的熵（bit）。池大小见 <see cref="LowerPoolSize"/> 等常量：
    /// 字母/数字按完整字符类、符号按实际 <see cref="SymbolSet"/>，保证口算与生成器一致。
    /// </summary>
    public static (double Bits, int Classes) EstimatePasswordEntropy(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return (0, 0);
        }

        var hasLower = false;
        var hasUpper = false;
        var hasDigit = false;
        var hasSymbol = false;
        foreach (var c in password)
        {
            if (c is >= 'a' and <= 'z')
            {
                hasLower = true;
            }
            else if (c is >= 'A' and <= 'Z')
            {
                hasUpper = true;
            }
            else if (c is >= '0' and <= '9')
            {
                hasDigit = true;
            }
            else
            {
                hasSymbol = true;
            }
        }

        var pool = (hasLower ? LowerPoolSize : 0)
                   + (hasUpper ? UpperPoolSize : 0)
                   + (hasDigit ? DigitPoolSize : 0)
                   + (hasSymbol ? SymbolPoolSize : 0);
        var classes = (hasLower ? 1 : 0) + (hasUpper ? 1 : 0) + (hasDigit ? 1 : 0) + (hasSymbol ? 1 : 0);
        return (pool == 0 ? 0 : password.Length * Math.Log2(pool), classes);
    }

    private static void Shuffle(Span<char> chars)
    {
        for (var i = chars.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
    }
}
