namespace Jietu.Tests;

/// <summary>
/// 历史列表 OCR 摘要的清洗规则：系统 OCR 会把设置页的圆形开关/圆钮认成
/// 孤立的「0/O」，它们单独成词或黏在中文词首，列表里看起来就像每条记录
/// 都以「0 」开头。样例取自真实历史库中的识别文本。
/// </summary>
public class ClipRowSnippetTests
{
    [Fact]
    public void 剥掉黏在中文词首的噪声0()
    {
        Assert.Equal("荬贴藐历史 常规与自启 启动",
            ClipRow.OcrSnippet("0荬贴藐历史\r\n0常规与自启\n0启动"));
    }

    [Fact]
    public void 丢掉孤立成词的噪声0和大写O()
    {
        Assert.Equal("SnapFlowä规与自启 后动、存与三示器第略 1 19分前",
            ClipRow.OcrSnippet("0 SnapFlowä规与自启\n后动、存与三示器第略\n1 19分前\nO"));
        Assert.Equal("1监视器 自动保存截图到本地",
            ClipRow.OcrSnippet("0 1监视器\n自动保存截图到本地"));
    }

    [Fact]
    public void 不动正常的西文数字内容()
    {
        Assert.Equal("版本 1.1.5 WinAppSDK 1.10", ClipRow.OcrSnippet("版本 1.1.5 WinAppSDK 1.10"));
        Assert.Equal("API 端点 https://api.openai.com/v1", ClipRow.OcrSnippet("API 端点 https://api.openai.com/v1"));
    }

    [Fact]
    public void 只剩噪声时返回空串()
    {
        Assert.Equal("", ClipRow.OcrSnippet("0\nO\no\n0"));
        Assert.Equal("", ClipRow.OcrSnippet(""));
    }

    [Fact]
    public void 换行压平为空格并保留400字截断()
    {
        Assert.Equal("a b c", ClipRow.OcrSnippet("a\r\nb\tc"));
        var longText = new string('字', 500);
        Assert.Equal(400, ClipRow.OcrSnippet(longText).Length);
    }
}
