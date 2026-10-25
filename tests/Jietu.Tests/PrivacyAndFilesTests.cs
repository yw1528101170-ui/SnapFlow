using System.Net;

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

public sealed class AiVisionEndpointTests
{
    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://api.siliconflow.cn/v1/chat/completions")]
    [InlineData("http://127.0.0.1:11434/v1")]   // Ollama 回环：http 明文仅此放行
    [InlineData("http://localhost:8000/v1/")]   // 尾斜杠要归一
    [InlineData("http://[::1]:9000/v1")]
    [InlineData("")]                            // 空值回退默认端点
    public void TryResolveApiBase_AcceptsValid(string url)
    {
        Assert.True(AiVisionService.TryResolveApiBase(url, out var uri, out var error));
        Assert.Null(error);
        Assert.False(uri.AbsoluteUri.EndsWith('/'));
    }

    [Theory]
    [InlineData("api.openai.com/v1")]     // 缺协议头
    [InlineData("ftp://example.com")]     // 非 http(s)
    [InlineData("http://10.0.0.5:8080")]  // 明文端点且非回环
    [InlineData("http://192.168.1.1/v1")]
    [InlineData("junk")]
    public void TryResolveApiBase_Rejects(string url)
    {
        Assert.False(AiVisionService.TryResolveApiBase(url, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Clip_TruncatesLongErrorBodies()
    {
        var body = new string('x', 5000);
        var clipped = AiVisionService.Clip(body);
        Assert.True(clipped.Length < 500);
        Assert.EndsWith("…(已截断)", clipped);
        Assert.Equal("short", AiVisionService.Clip("short"));
    }
}

/// <summary>
/// 用本机回环假服务器验证流式请求链路：SSE 分片、finish_reason 截断标记、
/// 忽略 stream 的完整 JSON 回退、429 限流文案。请求侧仅放行 http://127.0.0.1，正好复用该规则。
/// </summary>
public sealed class AiVisionStreamingTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _baseUrl;

    public AiVisionStreamingTests()
    {
        // HttpListener 前缀不支持 0 端口：先探测一个空闲端口再监听。
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _baseUrl = $"http://127.0.0.1:{port}/v1";
    }

    private string BaseUrl => _baseUrl;

    private void Serve(Func<HttpListenerResponse, Task> responder)
    {
        // 监听器在构造函数里已 Start，内核会为早到的连接排队；这里只挂接收循环。
        _ = Task.Run(async () =>
        {
            try
            {
                while (_listener.IsListening)
                {
                    var context = await _listener.GetContextAsync();
                    await responder(context.Response);
                }
            }
            catch (Exception)
            {
                // 监听器随 Dispose 关闭，属正常退出
            }
        });
    }

    [Fact]
    public async Task SseChunks_AreStreamedAndFinishLengthAppendsWarning()
    {
        Serve(async response =>
        {
            response.ContentType = "text/event-stream";
            var body = string.Join("\n\n",
            [
                """data: {"choices":[{"delta":{"content":"你好"}}]}""",
                """data: {"choices":[{"delta":{"content":"，世界"}}]}""",
                """data: {"choices":[{"delta":{},"finish_reason":"length"}]}""",
                "data: [DONE]",
            ]) + "\n\n";
            var buffer = System.Text.Encoding.UTF8.GetBytes(body);
            await response.OutputStream.WriteAsync(buffer);
            response.OutputStream.Close();
        });

        var settings = new AppSettings { AiApiKey = "test-key", AiApiUrl = BaseUrl };
        var deltas = new List<string>();
        var progress = new Progress<string>(deltas.Add);
        // 走主重载才能拿到流式回调（3 参便捷重载固定不带 delta）
        var answer = await AiVisionService.AskVisionAsync([new VisionTurn("user", "hi", null)], settings, delta: progress);
        await Task.Delay(150);

        Assert.Equal("你好，世界" + AiVisionContract.TruncatedSuffix, answer);
        Assert.Equal(["你好", "，世界"], deltas);
    }

    [Fact]
    public async Task NonStreamFallback_ParsesFullJsonBody()
    {
        Serve(async response =>
        {
            response.ContentType = "application/json";
            var buffer = System.Text.Encoding.UTF8.GetBytes(
                """{"choices":[{"message":{"content":"完整回答"},"finish_reason":"stop"}]}""");
            await response.OutputStream.WriteAsync(buffer);
            response.OutputStream.Close();
        });

        var settings = new AppSettings { AiApiKey = "test-key", AiApiUrl = BaseUrl };
        var answer = await AiVisionService.AskVisionAsync([], "hi", settings);
        Assert.Equal("完整回答", answer);
    }

    [Fact]
    public async Task RateLimit_GetsDedicatedMessage()
    {
        Serve(response =>
        {
            response.StatusCode = 429;
            var buffer = System.Text.Encoding.UTF8.GetBytes("rate limited");
            response.OutputStream.Write(buffer);
            response.OutputStream.Close();
            return Task.CompletedTask;
        });

        var settings = new AppSettings { AiApiKey = "test-key", AiApiUrl = BaseUrl };
        var answer = await AiVisionService.AskVisionAsync([], "hi", settings);
        Assert.StartsWith(AiVisionContract.RequestFailedPrefix, answer);
        Assert.Contains("429", answer);
        Assert.Contains("限流", answer);
    }

    public void Dispose() => _listener.Close();
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
