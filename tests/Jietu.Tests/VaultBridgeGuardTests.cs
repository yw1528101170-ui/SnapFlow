namespace Jietu.Tests;

/// <summary>
/// 桥接守卫与导入上限（ROB-04 / ROB-05 / ROB-06 / ROB-07）。
/// 这里锁的都是"错了不会报错、只会悄悄放行"的行为：
/// 后缀匹配放过钓鱼域、配对槽位被并发抢占、限流形同虚设、导入截断半份数据。
/// </summary>
public sealed class VaultBridgeGuardTests
{
    // ---------------- 域名匹配与公共后缀防护 ----------------

    [Theory]
    // 既有行为必须保住：子域应该能命中主域条目，否则大量正常登录会失效。
    [InlineData("https://github.com", "https://api.github.com", true)]
    [InlineData("github.com", "https://github.com/login", true)]
    [InlineData("https://www.github.com", "https://github.com", true)]
    [InlineData("https://github.com", "https://notgithub.com", false)]
    [InlineData("https://github.com", "https://githubXcom", false)]
    public void UrlMatches_KeepsNormalSubdomainBehaviour(string entry, string request, bool expected)
        => Assert.Equal(expected, VaultBridgeProtocol.UrlMatches(entry, request));

    [Theory]
    // 公共后缀：这些域名的下一级由任意人注册，后缀匹配等于把密码交给陌生人。
    [InlineData("https://github.io", "https://evil.github.io")]
    [InlineData("https://appspot.com", "https://evil.appspot.com")]
    [InlineData("https://co.uk", "https://evil.co.uk")]
    [InlineData("https://vercel.app", "https://evil.vercel.app")]
    public void UrlMatches_RefusesSuffixMatchOnPublicSuffixes(string entry, string request)
        => Assert.False(VaultBridgeProtocol.UrlMatches(entry, request));

    [Fact]
    public void UrlMatches_SingleLabelHostOnlyMatchesExactly()
    {
        Assert.True(VaultBridgeProtocol.UrlMatches("https://com", "https://com"));
        Assert.False(VaultBridgeProtocol.UrlMatches("https://com", "https://anything.com"));
    }

    [Fact]
    public void SuffixPolicy_HasExpectedShape()
    {
        Assert.True(VaultSuffixPolicy.AllowsSuffixMatch("github.com"));
        Assert.False(VaultSuffixPolicy.AllowsSuffixMatch("github.io"));
        Assert.False(VaultSuffixPolicy.AllowsSuffixMatch("localhost"));
        Assert.Equal(3, VaultSuffixPolicy.LabelCount("api.github.com"));
        Assert.Equal(1, VaultSuffixPolicy.LabelCount("com"));
        Assert.Equal(0, VaultSuffixPolicy.LabelCount(""));
        Assert.True(VaultSuffixPolicy.MultiLabelSuffixCount >= 20, "防护表被删到不足 20 条，说明有人动过它");
    }

    // ---------------- 配对槽位 ----------------

    [Fact]
    public void PairingGate_OnlyOneWinnerUnderConcurrency()
    {
        var gate = new VaultPairingGate();
        var winners = 0;

        Parallel.For(0, 32, i =>
        {
            var pair = new VaultPairingGate.PendingPair(
                Guid.NewGuid().ToString("N"), $"browser-{i}", "hash", new TaskCompletionSource<bool>());
            if (gate.TryBegin(pair))
            {
                Interlocked.Increment(ref winners);
            }
        });

        Assert.Equal(1, winners);
    }

    [Fact]
    public async Task PairingGate_CompleteReleasesSlotAndRejectsStalePairId()
    {
        var gate = new VaultPairingGate();
        var first = new VaultPairingGate.PendingPair("p1", "a", "h", new TaskCompletionSource<bool>());
        Assert.True(gate.TryBegin(first));

        Assert.False(gate.Complete("p2", true));           // 过期 pairId 不能释放别人的槽位
        Assert.NotNull(gate.Current);

        Assert.True(gate.Complete("p1", false));
        Assert.Null(gate.Current);
        Assert.True(first.Completion.Task.IsCompleted);
        Assert.False(await first.Completion.Task);
    }

    [Fact]
    public void PairingGate_AbandonOnlyClearsOwnSlot()
    {
        var gate = new VaultPairingGate();
        var old = new VaultPairingGate.PendingPair("old", "a", "h", new TaskCompletionSource<bool>());
        var fresh = new VaultPairingGate.PendingPair("fresh", "b", "h2", new TaskCompletionSource<bool>());

        Assert.True(gate.TryBegin(old));
        gate.Abandon("old");                                // 超时清理
        Assert.True(gate.TryBegin(fresh));                  // 新请求可以进来
        gate.Abandon("old");                                // 迟到的清理不能踢掉新请求
        Assert.Equal("fresh", gate.Current!.PairId);
    }

