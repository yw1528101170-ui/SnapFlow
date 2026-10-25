namespace Jietu;

/// <summary>
/// 回收站：删除先进这里，而不是立刻销毁。
///
/// 设计上的三个决定：
/// ① **存放位置唯一**——条目要么在 <see cref="VaultData.Entries"/>（DeletedUtc 恒 null），
///    要么在 <see cref="VaultData.RecycleBin"/>（DeletedUtc 恒非 null）。不存在第三态。
/// ② 回收站条目**不参与**搜索、体检、浏览器填充、CSV 导出——范围隔离在"遍历哪个列表"这一层，
///    不靠每处都记得加 `DeletedUtc == null` 判断。
/// ③ 清理只发生在 <see cref="Sweep"/>（明确的触发点），不在 <see cref="VaultData.Normalize"/> 里：
///    打开一份旧备份就悄悄删数据的实现是"正确但危险"。
/// </summary>
public static class VaultRecycleBinService
{
    /// <summary>移入回收站。返回 false 表示该 id 不在活跃条目里。</summary>
    public static bool MoveToBin(VaultData data, string entryId, DateTimeOffset now)
    {
        var index = data.Entries.FindIndex(entry => entry.Id == entryId);
        if (index < 0)
        {
            return false;
        }

        var entry = data.Entries[index];
        data.Entries.RemoveAt(index);
        entry.DeletedUtc = now;
        data.RecycleBin.Add(entry);
        return true;
    }

    /// <summary>从回收站恢复。同 id 已在活跃列表时直接丢弃回收站那份（活跃优先）。</summary>
    public static bool Restore(VaultData data, string entryId)
    {
        var index = data.RecycleBin.FindIndex(entry => entry.Id == entryId);
        if (index < 0)
        {
            return false;
        }

        var entry = data.RecycleBin[index];
        data.RecycleBin.RemoveAt(index);

        if (data.Entries.Exists(active => active.Id == entryId))
        {
            return true;
        }

        entry.DeletedUtc = null;
        entry.UpdatedUtc = DateTimeOffset.UtcNow;
        data.Entries.Add(entry);
        return true;
    }

    /// <summary>彻底删除单条（不可恢复）。</summary>
    public static bool Purge(VaultData data, string entryId)
        => data.RecycleBin.RemoveAll(entry => entry.Id == entryId) > 0;

    /// <summary>清空回收站，返回删除条数。</summary>
    public static int Clear(VaultData data)
    {
        var count = data.RecycleBin.Count;
        data.RecycleBin.Clear();
        return count;
    }

    /// <summary>
    /// 按保留期与条数上限淘汰：先丢超期的，再在超量时从最老的开始丢。
    /// 只在明确的时机调用（锁定、解锁后的维护、用户手动清理），不在读取时偷偷执行。
    /// </summary>
    public static int Sweep(
        VaultData data,
        DateTimeOffset now,
        int retainDays = VaultLimits.TrashRetainDays,
        int maxCount = VaultLimits.TrashMaxEntries)
    {
        var removed = 0;

        if (retainDays > 0)
        {
            var cutoff = now.AddDays(-retainDays);
            removed += data.RecycleBin.RemoveAll(entry =>
                (entry.DeletedUtc ?? entry.UpdatedUtc) < cutoff);
        }

        if (maxCount > 0 && data.RecycleBin.Count > maxCount)
        {
            // 先进先出：删除时间最久的先走。
            var ordered = data.RecycleBin
                .OrderBy(entry => entry.DeletedUtc ?? entry.UpdatedUtc)
                .Take(data.RecycleBin.Count - maxCount)
                .Select(entry => entry.Id)
                .ToHashSet(StringComparer.Ordinal);

            removed += data.RecycleBin.RemoveAll(entry => ordered.Contains(entry.Id));
        }

        return removed;
    }
}
