namespace Jietu.Tests;

/// <summary>
/// 异步保存编排的行为断言（ROB-03）：
/// ① 突发编辑只落盘一两次，而不是每个按键都写盘；
/// ② 单飞：同一时刻不允许两个写盘任务（tmp 文件名固定，并发写会互相踩）；
/// ③ 失败必须回退：状态 Failed、旧文件不受影响、dirty 保留以便自动重试；
/// ④ Flush 等到"最后一次编辑"落盘，Stop 之后不再落盘；
/// ⑤ 快照隔离：克隆之后修改源对象，落盘内容仍是快照时刻的值。
/// </summary>
public sealed class VaultSaveSchedulerTests
{
    private static VaultSnapshot Snapshot(VaultData? data = null)
        => new() { Header = VaultTestKit.SmallHeader(), Data = data ?? VaultTestKit.SampleData() };

    [Fact]
    public async Task BurstOfRequests_IsCoalescedIntoOneWrite()
    {
        var writes = 0;
        using var scheduler = new VaultSaveScheduler(
            ui: null,
            captureOnUi: () => Snapshot(),
            persist: (_, _) =>
            {
                Interlocked.Increment(ref writes);
                return Task.CompletedTask;
            },
            debounceMs: 200);

        for (var i = 0; i < 10; i++)
        {
            scheduler.Request();
        }

        Assert.True(await scheduler.FlushAsync());

        // 10 次编辑挤在一个防抖窗口里 ⇒ 只写一次。
        Assert.Equal(1, writes);
        Assert.Equal(VaultSaveState.Saved, scheduler.State);
        Assert.NotNull(scheduler.LastSavedUtc);
    }

    [Fact]
    public async Task EditsDuringSave_AreMergedIntoAtMostOneExtraWrite()
    {
        var writes = 0;
        using var scheduler = new VaultSaveScheduler(
            ui: null,
            captureOnUi: () => Snapshot(),
            persist: async (_, _) =>
            {
                Interlocked.Increment(ref writes);
                await Task.Delay(60).ConfigureAwait(false);
            },
            debounceMs: 30);

        scheduler.Request();
        await Task.Delay(40);   // 让第一次写盘开始
        scheduler.Request();    // 写盘期间的新编辑
        scheduler.Request();

        Assert.True(await scheduler.FlushAsync());
        Assert.InRange(writes, 1, 3);
    }

    [Fact]
    public async Task OnlyOnePersistRunsAtATime()
    {
        var concurrent = 0;
        var maxConcurrent = 0;
        using var scheduler = new VaultSaveScheduler(
            ui: null,
            captureOnUi: () => Snapshot(),
            persist: async (_, _) =>
            {
                var now = Interlocked.Increment(ref concurrent);
                InterlockedMax(ref maxConcurrent, now);
                await Task.Delay(50).ConfigureAwait(false);
                Interlocked.Decrement(ref concurrent);
            },
            debounceMs: 20);

        for (var i = 0; i < 5; i++)
        {
            scheduler.Request();
            await Task.Delay(15);
        }

        Assert.True(await scheduler.FlushAsync());
        Assert.Equal(1, maxConcurrent);
    }

    [Fact]
    public async Task PersistFailure_RevertsToFailedStateAndKeepsDirtyForRetry()
    {
        var attempts = 0;
        var shouldFail = true;
        using var scheduler = new VaultSaveScheduler(
            ui: null,
            captureOnUi: () => Snapshot(),
            persist: (_, _) =>
            {
                Interlocked.Increment(ref attempts);
                return shouldFail
                    ? Task.FromException(new IOException("磁盘满了"))
                    : Task.CompletedTask;
            },
            debounceMs: 20);

        scheduler.Request();
        Assert.False(await scheduler.FlushAsync());
        Assert.Equal(VaultSaveState.Failed, scheduler.State);
        Assert.Equal(VaultMessages.SaveFailed, scheduler.LastError);
        Assert.Equal(1, attempts);

        // dirty 没被清掉 ⇒ 下一次 Request 自动重试。
        shouldFail = false;
        scheduler.Request();
        Assert.True(await scheduler.FlushAsync());
        Assert.Equal(VaultSaveState.Saved, scheduler.State);
        Assert.Null(scheduler.LastError);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task FlushWithoutPendingEdits_ReturnsImmediately()
    {
        using var scheduler = new VaultSaveScheduler(
            ui: null,
            captureOnUi: () => Snapshot(),
            persist: (_, _) => Task.CompletedTask);

        Assert.True(await scheduler.FlushAsync());
    }

    [Fact]
    public async Task CaptureReturningNull_StopsWithoutReportingFailure()
    {
        using var scheduler = new VaultSaveScheduler(
            ui: null,
            captureOnUi: () => null,   // 库已锁定/阻断
            persist: (_, _) => Task.CompletedTask,
            debounceMs: 20);

        scheduler.Request();

        // 不可保存不是错误：返回 false（没落盘）但不该进 Failed 状态。
        Assert.False(await scheduler.FlushAsync());
        Assert.NotEqual(VaultSaveState.Failed, scheduler.State);
    }

    [Fact]
    public async Task StopAsync_PreventsFurtherWrites()
    {
        var writes = 0;
        var scheduler = new VaultSaveScheduler(
            ui: null,
            captureOnUi: () => Snapshot(),
            persist: (_, _) =>
            {
                Interlocked.Increment(ref writes);
                return Task.CompletedTask;
            },
            debounceMs: 20);

        await scheduler.StopAsync();
        scheduler.Request();
        await Task.Delay(120);

        Assert.Equal(0, writes);
        scheduler.Dispose();
    }

    [Fact]
    public void CloneForBackground_IsolatesMutableContainers()
    {
        var source = VaultTestKit.SampleData();
        source.Entries[0].Tags = ["work"];
        source.Entries[0].History.Add(new VaultEntryVersion { Password = "old" });

        var clone = source.CloneForBackground();

        // 容器必须是新对象：否则后台序列化时 UI 继续编辑会产出撕裂快照。
        Assert.NotSame(source.Entries, clone.Entries);
        Assert.NotSame(source.Entries[0], clone.Entries[0]);
        Assert.NotSame(source.Entries[0].Tags, clone.Entries[0].Tags);
        Assert.NotSame(source.Entries[0].History, clone.Entries[0].History);

        // 字符串是共享引用：不可变 ⇒ 安全，且不产生额外明文副本（对清零目标有利）。
        Assert.Same(source.Entries[0].Password, clone.Entries[0].Password);

        // 改源不影响副本。
        source.Entries[0].Password = "changed";
        source.Entries[0].Tags.Add("extra");
        source.Entries[0].History.Clear();
        source.Entries.Add(new VaultEntry { Title = "新增" });

        Assert.Equal("P@ssw0rd-机密", clone.Entries[0].Password);
        Assert.Single(clone.Entries[0].Tags);
        Assert.Single(clone.Entries[0].History);
        Assert.Single(clone.Entries);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value)
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
    }
}
