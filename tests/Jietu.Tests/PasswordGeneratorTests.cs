using System.Security.Cryptography;

namespace Jietu.Tests;

public sealed class PasswordGeneratorTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(64)]
    [InlineData(128)]
    public void Generate_HasRequestedLength(int length)
    {
        Assert.Equal(length, PasswordGenerator.Generate(length).Length);
    }

    [Fact]
    public void Generate_ContainsEverySelectedClass()
    {
        for (var i = 0; i < 32; i++)
        {
            var password = PasswordGenerator.Generate(16);
            Assert.Contains(password, c => c is >= 'a' and <= 'z');
            Assert.Contains(password, c => c is >= 'A' and <= 'Z');
            Assert.Contains(password, c => c is >= '0' and <= '9');
        }
    }

    [Fact]
    public void Generate_RespectsSubsetOfClasses()
    {
        var digitsOnly = PasswordGenerator.Generate(12, lower: false, upper: false, symbols: false);
        Assert.All(digitsOnly, c => Assert.True(c is >= '0' and <= '9'));
    }

    [Fact]
    public void Generate_AvoidsLookalikeCharacters()
    {
        for (var i = 0; i < 64; i++)
        {
            var password = PasswordGenerator.Generate(24);
            Assert.DoesNotContain(password, "l1IO0");
        }
    }

    [Fact]
    public void Generate_ProducesDifferentPasswords()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 64; i++)
        {
            Assert.True(seen.Add(PasswordGenerator.Generate(16)));
        }
    }

    [Fact]
    public void Generate_RejectsInvalidArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PasswordGenerator.Generate(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => PasswordGenerator.Generate(129));
        Assert.Throws<ArgumentException>(() => PasswordGenerator.Generate(16, lower: false, upper: false, digits: false, symbols: false));
    }

    [Fact]
    public void GeneratePassphrase_HasRequestedWordsAndKnownVocabulary()
    {
        var phrase = PasswordGenerator.GeneratePassphrase(4);
        var words = phrase.Split('-');
        Assert.Equal(4, words.Length);
        Assert.All(words, w => Assert.Matches("^[a-z]+$", w));

        var again = PasswordGenerator.GeneratePassphrase(4);
        Assert.NotEqual(phrase, again);
    }

    [Fact]
    public void GeneratePassphrase_RejectsInvalidWordCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PasswordGenerator.GeneratePassphrase(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => PasswordGenerator.GeneratePassphrase(13));
    }

    [Fact]
    public void EstimateEntropyBits_GrowsWithPoolAndLength()
    {
        var digitsOnly = PasswordGenerator.EstimateEntropyBits(10, lower: false, upper: false, digits: true, symbols: false);
        var everything = PasswordGenerator.EstimateEntropyBits(10, lower: true, upper: true, digits: true, symbols: true);
        Assert.True(digitsOnly > 0);
        Assert.True(everything > digitsOnly);
        Assert.Equal(0, PasswordGenerator.EstimateEntropyBits(10, false, false, false, false));
    }

    [Fact]
    public void EstimatePasswordEntropy_CountsClassesAndScalesWithLength()
    {
        var (lowerBits, lowerClasses) = PasswordGenerator.EstimatePasswordEntropy("abcdefgh");
        Assert.Equal(1, lowerClasses);
        Assert.Equal(8 * Math.Log2(26), lowerBits, 5);

        var (mixedBits, mixedClasses) = PasswordGenerator.EstimatePasswordEntropy("Abc123!!");
        Assert.Equal(4, mixedClasses);
        // 符号池取生成器实际使用的集合（去掉了易混淆符号），保证"生成器给的强度"与"强度计显示"口径一致。
        Assert.Equal(
            8 * Math.Log2(26 + 26 + 10 + PasswordGenerator.SymbolPoolSize),
            mixedBits,
            5);
        Assert.True(mixedBits > lowerBits);

        // 空串与单类内容的边界。
        Assert.Equal(0, PasswordGenerator.EstimatePasswordEntropy("").Bits);
        Assert.Equal(1, PasswordGenerator.EstimatePasswordEntropy("12345678").Classes);
    }

    [Fact]
    public void EstimatePassphraseEntropy_ScalesWithWords()
    {
        var four = PasswordGenerator.EstimatePassphraseEntropyBits(4);
        var six = PasswordGenerator.EstimatePassphraseEntropyBits(6);
        Assert.Equal(4 * Math.Log2(PasswordGenerator.WordListSize), four, 5);
        Assert.True(six > four);

        // 词表必须是完整的 EFF 大词表。断言精确值而不是下限：词表被截断时熵会凭空下降，
        // 而界面仍按实算显示 77 bit —— "看起来正常、实际更弱"是最坏的失败形态。
        Assert.Equal(7776, PasswordGenerator.WordListSize);

        // 6 词 ≈ 77.55 bit，界面显示 77（向下取整）。SEC-01 的验收线是 ≥77。
        Assert.InRange(six, 77.0, 78.0);
        Assert.Equal(77, PasswordGenerator.PassphraseEntropyBits(6));
        Assert.True(PasswordGenerator.PassphraseEntropyBits(4) >= 51, "4 词短语不得低于 51 bit");
    }
}
