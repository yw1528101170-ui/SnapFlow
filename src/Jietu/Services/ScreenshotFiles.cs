namespace Jietu;

internal static class ScreenshotFiles
{
    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        "SnapFlow");

    public static string TempPngPath() =>
        Path.Combine(AppPaths.Temp, Guid.NewGuid().ToString("N") + ".png");

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
}
