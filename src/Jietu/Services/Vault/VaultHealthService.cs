using System.Text;

namespace Jietu;

public enum VaultHealthDimension
{
    /// <summary>弱密码（熵不足或过短）。</summary>
    Weak,

    /// <summary>密码里含有标题/用户名的片段。</summary>
    Fragment,

    /// <summary>多个条目共用同一密码。</summary>
    Reused,

    /// <summary>长期未更新。</summary>
    Stale,

    /// <summary>关键账号未启用两步验证。</summary>
    MissingTwoFactor,
}

public sealed record VaultHealthOptions
{
    /// <summary>低于这个熵值算弱密码。</summary>
    public int WeakEntropyBits { get; init; } = 50;

    /// <summary>短于这个长度算弱密码（两者取"或"）。</summary>
    public int WeakMinLength { get; init; } = 10;

    /// <summary>超过这么多天没改密码算过期。</summary>
    public int StaleDays { get; init; } = 365;

    /// <summary>片段最小长度：太短的片段（`a`、`ab`）在任何密码里都可能出现，会变成全量误报。</summary>
    public int FragmentMinChars { get; init; } = 4;
}

public sealed record VaultHealthIssue(
    string EntryId,
    string Title,
    VaultHealthDimension Dimension,
    double? EntropyBits = null,
    string? Fragment = null,
    IReadOnlyList<string>? PeerTitles = null,
    bool CriticalByUser = false);

/// <summary>
/// 体检结果。
///
/// **五个计数各自独立、互不包含**：同一条目可以同时"弱"且"重复"，UI 不做任何加总。
/// <see cref="TotalEntriesToFix"/> 是按条目去重的并集，**不等于五组之和**——
/// 用户拿五个数字相加核对会加不上，那会让他认为整个体检算错了。
/// </summary>
public sealed record VaultHealthReport(
    DateTimeOffset ComputedUtc,
    int TotalEntriesToFix,
    int WeakCount,
    int FragmentCount,
    int ReusedGroupCount,
    int StaleCount,
    int MissingTwoFactorCount,
    int MissingTwoFactorByUser,
    IReadOnlyList<VaultHealthIssue> Issues)
{
    public bool IsClean => TotalEntriesToFix == 0;

    public static VaultHealthReport Empty { get; } = new(
        DateTimeOffset.MinValue, 0, 0, 0, 0, 0, 0, 0, []);
}

