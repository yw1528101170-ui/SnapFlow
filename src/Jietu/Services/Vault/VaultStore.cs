namespace Jietu;

/// <summary>
/// 一次"读库文件"的完整结果：原始字节、字节层分类、以及在 V2 情况下解析出的头与体。
///
/// <see cref="Raw"/> 保留原始字节是刻意的：解锁失败的诊断需要"文件多少字节、分类是什么"，
/// 而这些都是可以安全记录的（不含明文）。明文只在 <see cref="Body"/> 被解密后存在于内存。
/// </summary>
public readonly record struct VaultFileRead(byte[] Raw, VaultFileKind Kind, VaultHeader Header, byte[]? Body, bool IoFailed = false)
{
    public long Length => Raw.LongLength;
}

/// <summary>
/// vault.spv 的文件 IO：只负责字节进出，加解密在 VaultCrypto。
///
/// 类型层面的安全边界（SEC-03 的落地方式）：
/// 解锁路径只能拿到 <see cref="Read"/> 这一个方法——它读文件、返回分类，**不产生任何文件**。
/// 会写盘的方法（<see cref="SaveAsync"/> / <see cref="TryCreateCorruptBackup"/>）都不在
/// 解锁分支的调用链上，且 <see cref="TryCreateCorruptBackup"/> 自带 kind 白名单
/// （v1/更新版本/认证失败一律不写）。这样"忘了别写盘"就不再依赖人的记性。
/// </summary>
public static class VaultStore
{
    private static readonly Lock BackupGate = new();

    /// <summary>本进程内已经为哪些库文件生成过损坏备份（同一会话只备份一次）。</summary>
    private static readonly HashSet<string> BackedUpPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>最近一次 IO/备份操作的面向用户提示；没有可提示的事时为 null。</summary>
    public static string? LastNotice { get; private set; }

    public static bool Exists(string path) => File.Exists(path);

    /// <summary>
    /// 读取并分类。**纯读**：不创建、不改名、不删除、不备份任何文件。
    /// </summary>
    public static VaultFileRead Read(string path)
    {
        LastNotice = null;
        try
        {
            if (!File.Exists(path))
            {
                return new VaultFileRead([], VaultFileKind.Missing, default, null);
            }

            var raw = File.ReadAllBytes(path);
            return ClassifyBytes(raw);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            LastNotice = VaultMessages.VaultReadFailed(ex.Message);
            return new VaultFileRead([], VaultFileKind.Missing, default, null, IoFailed: true);
        }
    }

    /// <summary>
    /// 对一段已有字节做分类（不碰磁盘）。加密备份恢复与库文件解锁共用这一条路径，
    /// 保证"备份文件"和"库文件"的判定规则永远一致。
    /// </summary>
    public static VaultFileRead ClassifyBytes(byte[] raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var kind = VaultFormat.Classify(raw);
        if (kind == VaultFileKind.V2 && VaultFormat.TryReadHeader(raw, out var header, out _))
        {
            return new VaultFileRead(raw, kind, header, raw[VaultFormat.HeaderLength..]);
        }

        // 非 V2 一律不给 Body：调用方在类型上就拿不到"可解密的体"，不必靠自觉不去解。
        return new VaultFileRead(raw, kind, default, null);
    }

