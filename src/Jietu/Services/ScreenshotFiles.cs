namespace Jietu;

internal static class ScreenshotFiles
{
    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        "SnapFlow");

    public static string TempPngPath() =>
        Path.Combine(AppPaths.Temp, Guid.NewGuid().ToString("N") + ".png");

    /// <summary>
    /// 会话内限量清理临时 PNG：只保留最近 4 份。AI 窗口的复制图片/贴图每次都写
    /// 一个新 GUID 文件，原来只在启动时清理一次，长会话会累积数十 MB。
    /// 删最旧的而非全部：刚写盘并放进剪贴板的文件不能动。
    /// </summary>
    public static void SweepTemp()
    {
        if (!Directory.Exists(AppPaths.Temp))
        {
            return;
        }

        try
        {
            var files = Directory.EnumerateFiles(AppPaths.Temp, "*.png")
                .Select(f => (Path: f, Write: File.GetLastWriteTimeUtc(f)))
                .OrderByDescending(t => t.Write)
                .ToList();
            for (var i = 4; i < files.Count; i++)
            {
                try
                {
                    File.Delete(files[i].Path);
                }
                catch (Exception ex)
                {
                    Log.Info("清理临时图片失败：" + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Info("扫描临时图片失败：" + ex.Message);
        }
    }

    public static void CleanupTemp()
    {
        if (!Directory.Exists(AppPaths.Temp))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(AppPaths.Temp, "*.png"))
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                Log.Info("清理临时图片失败：" + ex.Message);
            }
        }
    }

    public static string Save(byte[] png, string folder)
    {
        Directory.CreateDirectory(folder);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(folder, "SnapFlow-" + stamp + ".png");
        for (var n = 2; File.Exists(path) && n < 1000; n++)
        {
            path = Path.Combine(folder, "SnapFlow-" + stamp + "-" + n + ".png");
        }

        File.WriteAllBytes(path, png);
        return path;
    }

    /// <summary>
    /// 「保存到文件夹」的截图副本不受历史库管理，若不清理会无限累积、敏感截图长期留存。
    /// 按与历史相同的保留天数删除本应用命名（SnapFlow-年月日-时分秒[序号].png）且超期的文件；
    /// 用户自己放进该目录的其他文件一概不动。
    /// </summary>
    public static int PurgeSaved(string folder, int retentionDays)
    {
        var days = Math.Clamp(retentionDays, AppSettings.MinRetentionDays, AppSettings.MaxRetentionDays);
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                return 0;
            }

            var cutoffUtc = DateTime.UtcNow.AddDays(-days);
            var removed = 0;
            foreach (var path in Directory.EnumerateFiles(folder, "SnapFlow-????????-??????*.png"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) >= cutoffUtc)
                    {
                        continue;
                    }

                    File.Delete(path);
                    removed++;
                }
                catch (Exception ex)
                {
                    Log.Info("清理保存的截图失败：" + ex.Message);
                }
            }

            return removed;
        }
        catch (Exception ex)
        {
            Log.Info("扫描保存目录失败：" + ex.Message);
            return 0;
        }
    }
}