/// <summary>
/// 密码体检：五个维度全部基于**活跃条目**（回收站不参与——已经删掉的东西不构成风险）。
/// 纯函数、无 IO、可取消；5000 条目在几十毫秒内完成，调用方决定是否放后台。
/// </summary>
public static class VaultHealthService
{
    public static VaultHealthReport Compute(
        VaultData data,
        VaultHealthOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        var opt = options ?? new VaultHealthOptions();
        var now = DateTimeOffset.UtcNow;
        var issues = new List<VaultHealthIssue>();
        var toFix = new HashSet<string>(StringComparer.Ordinal);

        // 密码 → 条目（重复检测用）。空密码不参与弱/重复判定。
        var byPassword = new Dictionary<string, List<VaultEntry>>(StringComparer.Ordinal);

        var weak = 0;
        var fragment = 0;
        var stale = 0;

        foreach (var entry in data.Entries)
        {
            ct.ThrowIfCancellationRequested();

            if (!string.IsNullOrEmpty(entry.Password))
            {
                if (!byPassword.TryGetValue(entry.Password, out var peers))
                {
                    byPassword[entry.Password] = peers = [];
                }

                peers.Add(entry);

                var (bits, _) = PasswordGenerator.EstimatePasswordEntropy(entry.Password);
                if (bits < opt.WeakEntropyBits || entry.Password.Length < opt.WeakMinLength)
                {
                    weak++;
                    toFix.Add(entry.Id);
                    issues.Add(new VaultHealthIssue(entry.Id, entry.Title, VaultHealthDimension.Weak, EntropyBits: bits));
                }

                var matched = FindFragment(entry, opt.FragmentMinChars);
                if (matched != null)
                {
                    fragment++;
                    toFix.Add(entry.Id);
                    issues.Add(new VaultHealthIssue(
                        entry.Id, entry.Title, VaultHealthDimension.Fragment, Fragment: matched));
                }
            }

            if (opt.StaleDays > 0 && (now - entry.UpdatedUtc).TotalDays > opt.StaleDays)
            {
                stale++;
                toFix.Add(entry.Id);
                issues.Add(new VaultHealthIssue(entry.Id, entry.Title, VaultHealthDimension.Stale));
            }
        }

        // 重复使用：按组报，组内每条都列出来（用户才知道去改哪几个）。
        var reusedGroups = 0;
        foreach (var (_, peers) in byPassword)
        {
            if (peers.Count < 2)
            {
                continue;
            }

            reusedGroups++;
            var titles = peers.Select(peer => peer.Title).ToArray();
            foreach (var entry in peers)
            {
                toFix.Add(entry.Id);
                issues.Add(new VaultHealthIssue(
                    entry.Id,
                    entry.Title,
                    VaultHealthDimension.Reused,
                    PeerTitles: titles.Where(title => title != entry.Title).ToArray()));
            }
        }

        // 关键账号未启用两步验证。判定来源有两条：关键词表命中，或用户手动标记为「是」。
        var missingTwoFactor = 0;
        var missingByUser = 0;
        foreach (var entry in data.Entries)
        {
            ct.ThrowIfCancellationRequested();

            if (!string.IsNullOrEmpty(entry.TotpSecret))
            {
                continue;
            }

            var byUser = entry.CriticalOverride == true;
            var critical = entry.CriticalOverride
                           ?? VaultCriticalDomains.IsCriticalHost(VaultBridgeProtocol.HostOfUrl(entry.Url));
            if (!critical)
            {
                continue;
            }

            missingTwoFactor++;
            if (byUser)
            {
                missingByUser++;
            }

            toFix.Add(entry.Id);
            issues.Add(new VaultHealthIssue(
                entry.Id, entry.Title, VaultHealthDimension.MissingTwoFactor, CriticalByUser: byUser));
        }

        return new VaultHealthReport(
            now,
            toFix.Count,
            weak,
            fragment,
            reusedGroups,
            stale,
            missingTwoFactor,
            missingByUser,
            issues);
    }

    /// <summary>
    /// 找出密码里含有的标题/用户名片段。命中返回该片段，否则 null。
    ///
    /// 这是最容易被低估的一类弱口令：`GitHub2024!` 看起来有大小写、数字、符号，
    /// 熵也不低，但对知道你在哪个站点的人来说，"站点名 + 年份"是第一个尝试的组合。
    /// </summary>
    private static string? FindFragment(VaultEntry entry, int minChars)
    {
        foreach (var candidate in SplitWords(entry.Title).Concat(SplitWords(entry.Username)))
        {
            if (candidate.Length >= minChars
                && entry.Password.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        // 邮箱前缀（@ 之前）常被当密码的一部分。
        var at = entry.Username.IndexOf('@');
        if (at > 0)
        {
            var local = entry.Username[..at];
            if (local.Length >= minChars && entry.Password.Contains(local, StringComparison.OrdinalIgnoreCase))
            {
                return local;
            }
        }

        return null;
    }

    /// <summary>按非字母数字切分。中文没有空格，整段会作为一个候选（长度不足时自然被过滤）。</summary>
    private static IEnumerable<string> SplitWords(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        var buffer = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer.Append(ch);
                continue;
            }

            if (buffer.Length > 0)
            {
                yield return buffer.ToString();
                buffer.Clear();
            }
        }

        if (buffer.Length > 0)
        {
            yield return buffer.ToString();
        }
    }
}