    /// <summary>
    /// 原子落盘：写 <c>.tmp</c>（WriteThrough + Flush(true)）→ 为旧文件留一份滚动备份
    /// → <c>File.Move(overwrite: true)</c> 替换 → 轮转保留 3 份备份 → 清掉可能残留的 tmp。
    ///
    /// 为什么先备份再替换：Windows 上同卷 Move 替换是原子的，"磁盘上始终存在一个完整可用的库"
    /// 由它保证；备份只是让用户还能回到上一版，失败也不阻断保存。
    /// </summary>
    public static void Save(string path, byte[] fileBytes, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(fileBytes);

        var temporary = path + VaultBackupRotation.TemporarySuffix;
        var directory = Path.GetDirectoryName(path);
        try
        {
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var stream = new FileStream(
                       temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(fileBytes);
                stream.Flush(flushToDisk: true);
            }

            ct.ThrowIfCancellationRequested();

            if (File.Exists(path))
            {
                VaultBackupRotation.CreateBackup(path);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            // 失败必须清 tmp：留着半截文件会让下一次"孤儿 tmp"清理误以为有未完成的保存。
            TryDeleteFile(temporary);
            throw;
        }

        VaultBackupRotation.Prune(path);
        VaultBackupRotation.CleanupTemporaryFiles(path);
    }

    /// <summary>异步落盘（B 批的保存管线直接复用；本批调用方很少，但接口先立住）。</summary>
    public static Task SaveAsync(string path, byte[] fileBytes, CancellationToken ct = default)
        => Task.Run(() => Save(path, fileBytes, ct), ct);

    /// <summary>按时间倒序列出滚动备份（诊断与测试用；恢复走文件选择器，不依赖这份清单）。</summary>
    public static IReadOnlyList<string> ListBackups(string path) => VaultBackupRotation.List(path);

    /// <summary>
    /// 为"结构损坏/无法识别"的库文件留一份带时间戳的副本。
    ///
    /// 只对 <see cref="VaultFileKind.StructurallyInvalid"/> 与 <see cref="VaultFileKind.Unknown"/> 生效：
    /// 前者的字节已经不可解析（有保存价值），后者不是我们的文件（更要留证据）。
    /// 相反：认证失败可能只是手滑输错密码（不产生产物），v1 与更新版本是我们主动拒绝的正常文件
    /// （零写盘是硬约束），这三类都不许备份。
    ///
    /// 上限：同一会话一次、同目录最多 3 份、单份与总量 50MB。**超限只提示，不删用户任何文件。**
    /// 返回新生成的备份路径；未生成返回 null（原因见 <see cref="LastNotice"/>）。
    /// </summary>
    public static string? TryCreateCorruptBackup(string path, VaultFileKind kind)
    {
        LastNotice = null;

        if (kind is not (VaultFileKind.StructurallyInvalid or VaultFileKind.Unknown))
        {
            return null;
        }

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath) ?? "";
            var size = new FileInfo(fullPath).Length;

            if (size > VaultLimits.MaxCorruptBackupBytes)
            {
                LastNotice = VaultMessages.CorruptBackupLimitReached(fullPath);
                Log.Info($"密码库：文件 {size} 字节超过损坏备份上限，未备份。path={fullPath}");
                return null;
            }

            lock (BackupGate)
            {
                if (!BackedUpPaths.Add(fullPath))
                {
                    // 同一会话只备份一次：用户反复点解锁不该在目录里堆出十份同样的垃圾。
                    return null;
                }

                var existing = VaultBackupRotation.ListCorruptBackups(fullPath);
                var total = 0L;
                foreach (var file in existing)
                {
                    try
                    {
                        total += new FileInfo(file).Length;
                    }
                    catch (Exception ex)
                    {
                        Log.Info("密码库：统计损坏备份体积失败：" + ex.Message);
                    }
                }

                if (existing.Count >= VaultLimits.MaxCorruptBackups
                    || total + size > VaultLimits.MaxCorruptBackupBytes)
                {
                    LastNotice = VaultMessages.CorruptBackupLimitReached(fullPath);
                    Log.Info($"密码库：损坏备份已达上限（{existing.Count} 份 / {total} 字节），未备份。");
                    return null;
                }

                var backup = VaultBackupRotation.CorruptBackupPath(fullPath, DateTimeOffset.Now);
                File.Copy(fullPath, backup, overwrite: false);
                LastNotice = VaultMessages.CorruptBackupCreated(Path.GetFileName(backup));
                Log.Info($"密码库：结构损坏的文件已备份为 {Path.GetFileName(backup)}（{size} 字节）。");
                return backup;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            LastNotice = VaultMessages.CorruptBackupLimitReached(path);
            return null;
        }
    }

    /// <summary>清空"本会话已备份"记录（单测隔离用；生产不会调用）。</summary>
    internal static void ResetCorruptBackupTracking()
    {
        lock (BackupGate)
        {
            BackedUpPaths.Clear();
        }

        LastNotice = null;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Log.Info("密码库：删除临时文件失败：" + ex.Message);
        }
    }
}
