namespace Jietu;

/// <summary>
/// 条目历史：每次保存前把"被覆盖的那一版"存进 <see cref="VaultEntry.History"/>，
/// 上限 5 版（FIFO 丢最老）。
///
/// 为什么不存"每次编辑的快照"：密码库的写入是整库重加密，历史越多元库越大、
/// 每次保存越慢。5 版足够覆盖"改错了想退回上一步"这个真实诉求。
///
/// 历史版本里的密码字段同样是明文 —— 它随库一起加密，不额外落盘；
/// 但也意味着"改密码后旧密码仍在历史里"，这是用户可预期且可自行删除的。
/// </summary>
public static class VaultHistoryService
{
    /// <summary>把条目当前内容打包成一个历史版本。</summary>
    public static VaultEntryVersion Snapshot(VaultEntry entry, DateTimeOffset changedUtc) => new()
    {
        ChangedUtc = changedUtc,
        ChangedFields = [],
        Title = entry.Title,
        Username = entry.Username,
        Password = entry.Password,
        Url = entry.Url,
        Notes = entry.Notes,
        Tags = [.. entry.Tags],
        TotpSecret = entry.TotpSecret,
    };

    /// <summary>
    /// 记录一个新版本并裁剪到上限。返回实际被记入的版本（含与上一版的字段差异）。
    /// </summary>
    public static VaultEntryVersion Record(VaultEntry entry, DateTimeOffset changedUtc, int keep = VaultLimits.HistoryVersions)
    {
        var version = Snapshot(entry, changedUtc);
        if (entry.History.Count > 0)
        {
            version.ChangedFields = DiffFields(entry.History[^1], version);
        }

        // 内容没有任何变化就不留版本：否则"点开又关掉"也会堆历史。
        if (version.ChangedFields.Count == 0 && entry.History.Count > 0)
        {
            return version;
        }

        entry.History.Add(version);
        Trim(entry, keep);
        return version;
    }

    /// <summary>把条目回滚到某个历史版本；当前内容先入历史，所以回滚本身也可再回滚。</summary>
    public static bool Rollback(VaultEntry entry, int index, DateTimeOffset now, int keep = VaultLimits.HistoryVersions)
    {
        if (index < 0 || index >= entry.History.Count)
        {
            return false;
        }

        // 先把"当前状态"存进历史，否则回滚是不可逆的。
        Record(entry, entry.UpdatedUtc);

        var version = entry.History[index];
        entry.Title = version.Title;
        entry.Username = version.Username;
        entry.Password = version.Password;
        entry.Url = version.Url;
        entry.Notes = version.Notes;
        entry.Tags = [.. version.Tags];
        entry.TotpSecret = version.TotpSecret;
        entry.UpdatedUtc = now;

        Trim(entry, keep);
        return true;
    }

    /// <summary>两个版本之间发生变化的字段名（顺序固定，便于界面稳定展示）。</summary>
    public static List<string> DiffFields(VaultEntryVersion older, VaultEntryVersion newer)
    {
        var changed = new List<string>(7);

        if (!string.Equals(older.Title, newer.Title, StringComparison.Ordinal))
        {
            changed.Add("title");
        }

        if (!string.Equals(older.Username, newer.Username, StringComparison.Ordinal))
        {
            changed.Add("username");
        }

        if (!string.Equals(older.Password, newer.Password, StringComparison.Ordinal))
        {
            changed.Add("password");
        }

        if (!string.Equals(older.Url, newer.Url, StringComparison.Ordinal))
        {
            changed.Add("url");
        }

        if (!string.Equals(older.Notes, newer.Notes, StringComparison.Ordinal))
        {
            changed.Add("notes");
        }

        if (!string.Equals(older.TotpSecret, newer.TotpSecret, StringComparison.Ordinal))
        {
            changed.Add("totpSecret");
        }

        if (!older.Tags.SequenceEqual(newer.Tags, StringComparer.Ordinal))
        {
            changed.Add("tags");
        }

        return changed;
    }

    /// <summary>字段名的中文展示（界面用；未知字段原样返回）。</summary>
    public static string DescribeField(string field) => field switch
    {
        "title" => "标题",
        "username" => "用户名",
        "password" => "密码",
        "url" => "网址",
        "notes" => "备注",
        "totpSecret" => "动态验证码密钥",
        "tags" => "标签",
        _ => field,
    };

    private static void Trim(VaultEntry entry, int keep)
    {
        var limit = Math.Max(1, keep);
        while (entry.History.Count > limit)
        {
            entry.History.RemoveAt(0);
        }
    }
}
