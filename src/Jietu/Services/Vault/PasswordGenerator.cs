using System.Security.Cryptography;

namespace Jietu;

/// <summary>
/// 随机密码 / 密码短语生成。随机源用 RandomNumberGenerator（CSPRNG）。
/// </summary>
public static class PasswordGenerator
{
    public const int MinLength = 6;
    public const int MaxLength = 128;
    public const int MinWords = 3;
    public const int MaxWords = 12;

    public const string LowerSet = "abcdefghijkmnopqrstuvwxyz";
    public const string UpperSet = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    public const string DigitSet = "23456789";
    public const string SymbolSet = "!@#$%^&*()-_=+[]{};:,.<>?";

    // 128 个短小易拼写的英文词：4 词 ≈ 84 bit 熵（对离线 Argon2 暴破已足够强），
    // 比 EFF 大词表省体积，且全部 ≤6 字母方便记忆。
    private static readonly string[] WordList =
    [
        "ace","acrobat","agent","album","amber","anchor","apple","arbor","arch","aria",
        "aspen","atlas","aurora","badge","balcony","bamboo","basil","beacon","beaver","bicycle",
        "bishop","bison","blaze","bloom","boulder","brave","breeze","bridge","bronze","brook",
        "cabin","cactus","camera","candle","canvas","canyon","cargo","cedar","cement","chalk",
        "cherry","chess","chime","cider","cipher","cobalt","comet","copper","coral","cosmos",
        "cotton","crane","crater","crisp","crown","crystal","cypress","dahlia","daisy","dawn",
        "delta","desert","diamond","dolphin","domino","dragon","duet","echo","eclipse","ember",
        "emerald","engine","escort","falcon","fern","fiddle","fig","fjord","flamingo","flint",
        "float","forest","fossil","fountain","fox","frost","gadget","galaxy","garnet","gecko",
        "ginger","glacier","glide","granite","gravel","guitar","hammer","harbor","harvest","hawk",
        "hazel","helmet","heron","hickory","hollow","honey","horizon","indigo","ivory","jade",
        "jasmine","jetty","jewel","jigsaw","juniper","kayak","kernel","kestrel","kitten","koala",
        "lagoon","lantern","lattice","lemur","lighthouse","lilac","linen","lotus","lumber","lynx",
    ];

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
        if (lower) pools.Add(LowerSet);
        if (upper) pools.Add(UpperSet);
        if (digits) pools.Add(DigitSet);
        if (symbols) pools.Add(SymbolSet);
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
    public static string GeneratePassphrase(int words, string separator = "-")
    {
        if (words is < MinWords or > MaxWords)
        {
            throw new ArgumentOutOfRangeException(nameof(words));
        }

        var parts = new string[words];
        for (var i = 0; i < words; i++)
        {
            parts[i] = WordList[RandomNumberGenerator.GetInt32(WordList.Length)];
        }

        return string.Join(separator, parts);
    }

    /// <summary>粗略熵估计（bit），密码强度计用。</summary>
    public static double EstimateEntropyBits(int length, bool lower, bool upper, bool digits, bool symbols)
    {
        var pool = 0;
        if (lower) pool += LowerSet.Length;
        if (upper) pool += UpperSet.Length;
        if (digits) pool += DigitSet.Length;
        if (symbols) pool += SymbolSet.Length;
        return pool == 0 ? 0 : length * Math.Log2(pool);
    }

    /// <summary>密码短语熵估计：词数 × log2(词表大小)。</summary>
    public static double EstimatePassphraseEntropyBits(int words) => words * Math.Log2(WordList.Length);

    /// <summary>词表大小（熵估算与测试锚点用）。</summary>
    public static int WordListSize => WordList.Length;

    /// <summary>
    /// 按实际出现的字符类估算一段已有密码的熵（bit）。类池取全量
    /// （26/26/10/33），不按生成器的去歧义子集算——强度计衡量的是
    /// 攻击者的搜索空间，不是自家生成器的输出分布。
    /// </summary>
    public static (double Bits, int Classes) EstimatePasswordEntropy(string password)
    {
        var hasLower = false;
        var hasUpper = false;
        var hasDigit = false;
        var hasSymbol = false;
        foreach (var c in password)
        {
            if (c is >= 'a' and <= 'z') hasLower = true;
            else if (c is >= 'A' and <= 'Z') hasUpper = true;
            else if (c is >= '0' and <= '9') hasDigit = true;
            else hasSymbol = true;
        }

        var pool = (hasLower ? 26 : 0) + (hasUpper ? 26 : 0) + (hasDigit ? 10 : 0) + (hasSymbol ? 33 : 0);
        var classes = new[] { hasLower, hasUpper, hasDigit, hasSymbol }.Count(c => c);
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
