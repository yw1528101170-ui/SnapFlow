using System.Text;

namespace Jietu.Tests;

public sealed class ClipFormatTests
{
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void ShouldKeep_PlainOuterHtmlIsDropped()
    {
        var plain = Utf8("<html><body><!--StartFragment-->E:\\jietu\\src<!--EndFragment--></body></html>");
        var plainRtf = Encoding.ASCII.GetBytes("{\\rtf1 \\b0 hi}");
        Assert.False(ClipFormat.ShouldKeep("E:\\jietu\\src", plain, plainRtf));
    }

    [Fact]
    public void ShouldKeep_KeepsMeaningfulFormatting()
    {
        var bold = Utf8("<html><body><!--StartFragment--><b>你好</b><!--EndFragment--></body></html>");
        var link = Utf8("<a href=\"https://example.com\">hi</a>");
        var boldRtf = Encoding.ASCII.GetBytes("{\\rtf1 \\b hi}");
        Assert.True(ClipFormat.ShouldKeep("你好", bold, null));
        Assert.True(ClipFormat.ShouldKeep("hi", link, null));
        Assert.True(ClipFormat.ShouldKeep("hi", null, boldRtf));
    }

    [Fact]
    public void ShouldKeep_DropsUnboldedRtf()
    {
        var plainRtf = Encoding.ASCII.GetBytes("{\\rtf1 \\b0 hi}");
        Assert.False(ClipFormat.ShouldKeep("hi", null, plainRtf));
    }
}

public sealed class HashingTests
{
    // SHA-256("abc") 的标准向量（Convert.ToHexString 输出大写），钉死哈希算法与大小写不被悄悄更换。
    private const string AbcSha256 = "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD";

    [Fact]
    public void OfText_MatchesKnownVector()
    {
        Assert.Equal(AbcSha256, Hashing.OfText("abc"));
    }

    [Fact]
    public void OfBytes_MatchesTextForSameUtf8()
    {
        Assert.Equal(Hashing.OfText("abc"), Hashing.OfBytes(Encoding.UTF8.GetBytes("abc")));
    }

    [Fact]
    public void OfBytes_DistinguishesInputs()
    {
        Assert.NotEqual(Hashing.OfBytes([1, 2, 3]), Hashing.OfBytes([1, 2, 4]));
        Assert.Equal(Hashing.OfBytes([1, 2, 3]), Hashing.OfBytes([1, 2, 3]));
    }

    [Fact]
    public void OfRich_SeparatesTextFromAttachments()
    {
        var html = Encoding.UTF8.GetBytes("<b>hi</b>");
        Assert.NotEqual(Hashing.OfRich("hi", null, null), Hashing.OfRich("hi", html, null));
        Assert.Equal(Hashing.OfRich("hi", html, null), Hashing.OfRich("hi", html, null));
    }
}

public sealed class DibImageTests
{
    [Fact]
    public void Build_TryRead_RoundTrip()
    {
        var pixels = new byte[2 * 2 * 4];
        pixels[2] = 255; // 第一个像素的 R 分量
        for (var i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
        }

        var dib = DibImage.Build(pixels, 2, 2);
        Assert.True(DibImage.TryRead(dib, out var back, out var width, out var height));
        Assert.Equal(2, width);
        Assert.Equal(2, height);
        Assert.Equal(16, back.Length);
        Assert.Equal(255, back[2]);
        Assert.Equal(0, back[0]);
    }

    [Fact]
    public void TryRead_RejectsShortBuffer()
    {
        Assert.False(DibImage.TryRead(new byte[10], out _, out _, out _));
    }
}

public sealed class FileDropTests
{
    [Fact]
    public void Build_Parse_RoundTrip()
    {
        var paths = new List<string> { @"C:\a\b.txt", @"C:\c\d e.png" };
        var parsed = FileDrop.Parse(FileDrop.Build(paths));
        Assert.Equal(paths, parsed);
    }

    [Fact]
    public void Parse_RejectsShortData()
    {
        Assert.Empty(FileDrop.Parse(new byte[5]));
    }
}
