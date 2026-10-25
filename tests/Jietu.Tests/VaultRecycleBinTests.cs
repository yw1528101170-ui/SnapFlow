namespace Jietu.Tests;

/// <summary>
/// 回收站 / 历史 / 体检（UX-02 / UX-03 / UX-04）。
/// 这三个功能的共同风险不是"算错"，而是"算得看起来对、但在边界上悄悄丢数据或误导用户"：
/// 回收站把活跃条目删掉、历史回滚不可逆、体检把五组数字加总成错的总数。
/// </summary>
public sealed class VaultRecycleBinTests
{
    private static VaultEntry Entry(string id, string title, string password = "P@ssw0rd-long-enough", string url = "")
        => new()
        {
            Id = id,
            Title = title,
            Username = "user@example.com",
            Password = password,
            Url = url,
            CreatedUtc = DateTimeOffset.UtcNow.AddDays(-10),
            UpdatedUtc = DateTimeOffset.UtcNow.AddDays(-10),
        };

    // ---------------- 回收站 ----------------

    [Fact]
    public void MoveToBin_MovesEntryAndStampsDeletedUtc()
    {
        var data = new VaultData { Entries = [Entry("1", "站点")] };
        var now = DateTimeOffset.UtcNow;

        Assert.True(VaultRecycleBinService.MoveToBin(data, "1", now));

        Assert.Empty(data.Entries);
        Assert.Single(data.RecycleBin);
        Assert.Equal(now, data.RecycleBin[0].DeletedUtc);
    }

    [Fact]
    public void Restore_ClearsDeletedUtcAndReturnsToActiveList()
    {
        var data = new VaultData { Entries = [Entry("1", "站点")] };
        VaultRecycleBinService.MoveToBin(data, "1", DateTimeOffset.UtcNow);

        Assert.True(VaultRecycleBinService.Restore(data, "1"));

        Assert.Single(data.Entries);
        Assert.Empty(data.RecycleBin);
        Assert.Null(data.Entries[0].DeletedUtc);
    }

    [Fact]
    public void Restore_DropsRecycleCopyWhenActiveAlreadyExists()
    {
        var data = new VaultData { Entries = [Entry("1", "活跃")] };
        data.RecycleBin.Add(new VaultEntry { Id = "1", Title = "回收站", DeletedUtc = DateTimeOffset.UtcNow });

        Assert.True(VaultRecycleBinService.Restore(data, "1"));

        // 活跃优先：不能因为恢复而出现同 id 两条。
        Assert.Single(data.Entries);
        Assert.Empty(data.RecycleBin);
        Assert.Equal("活跃", data.Entries[0].Title);
    }

    [Fact]
    public void Sweep_RemovesExpiredEntries()
    {
        var data = new VaultData();
        data.RecycleBin.Add(new VaultEntry { Id = "old", DeletedUtc = DateTimeOffset.UtcNow.AddDays(-40) });
        data.RecycleBin.Add(new VaultEntry { Id = "fresh", DeletedUtc = DateTimeOffset.UtcNow.AddDays(-2) });

        var removed = VaultRecycleBinService.Sweep(data, DateTimeOffset.UtcNow, retainDays: 30, maxCount: 500);

        Assert.Equal(1, removed);
        Assert.Equal("fresh", Assert.Single(data.RecycleBin).Id);
    }

    [Fact]
    public void Sweep_DropsOldestWhenOverCapacity()
    {
        var data = new VaultData();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            data.RecycleBin.Add(new VaultEntry { Id = $"e{i}", DeletedUtc = now.AddHours(-i) });
        }

        var removed = VaultRecycleBinService.Sweep(data, now, retainDays: 0, maxCount: 2);

