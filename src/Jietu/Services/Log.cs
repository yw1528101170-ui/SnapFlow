namespace Jietu;

public static class Log
{
    private static readonly object Gate = new();
    private static bool _dirEnsured;
    private static long _estimatedBytes = -1;

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message) => Write("ERROR", message);

    public static void Error(Exception exception) => Write("ERROR", exception.ToString());

    private static void Write(string level, string message)
    {
        try
        {
            if (!_dirEnsured)
            {
                Directory.CreateDirectory(AppPaths.Root);
                _dirEnsured = true;
            }

            var path = Path.Combine(AppPaths.Root, "log.txt");
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}";
            lock (Gate)
            {
                File.AppendAllText(path, line);
                if (_estimatedBytes < 0)
                {
                    _estimatedBytes = new FileInfo(path).Length;
                }
                else
                {
                    _estimatedBytes += line.Length;
                }

                if (_estimatedBytes > 200_000)
                {
                    // 滚动改为一次 rename：整份旧日志挪到 log.old.txt（覆盖上一份备份），
                    // 不再锁内 ReadAllText+WriteAllText 全文件读写；随后从空文件继续追加。
                    // _estimatedBytes 归位为 -1，下次写入按实际文件长度重新计量，阈值行为保持不变。
                    File.Move(path, Path.Combine(AppPaths.Root, "log.old.txt"), overwrite: true);
                    _estimatedBytes = -1;
                }
            }
        }
        catch
        {
            // 日志失败不影响主流程。
        }
    }
}
