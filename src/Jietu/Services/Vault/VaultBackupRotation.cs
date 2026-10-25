using System.Text.RegularExpressions;

namespace Jietu;

/// <summary>
/// 滚动备份与临时文件命名规则。只操作**匹配自身命名规则**的文件：
/// 用户的其它文件（包括他手动复制的"vault - 副本.spv"）永远不在删除范围内。
///
/// 命名：<c>&lt;库文件名&gt;.yyyyMMdd-HHmmss.bak</c>，同秒冲突追加 <c>-2</c>、<c>-3</c>……
/// 定长时间戳让"按文件名降序"等价于"按时间降序"，省掉读文件属性。
///
/// 本批次只做"最小可用"：创建一份备份 + 保留 3 份 + 清理孤儿 tmp。
/// B 批的异步保存管线会复用同一套命名，不会另起规则。
/// </summary>
public static class VaultBackupRotation
{
    /// <summary>滚动备份保留份数。</summary>
    public const int KeepCount = 3;

    /// <summary>临时文件后缀（原子写的中转文件）。</summary>
    public const string TemporarySuffix = ".tmp";

    /// <summary>损坏备份的文件名中缀。</summary>
    public const string CorruptInfix = ".corrupt-";

    /// <summary>
    /// 把当前库文件复制为一份 <c>.bak</c>。失败**不抛**：原子替换本身已保证不会留半文件，
    /// 备份只是"多一层可回退"，不该因为它失败就把用户的保存整个挡下来。
    /// 返回备份路径，失败返回 null。
    /// </summary>
    public static string? CreateBackup(string vaultPath, DateTimeOffset? now = null)
    {
        try
        {
            if (!File.Exists(vaultPath))
            {
                return null;
            }

            var backup = NextFreePath(vaultPath, now ?? DateTimeOffset.Now, RollingBackupSuffix);
            File.Copy(vaultPath, backup, overwrite: false);
            return backup;
        }
        catch (Exception ex)
        {
            Log.Info("密码库：滚动备份失败（不阻断保存）：" + ex.Message);
            return null;
        }
    }

    /// <summary>只保留最近的 <see cref="KeepCount"/> 份滚动备份，返回删除份数。</summary>
    public static int Prune(string vaultPath)
    {
        var removed = 0;
        try
        {
            foreach (var file in List(vaultPath).Skip(KeepCount))
            {
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (Exception ex)
                {
                    Log.Info("密码库：删除旧备份失败：" + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Info("密码库：轮转备份失败：" + ex.Message);
        }

        return removed;
    }

    /// <summary>按文件名降序（= 时间降序）列出滚动备份。</summary>
    public static IReadOnlyList<string> List(string vaultPath)
    {
        var directory = Path.GetDirectoryName(vaultPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        var pattern = BuildRegex(vaultPath, @"\d{8}-\d{6}(-\d+)?\.bak$");
        return Directory
            .EnumerateFiles(directory, Path.GetFileName(vaultPath) + ".*.bak")
            .Where(file => pattern.IsMatch(Path.GetFileName(file)))
            .OrderByDescending(file => Path.GetFileName(file), StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>列出损坏备份（按文件名降序）。</summary>
    public static IReadOnlyList<string> ListCorruptBackups(string vaultPath)
    {
        var directory = Path.GetDirectoryName(vaultPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        var pattern = BuildRegex(vaultPath, @"corrupt-\d{8}-\d{6}(-\d+)?$");
        return Directory
            .EnumerateFiles(directory, Path.GetFileName(vaultPath) + CorruptInfix + "*")
            .Where(file => pattern.IsMatch(Path.GetFileName(file)))
            .OrderByDescending(file => Path.GetFileName(file), StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>损坏备份的完整路径（带时间戳，便于用户按时间比对与识别）。</summary>
    public static string CorruptBackupPath(string vaultPath, DateTimeOffset now)
        => NextFreePath(vaultPath, now, CorruptInfix);

    /// <summary>清理孤儿临时文件。库文件不存在时**不动手**：那种情况下 tmp 可能是唯一的数据副本。</summary>
    public static int CleanupTemporaryFiles(string vaultPath)
    {
        if (!File.Exists(vaultPath))
        {
            return 0;
        }

        var directory = Path.GetDirectoryName(vaultPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return 0;
        }

        var removed = 0;
        var name = Path.GetFileName(vaultPath);
        IEnumerable<string> candidates;
        try
        {
            candidates = Directory
                .EnumerateFiles(directory, name + "*" + TemporarySuffix)
                .Where(file => IsOwnTemporary(name, Path.GetFileName(file)))
                .ToArray();
        }
        catch (Exception ex)
        {
            Log.Info("密码库：枚举临时文件失败：" + ex.Message);
            return 0;
        }

        foreach (var file in candidates)
        {
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception ex)
            {
                Log.Info("密码库：删除临时文件失败：" + ex.Message);
            }
        }

        return removed;
    }

    /// <summary>tmp 文件名是否由本模块产生（<c>vault.spv.tmp</c> 或 <c>vault.spv.&lt;digits&gt;.tmp</c>）。</summary>
    internal static bool IsOwnTemporary(string vaultFileName, string candidate)
    {
        if (!candidate.StartsWith(vaultFileName + ".", StringComparison.OrdinalIgnoreCase)
            || !candidate.EndsWith(TemporarySuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var middle = candidate[(vaultFileName.Length + 1)..^TemporarySuffix.Length];
        return middle.Length == 0 || middle.All(char.IsAsciiDigit);
    }

    private const string RollingBackupSuffix = ".bak";

    /// <summary>
    /// 找下一个未被占用的名字。同秒冲突（同一秒内保存两次）必须能区分，
    /// 否则第二份备份会覆盖第一份，等于凭空少一个可回退版本。
    /// </summary>
    private static string NextFreePath(string vaultPath, DateTimeOffset now, string suffix)
    {
        var stamp = now.ToString(VaultLimits.BackupTimestampFormat);
        var directory = Path.GetDirectoryName(vaultPath) ?? "";
        var name = Path.GetFileName(vaultPath);

        // suffix 决定时间戳放在文件名的哪一侧：
        // 滚动备份是 <name>.<stamp>.bak（按名排序即时间序），损坏备份是 <name>.corrupt-<stamp>。
        string Build(int index) => suffix == CorruptInfix
            ? $"{name}{CorruptInfix}{stamp}{(index > 1 ? "-" + index : "")}"
            : $"{name}.{stamp}{(index > 1 ? "-" + index : "")}{suffix}";

        var candidate = Path.Combine(directory, Build(1));
        var counter = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory, Build(counter));
            counter++;
        }

        return candidate;
    }

    private static Regex BuildRegex(string vaultPath, string tail)
        => new("^" + Regex.Escape(Path.GetFileName(vaultPath)) + @"\." + tail, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
