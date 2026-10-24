namespace Jietu.Tests;

public sealed class SensitiveForegroundTests
{
    [Fact]
    public void RecognizesPasswordManagers()
    {
        Assert.True(SensitiveForeground.IsSensitiveProcess("KeePass.exe"));
        Assert.False(SensitiveForeground.IsSensitiveProcess("notepad.exe"));
    }

    [Fact]
    public void TerminalsBlockedByDefault_CanOptIn()
    {
        try
        {
            Assert.True(SensitiveForeground.IsTerminal("WindowsTerminal.exe"));
            Assert.False(SensitiveForeground.IsSensitiveProcess("WindowsTerminal.exe"));
            Assert.True(SensitiveForeground.BlocksProcess("cmd.exe"));

            SensitiveForeground.SetRecordTerminals(true);
            Assert.False(SensitiveForeground.BlocksProcess("cmd.exe"));
        }
        finally
        {
            // 全局开关必须复位，避免影响并行执行的其他测试。
            SensitiveForeground.SetRecordTerminals(false);
        }
    }
}

public sealed class AiVisionServiceTests
{
    [Theory]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.0.1")]
    [InlineData("::1")]
    [InlineData("[::1]")]
    public void IsLoopbackHost_AcceptsLoopback(string host)
    {
        Assert.True(AiVisionService.IsLoopbackHost(host));
    }

    [Theory]
    [InlineData("api.openai.com")]
    [InlineData("10.0.0.5")]     // 内网 IP 也不是回环：明文出网一样不可接受
    [InlineData("192.168.1.1")]
    [InlineData("[::2]")]
    public void IsLoopbackHost_RejectsEverythingElse(string host)
    {
        Assert.False(AiVisionService.IsLoopbackHost(host));
    }
}

public sealed class ScreenshotFilesTests
{
    private string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jietu-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void PurgeSaved_DeletesOnlyStaleAppNamedFiles()
    {
        var dir = TempDir();
        try
        {
            var stale = Path.Combine(dir, "SnapFlow-20200101-000000.png");
            File.WriteAllText(stale, "x");
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-40));

            var fresh = Path.Combine(dir, "SnapFlow-20990101-000000.png");
            File.WriteAllText(fresh, "x");

            var userStale = Path.Combine(dir, "我的备份.png");
            File.WriteAllText(userStale, "x");
            File.SetLastWriteTimeUtc(userStale, DateTime.UtcNow.AddDays(-40));

            var wrongShape = Path.Combine(dir, "SnapFlow-笔记.png");
            File.WriteAllText(wrongShape, "x");
            File.SetLastWriteTimeUtc(wrongShape, DateTime.UtcNow.AddDays(-40));

            var removed = ScreenshotFiles.PurgeSaved(dir, 30);

            Assert.Equal(1, removed);
            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(fresh));
            Assert.True(File.Exists(userStale));
            Assert.True(File.Exists(wrongShape));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void PurgeSaved_MissingFolder_ReturnsZero()
    {
        var missing = Path.Combine(Path.GetTempPath(), "jietu-tests-missing-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(0, ScreenshotFiles.PurgeSaved(missing, 30));
    }

    [Fact]
    public void PurgeSaved_ClampsRetentionDays()
    {
        // days 传入非法值（0/负数）时按 1 天计算，不应抛异常。
        var dir = TempDir();
        try
        {
            Assert.Equal(0, ScreenshotFiles.PurgeSaved(dir, 0));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
