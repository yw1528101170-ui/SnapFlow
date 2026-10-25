using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Jietu;

/// <summary>
/// 原生消息宿主：浏览器以 `SnapFlow.exe --native-messaging` 拉起（注册表清单）。
/// 浏览器侧协议是「4 字节本机序长度前缀 + UTF-8 JSON」，桥管道侧是「按行 JSON」，
/// 本进程只做两侧的分帧转换与转发，不解析内容、不持有任何密钥。
/// </summary>
public static class NativeMessagingHost
{
    private const int MaxFrameBytes = 4 * 1024 * 1024;

    public static int Run()
    {
        try
        {
            using var stdin = Console.OpenStandardInput();
            using var stdout = Console.OpenStandardOutput();
            using var pipe = new NamedPipeClientStream(".", VaultBridge.PipeName, PipeDirection.InOut);
            try
            {
                // 主程序可能还没跑或桥未启动：给 5 秒重试窗口。
                var connected = false;
                for (var attempt = 0; attempt < 10 && !connected; attempt++)
                {
                    try
                    {
                        pipe.Connect(500);
                        connected = true;
                    }
                    catch (TimeoutException)
                    {
                    }
                }

                if (!connected)
                {
                    WriteFrame(stdout, Encoding.UTF8.GetBytes(
                        JsonSerializer.Serialize(new { type = "error", reason = "app-not-running" })));
                    return 1;
                }

                // 两个方向各一个任务，任一端结束即整体退出（浏览器关闭宿主进程是常态）。
                var stdinToPipe = Task.Run(async () =>
                {
                    while (true)
                    {
                        var length = ReadFrameLength(stdin);
                        if (length < 0)
                        {
                            return;
                        }

                        var frame = new byte[length];
                        await ReadExactlyAsync(stdin, frame);
                        // 管道侧按行分帧。
                        frame = [.. frame, (byte)'\n'];
                        await pipe.WriteAsync(frame);
                        await pipe.FlushAsync();
                    }
                });

                var pipeToStdout = Task.Run(async () =>
                {
                    var line = new MemoryStream();
                    var buffer = new byte[8 * 1024];
                    while (true)
                    {
                        var read = await pipe.ReadAsync(buffer);
                        if (read <= 0)
                        {
                            return;
                        }

                        for (var i = 0; i < read; i++)
                        {
                            if (buffer[i] == '\n')
                            {
                                if (line.Length > 0)
                                {
                                    WriteFrame(stdout, line.ToArray());
                                    line.SetLength(0);
                                }

                                continue;
                            }

                            if (buffer[i] != '\r')
                            {
                                line.WriteByte(buffer[i]);
                            }
                        }
                    }
                });

                Task.WaitAny(stdinToPipe, pipeToStdout);
                return 0;
            }
            finally
            {
                pipe.Dispose();
            }
        }
        catch (Exception ex)
        {
            // 宿主进程静默失败即可，浏览器会显示「原生宿主异常退出」。
            Log.Info("NativeMessagingHost 退出：" + ex.Message);
            return 1;
        }
    }

    /// <summary>读 4 字节本机序长度前缀；流结束返回 -1；超限返回 -2（协议滥用保护）。</summary>
    private static int ReadFrameLength(Stream stream)
    {
        Span<byte> prefix = stackalloc byte[4];
        var read = 0;
        while (read < 4)
        {
            var n = stream.Read(prefix[read..]);
            if (n <= 0)
            {
                return -1;
            }

            read += n;
        }

        var length = BitConverter.ToInt32(prefix);
        return length is < 0 or > MaxFrameBytes ? -2 : length;
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read));
            if (n <= 0)
            {
                throw new EndOfStreamException();
            }

            read += n;
        }
    }

    private static void WriteFrame(Stream stream, byte[] payload)
    {
        var prefix = BitConverter.GetBytes(payload.Length);
        stream.Write(prefix);
        stream.Write(payload);
        stream.Flush();
    }
}
