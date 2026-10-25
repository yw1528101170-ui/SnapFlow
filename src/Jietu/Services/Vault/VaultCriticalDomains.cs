namespace Jietu;

/// <summary>
/// 关键账号判定用的关键词表（子串匹配）。
///
/// 为什么用"关键词"而不是"精确域名列表"：
/// ① 域名列表必然漏（每家银行有几十个域名，还有 App 专用域），而漏报的代价是
///    "用户以为没事、其实这个账号该开两步验证"；
/// ② 关键词匹配偏保守（宁可多提示），且不会因为域名改版而失效；
/// ③ 误报目前只能自行忽略（编辑器暂无「标为非关键账号」开关，
///    <see cref="VaultEntry.CriticalOverride"/> 字段已在载荷 v2 预留）。
///
/// 纪律：全部小写 ASCII、长度 ≥ 3、只匹配 host（不含路径与查询串）、
/// **纯 `Contains`，没有任何后缀语义或例外分支**——规则无例外才不会被后人改坏。
///
/// 已知漏报（必须承认，靠手动标记兜底）：`x.com`（单字母）、`qq.com`（2 字符）、
/// 企业自有域名邮箱（`mail.company.com` 不含任何品牌词）。
/// </summary>
public static class VaultCriticalDomains
{
    /// <summary>73 条关键词，按用途分组（分组只用于维护，匹配时一视同仁）。</summary>
    private static readonly string[] Keywords =
    [
        // 银行（11）
        "icbc", "ccb", "abchina", "bankofchina", "cmbchina",
        "bankcomm", "spdb", "cib", "ecitic", "cgbchina", "psbc",

        // 支付（8）
        "alipay", "unionpay", "tenpay", "paypal", "stripe", "jdpay", "yeepay", "rapidpay",

        // 邮箱（11）
        "gmail", "outlook", "hotmail", "protonmail", "yahoo", "icloud",
        "163.com", "126.com", "exmail", "aliyun", "zoho",

        // 云服务（12）
        "aws", "amazonaws", "azure", "googlecloud", "tencentcloud", "huaweicloud",
        "digitalocean", "cloudflare", "heroku", "vercel", "netlify", "linode",

        // 社交与内容社区（12）
        "weibo", "wechat", "douyin", "tiktok", "facebook", "instagram",
        "twitter", "linkedin", "reddit", "telegram", "discord", "zhihu",

        // 政务与社保（6）
        "gov", "mohrss", "nhsa", "chinatax", "12333", "12306",

        // 企业办公与代码托管（13）
        "github", "gitlab", "bitbucket", "feishu", "larksuite", "dingtalk",
        "wecom", "slack", "notion", "jira", "confluence", "atlassian", "zoom",
    ];

    /// <summary>关键词总数（单测锚点：词表被误删会立刻失败）。</summary>
    public static int Count => Keywords.Length;

    /// <summary>只读视图（维护工具与单测用）。</summary>
    public static IReadOnlyList<string> All => Keywords;

    /// <summary>
    /// 命中即视为关键账号。<paramref name="host"/> 会先小写化；
    /// 空 host 直接返回 false（没有网址的条目无从判断）。
    /// </summary>
    public static bool IsCriticalHost(string? host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        var lowered = host.ToLowerInvariant();
        foreach (var keyword in Keywords)
        {
            if (lowered.Contains(keyword, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