        Assert.Equal(3, removed);
        // 先进先出：留下的是删除时间最近的两条（e0 最新、e1 次新）。
        Assert.Equal(["e0", "e1"], data.RecycleBin.Select(e => e.Id).OrderBy(id => id).ToArray());
    }

    [Fact]
    public void PurgeAndClear_AreExplicitAndIrreversible()
    {
        var data = new VaultData();
        data.RecycleBin.Add(new VaultEntry { Id = "a", DeletedUtc = DateTimeOffset.UtcNow });
        data.RecycleBin.Add(new VaultEntry { Id = "b", DeletedUtc = DateTimeOffset.UtcNow });

        Assert.True(VaultRecycleBinService.Purge(data, "a"));
        Assert.Single(data.RecycleBin);

        Assert.Equal(1, VaultRecycleBinService.Clear(data));
        Assert.Empty(data.RecycleBin);
    }

    // ---------------- 历史 ----------------

    [Fact]
    public void History_KeepsAtMostFiveVersions()
    {
        var entry = Entry("1", "站点", "v0");

        for (var i = 1; i <= 8; i++)
        {
            entry.Password = $"v{i}";
            VaultHistoryService.Record(entry, DateTimeOffset.UtcNow);
        }

        Assert.Equal(VaultLimits.HistoryVersions, entry.History.Count);
        // Record 存的是"调用那一刻的内容"，所以最后一条就是最后一次设置的值；
        // 保留的 5 条是 v4..v8。UI 那边的用法是"替换前先 Record(旧条目)"。
        Assert.Equal("v8", entry.History[^1].Password);
        Assert.Equal("v4", entry.History[0].Password);
    }

    [Fact]
    public void History_DoesNotRecordWhenNothingChanged()
    {
        var entry = Entry("1", "站点", "same");
        VaultHistoryService.Record(entry, DateTimeOffset.UtcNow);
        var afterFirst = entry.History.Count;

        VaultHistoryService.Record(entry, DateTimeOffset.UtcNow);
        VaultHistoryService.Record(entry, DateTimeOffset.UtcNow);

        // 点开又关掉不该堆历史。
        Assert.Equal(afterFirst, entry.History.Count);
    }

    [Fact]
    public void Rollback_RestoresContentAndKeepsCurrentStateRecoverable()
    {
        var entry = Entry("1", "站点", "old-password");
        VaultHistoryService.Record(entry, DateTimeOffset.UtcNow);

        entry.Password = "new-password";
        entry.Title = "改了标题";

        Assert.True(VaultHistoryService.Rollback(entry, 0, DateTimeOffset.UtcNow));

        Assert.Equal("old-password", entry.Password);
        Assert.Equal("站点", entry.Title);
        // 回滚本身也要可再回滚：当前内容必须先进历史。
        Assert.Contains(entry.History, version => version.Password == "new-password");
    }

    [Fact]
    public void DiffFields_ReportsOnlyChangedColumns()
    {
        var older = new VaultEntryVersion { Title = "a", Username = "u", Password = "p1", Tags = ["x"] };
        var newer = new VaultEntryVersion { Title = "a", Username = "u2", Password = "p2", Tags = ["x"] };

        var changed = VaultHistoryService.DiffFields(older, newer);

        Assert.Equal(["username", "password"], changed);
        Assert.Equal("用户名", VaultHistoryService.DescribeField("username"));
    }

    // ---------------- 体检 ----------------

    [Fact]
    public void Health_FlagsWeakFragmentAndReused()
    {
        var data = new VaultData
        {
            Entries =
            [
                Entry("weak", "短密码站", "abc"),
                Entry("fragment", "GitHub", "GitHub2024!"),
                Entry("dup1", "甲", "SamePass123!"),
                Entry("dup2", "乙", "SamePass123!"),
            ],
        };

        var report = VaultHealthService.Compute(data);

        Assert.Equal(1, report.WeakCount);
        Assert.Equal(1, report.FragmentCount);
        Assert.Equal(1, report.ReusedGroupCount);   // 按组计，不是按条目
        Assert.Contains(report.Issues, i => i.EntryId == "fragment" && i.Dimension == VaultHealthDimension.Fragment);
    }

    [Fact]
    public void Health_TotalIsDistinctEntriesNotSumOfGroups()
    {
        var data = new VaultData
        {
            Entries =
            [
                // 同一条既弱又重复：总数必须是 2（两条），不是 3。
                Entry("a", "甲", "abc"),
                Entry("b", "乙", "abc"),
            ],
        };

        var report = VaultHealthService.Compute(data);

        Assert.Equal(2, report.WeakCount);
        Assert.Equal(1, report.ReusedGroupCount);
        Assert.Equal(2, report.TotalEntriesToFix);
    }

    [Fact]
    public void Health_FlagsStaleEntries()
    {
        var entry = Entry("old", "老站点", "P@ssw0rd-long-enough");
        entry.UpdatedUtc = DateTimeOffset.UtcNow.AddDays(-400);
        var data = new VaultData { Entries = [entry] };

        var report = VaultHealthService.Compute(data);

        Assert.Equal(1, report.StaleCount);
    }

    [Fact]
    public void Health_FlagsCriticalAccountWithoutTotp()
    {
        var data = new VaultData
        {
            Entries =
            [
                Entry("critical", "银行", "P@ssw0rd-long-enough", "https://icbc.com.cn"),
                Entry("normal", "小站", "P@ssw0rd-long-enough", "https://example.org"),
                // 手动标记「是」：关键词认不出的账号靠它兜底。
                new VaultEntry
                {
                    Id = "manual",
                    Title = "公司内网",
                    Password = "P@ssw0rd-long-enough",
                    Url = "https://intranet.corp",
                    CriticalOverride = true,
                },
                // 手动标记「否」：关键词命中也要排除。
                new VaultEntry
                {
                    Id = "excluded",
                    Title = "测试",
                    Password = "P@ssw0rd-long-enough",
                    Url = "https://github.com",
                    CriticalOverride = false,
                },
            ],
        };

        var report = VaultHealthService.Compute(data);

        Assert.Equal(2, report.MissingTwoFactorCount);
        Assert.Equal(1, report.MissingTwoFactorByUser);
        // 只断言"没有被算进关键账号"：它仍可能因为密码重复出现在 Reused 组，那是正常的。
        Assert.DoesNotContain(
            report.Issues,
            i => i.EntryId == "excluded" && i.Dimension == VaultHealthDimension.MissingTwoFactor);
    }

    [Fact]
    public void Health_IgnoresRecycleBin()
    {
        var data = new VaultData();
        data.RecycleBin.Add(new VaultEntry
        {
            Id = "trashed",
            Title = "已删",
            Password = "abc",
            UpdatedUtc = DateTimeOffset.UtcNow.AddDays(-500),
            DeletedUtc = DateTimeOffset.UtcNow,
        });

        var report = VaultHealthService.Compute(data);

        // 已经删掉的东西不再构成风险，出现在体检里只会制造噪声。
        Assert.True(report.IsClean);
        Assert.Equal(0, report.TotalEntriesToFix);
    }

    [Fact]
    public void CriticalKeywords_AreWellFormed()
    {
        // 词表规模是锚点：被误删或误加都会让这里失败。
        Assert.Equal(73, VaultCriticalDomains.Count);
        Assert.All(VaultCriticalDomains.All, keyword =>
        {
            Assert.Equal(keyword, keyword.ToLowerInvariant());
            Assert.True(keyword.Length >= 3, $"关键词 `{keyword}` 过短，误伤面太大");
        });
        Assert.Equal(73, VaultCriticalDomains.All.Distinct(StringComparer.Ordinal).Count());

        Assert.True(VaultCriticalDomains.IsCriticalHost("console.aws.amazon.com"));
        Assert.False(VaultCriticalDomains.IsCriticalHost("example.org"));
        Assert.False(VaultCriticalDomains.IsCriticalHost(""));
    }
}
