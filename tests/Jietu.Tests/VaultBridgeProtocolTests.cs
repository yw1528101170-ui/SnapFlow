namespace Jietu.Tests;

public sealed class VaultBridgeProtocolTests
{
    [Theory]
    [InlineData("https://github.com", "https://github.com/login", true)]
    [InlineData("https://github.com", "https://api.github.com/repos", true)]   // 子域
    [InlineData("https://github.com", "https://notgithub.com", false)]         // 前缀伪造
    [InlineData("https://www.example.com", "http://example.com/whatever", true)] // www. 前缀等价 + 忽略协议
    [InlineData("http://x.test:8080", "http://x.test/", true)]                 // 忽略端口
    [InlineData("", "https://a.com", false)]
    [InlineData("https://a.com", "", false)]
    [InlineData("example.com", "https://example.com/signin", true)]            // 用户手存的裸域名
    [InlineData("example.com", "https://evilexample.com", false)]
    public void UrlMatches_MatchesHostWithSubdomainSuffix(string entryUrl, string requestUrl, bool expected)
    {
        Assert.Equal(expected, VaultBridgeProtocol.UrlMatches(entryUrl, requestUrl));
    }
}