    // ---------------- 限流 ----------------

    [Fact]
    public void RateLimiter_AllowsCapacityThenPauses()
    {
        var limiter = new VaultRateLimiter(perMinute: 5, pauseSeconds: 2);

        for (var i = 0; i < 5; i++)
        {
            Assert.True(limiter.TryAcquire(out _), $"第 {i + 1} 次应当放行");
        }

        Assert.False(limiter.TryAcquire(out var retryAfter));
        // 超限后必须给出等待秒数，让扩展能显示"已暂停 N 秒"而不是静默失败。
        Assert.InRange(retryAfter, 1, 2);

        // 冷却期内持续拒绝，且不因反复请求而延长（否则会变成永久封锁）。
        Assert.False(limiter.TryAcquire(out var second));
        Assert.InRange(second, 1, 2);
    }

    [Fact]
    public void RateLimiter_ResetClearsWindow()
    {
        var limiter = new VaultRateLimiter(perMinute: 2, pauseSeconds: 60);
        Assert.True(limiter.TryAcquire(out _));
        Assert.True(limiter.TryAcquire(out _));
        Assert.False(limiter.TryAcquire(out _));

        limiter.Reset();
        Assert.True(limiter.TryAcquire(out _));
    }

    // ---------------- 审计 ----------------

    [Fact]
    public void AuditLog_IsRingBufferAndNeverExceedsCapacity()
    {
        var audit = new VaultAuditLog(capacity: 3);
        for (var i = 0; i < 10; i++)
        {
            audit.Record($"site{i}.example", VaultAuditResult.Ok, i);
        }

        var snapshot = audit.Snapshot();
        Assert.Equal(3, snapshot.Count);
        // 环形：保留最新的三条。
        Assert.Equal("site7.example", snapshot[0].Host);
        Assert.Equal("site9.example", snapshot[2].Host);
    }

    // ---------------- 导入上限（整体拒绝，绝不截断） ----------------

    [Fact]
    public void ValidateImportRows_RejectsTooManyEntriesWholeSale()
    {
        var tooMany = Enumerable.Range(0, VaultLimits.MaxImportEntries + 1)
            .Select(i => new VaultCsvRow($"t{i}", "", "u", "p", "", ""))
            .ToList();

        var error = VaultImportExport.ValidateImportRows(tooMany);

        Assert.NotNull(error);
        Assert.Contains("本次没有导入任何条目", error);
        // 边界必须包含：正好 5000 条是合法的。
        Assert.Null(VaultImportExport.ValidateImportRows(tooMany.Take(VaultLimits.MaxImportEntries).ToList()));
    }

    [Fact]
    public void ValidateImportRows_NamesTheRowAndField()
    {
        var rows = new List<VaultCsvRow>
        {
            new("ok", "", "u", "p", "", ""),
            new("fine", "", "u", "p", "", ""),
            new("bad", "", "u", new string('x', VaultLimits.MaxFieldChars + 1), "", ""),
        };

        var error = VaultImportExport.ValidateImportRows(rows);

        Assert.NotNull(error);
        // 第 3 条数据行 = 文件第 4 行（含表头），且必须点名"密码"，
        // 否则用户要自己在 5000 行里翻哪一条超长。
        Assert.Contains("第 4 条", error);
        Assert.Contains("密码", error);
        // 恰好 8192 字符必须通过：把 > 写成 >= 会把合法数据挡在外面。
        rows[2] = new VaultCsvRow("bad", "", "u", new string('x', VaultLimits.MaxFieldChars), "", "");
        Assert.Null(VaultImportExport.ValidateImportRows(rows));
    }

    [Fact]
    public void MergeInto_DedupeKeyDoesNotCollideAcrossFieldBoundary()
    {
        var target = new VaultData
        {
            Entries = [new VaultEntry { Title = "ab", Username = "c" }],
        };

        // 没有分隔符时 "ab"+"c" 与 "a"+"bc" 会被误判成同一条，导致静默丢数据。
        var added = VaultImportExport.MergeInto(target, [new VaultEntry { Title = "a", Username = "bc" }]);

        Assert.Equal(1, added);
        Assert.Equal(2, target.Entries.Count);
    }

    [Fact]
    public void MergeInto_IsCaseInsensitiveAndIdempotent()
    {
        var target = new VaultData
        {
            Entries = [new VaultEntry { Title = "GitHub", Username = "Alice", Password = "old" }],
        };
        var incoming = new[]
        {
            new VaultEntry { Title = "github", Username = "alice", Password = "new" },
            new VaultEntry { Title = "GitHub", Username = "bob", Password = "p2" },
        };

        Assert.Equal(1, VaultImportExport.MergeInto(target, incoming));
        Assert.Equal("old", target.Entries[0].Password);
        Assert.Equal(0, VaultImportExport.MergeInto(target, incoming));
    }
}
