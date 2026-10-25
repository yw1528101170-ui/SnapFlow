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
}
