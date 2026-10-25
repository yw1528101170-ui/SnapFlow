namespace Jietu;

/// <summary>
/// 后缀匹配的白名单边界。
///
/// 背景：扩展的自动填充按 host 匹配，`api.github.com` 要能命中 `github.com` 的条目，
/// 所以必须保留"标签边界后缀匹配"。但纯后缀匹配有个真实钓鱼面：
/// 用户若存了 `github.io`，那么 `evil.github.io` 会命中并自动填充密码——
/// 而 `*.github.io` 是**任意人**都能注册的（GitHub Pages）。
///
/// 因此这里维护两类禁区，命中时把匹配降级为"精确相等"：
/// ① 单标签 host（如 `com`）：后缀匹配等于把整片 TLD 都交出去；
/// ② 多级公共后缀（`co.uk`、`github.io`、`appspot.com` …）：注册权在公众手里，不是某个组织专属。
///
/// 这不是完整的 Public Suffix List（那需要几千条且要定期更新），而是**按风险挑选**的
/// 常见可注册后缀。取舍理由：漏掉一条的后果是"该站点仍需精确匹配"（保守方向），
/// 而不是"放宽匹配"（危险方向）。
/// </summary>
public static class VaultSuffixPolicy
{
    /// <summary>
    /// 多级公共后缀：这些域名的下一级由公众注册，不能当作"机构主域"做后缀匹配。
    /// 全部小写，比较时先归一化。
    /// </summary>
    private static readonly HashSet<string> MultiLabelPublicSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        // 国家/地区二级后缀
        "co.uk", "org.uk", "net.cn", "com.cn", "org.cn", "gov.cn",
        "com.au", "net.au", "co.jp", "co.kr", "com.br", "com.tw", "com.hk",

        // 静态托管 / 应用平台：整段由任意用户注册
        "github.io", "gitlab.io", "appspot.com", "herokuapp.com", "netlify.app",
        "vercel.app", "pages.dev", "workers.dev", "azurewebsites.net", "cloudfront.net",
        "s3.amazonaws.com", "blogspot.com", "glitch.me", "web.app", "firebaseapp.com",
        "surge.sh", "repl.co", "ngrok.io", "ngrok-free.app", "readthedocs.io",
    };

    /// <summary>host 的标签数（`a.b.c` ⇒ 3）。空串返回 0。</summary>
    public static int LabelCount(string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return 0;
        }

        var count = 1;
        foreach (var ch in host)
        {
            if (ch == '.')
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>是否是"注册权在公众手里"的多级后缀。</summary>
    public static bool IsMultiLabelPublicSuffix(string host)
        => !string.IsNullOrEmpty(host) && MultiLabelPublicSuffixes.Contains(host);

    /// <summary>
    /// 该 host 是否**允许**作为后缀匹配的父域。
    /// false 表示只能精确相等——保守方向，宁可少填充也不要填错站点。
    /// </summary>
    public static bool AllowsSuffixMatch(string host)
        => LabelCount(host) >= 2 && !IsMultiLabelPublicSuffix(host);

    /// <summary>禁区条目数（单测锚点，防止表被误删）。</summary>
    public static int MultiLabelSuffixCount => MultiLabelPublicSuffixes.Count;
}
