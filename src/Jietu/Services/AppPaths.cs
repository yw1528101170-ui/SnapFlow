namespace Jietu;

public static class AppPaths
{
    private const string AppFolderName = "SnapFlow";

    // 数据目录放在 LocalApplicationData：Roaming 会随域漫游同步，history.db / images 这类
    // 本机缓存数据不适合漫游，也容易在多机登录时互相踩坏。
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppFolderName);

    public static string Database => Path.Combine(Root, "history.db");
    public static string Images => Path.Combine(Root, "images");
    public static string Thumbs => Path.Combine(Root, "thumbs");
    public static string Temp => Path.Combine(Root, "temp");
    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static void Ensure()
    {
        try
        {
            MigrateLegacyRoots();
        }
        catch (Exception ex)
        {
            // 迁移只是“尽量保留旧数据”，失败也必须能继续用新目录启动。
            Log.Info("迁移旧数据目录失败，继续使用新目录：" + ex.Message);
        }

        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Images);
        Directory.CreateDirectory(Thumbs);
        Directory.CreateDirectory(Temp);
    }

    /// <summary>
    /// 一次性迁移：旧版本数据在 Roaming（%APPDATA%\SnapFlow，更老是 %APPDATA%\Jietu），
    /// 新版本改到 Local（%LOCALAPPDATA%\SnapFlow）。只在新目录没有用户数据时执行，
    /// 优先 Move，失败退回复制并保留旧目录；绝不覆盖、不删除任何已有用户数据。
    /// </summary>
    private static void MigrateLegacyRoots()
    {
        if (HasUserData(Root))
        {
            return;
        }

        var source = PickLegacyRoot();
        if (source == null)
        {
            return;
        }

        if (!Directory.Exists(Root))
        {
            try
            {
                Directory.Move(source, Root);
                Log.Info("数据目录已迁移：" + source + " → " + Root);
                return;
            }
            catch (Exception ex)
            {
                // 跨卷或目录被占用时 Move 会失败，退回复制（旧目录保留，避免丢数据）。
                Log.Info("移动旧数据目录失败，改为复制：" + ex.Message);
            }
        }

        // 先整份复制到 Local 下的暂存目录，成功后再落到 Root：中途失败不会留下半份数据。
        var staging = Root + ".migrating";
        try
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            CopyDirectory(source, staging);
            if (Directory.Exists(Root))
            {
                // 新目录里只有日志等非用户数据（HasUserData 已确认），逐文件合并且不覆盖已有文件。
                CopyDirectory(staging, Root, overwrite: false);
                Directory.Delete(staging, recursive: true);
            }
            else
            {
                Directory.Move(staging, Root);
            }

            Log.Info("数据目录已复制迁移：" + source + " → " + Root + "（旧目录保留）");
        }
        catch (Exception ex)
        {
            Log.Error("复制迁移数据目录失败，继续使用新目录：" + ex.Message);
            try
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
            catch
            {
                // 暂存目录清理失败不影响主流程。
            }
        }
    }

    /// <summary>新目录里是否已经有用户数据；只有日志文件不算数据，仍允许迁移。</summary>
    private static bool HasUserData(string root)
    {
        if (!Directory.Exists(root))
        {
            return false;
        }

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(root))
            {
                if (string.Equals(Path.GetFileName(entry), "log.txt", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return true;
            }
        }
        catch
        {
            // 判断不了就按“已有数据”处理，宁可不迁移也不覆盖。
            return true;
        }

        return false;
    }

    /// <summary>在旧数据目录里挑“数据更完整”的那个；都不存在时返回 null。</summary>
    private static string? PickLegacyRoot()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new[]
        {
            Path.Combine(roaming, AppFolderName), // 1.1 及更早版本的 %APPDATA%\SnapFlow
            Path.Combine(roaming, "Jietu"),       // 更老的 %APPDATA%\Jietu
        };
        string? best = null;
        var bestScore = long.MinValue;
        foreach (var candidate in candidates)
        {
            if (!Directory.Exists(candidate))
            {
                continue;
            }

            var score = MigrationScore(candidate);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>数据完整度打分：有历史库最优先，其次看修改时间（越新越可能是当前在用的数据），最后看库大小。</summary>
    private static long MigrationScore(string directory)
    {
        long score = 0;
        try
        {
            var database = Path.Combine(directory, "history.db");
            if (File.Exists(database))
            {
                var info = new FileInfo(database);
                score += 1_000_000_000_000;
                score += info.LastWriteTimeUtc.Ticks / 1000;
                score += info.Length;
            }
        }
        catch
        {
            // 读不到库信息时按剩余信号打分。
        }

        try
        {
            if (File.Exists(Path.Combine(directory, "settings.json")))
            {
                score += 1_000;
            }
        }
        catch
        {
            // 同上。
        }

        return score;
    }

    private static void CopyDirectory(string source, string target, bool overwrite = true)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            if (!overwrite && File.Exists(destination))
            {
                // 不覆盖已有文件（合并进非空新目录时 log.txt 等可能同名）。
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite);
        }
    }
}
