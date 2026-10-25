#if DEBUG
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Dispatching;

namespace Jietu;

/// <summary>
/// 开发期热重载桥（仅 Debug 编译，且必须用 <c>--dev</c> 启动才生效）。
///
/// 解决的问题：WinUI 3 在没有 Visual Studio 的环境里拿不到 XAML 热重载，改完代码只能
/// “手动关掉托盘进程 → 构建 → 再双击 exe”。而进程活着的时候 bin 里的 exe/dll 被系统锁住，
/// 构建会直接 MSB3021 失败——所以必须先关，这就是热重载跑不起来的根因。
///
/// 这里让进程自己开一条命名管道 <c>SnapFlow.Dev.&lt;pid&gt;</c>，scripts/dev.ps1 在构建前
/// 请它优雅退出：先记下当前开着哪些窗口，再走 AppHost.Shutdown 的完整清理（热键 / 托盘 /
/// SQLite / 密码库），脚本随后替换二进制并重启，新进程把窗口重新打开——
/// “保存文件 → 界面更新”整条链路不再需要人工介入。
/// </summary>
internal static class DevBridge
{
    private static readonly string SessionFile =
        Path.Combine(Path.GetTempPath(), "snapflow-dev-session.json");

    /// <summary>命令行是否带 --dev。Release 编译里该类型整体不存在。</summary>
    public static bool Enabled { get; set; }

    public static string PipeName => "SnapFlow.Dev." + Environment.ProcessId;

    public static void Attach(AppHost host, DispatcherQueue queue)
    {
        var thread = new Thread(() => Listen(host, queue))
        {
            IsBackground = true,
            Name = "SnapFlow.DevBridge",
        };
        thread.Start();
        Log.Info("DevBridge 已就绪，管道 " + PipeName);
    }

    private static void Listen(AppHost host, DispatcherQueue queue)
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.None);
                server.WaitForConnection();

                using var reader = new StreamReader(server, Encoding.UTF8, false, 256, leaveOpen: true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), 256, leaveOpen: true)
                {
                    AutoFlush = true,
                };

                var command = (reader.ReadLine() ?? "").Trim().ToLowerInvariant();
                if (command == "exit")
                {
                    // 回执先发出去，脚本据此确认“对方收到了，开始等它退”。
                    // 读窗口状态必须在 UI 线程，所以保存与退出合并成一次调度。
                    writer.WriteLine("bye");
                    if (!queue.TryEnqueue(() =>
                        {
                            SaveSession(host);
                            host.Shutdown();
                        }))
                    {
                        // UI 队列已经停了（进程正在退），直接收尾即可。
                        host.Shutdown();
                    }

                    return;
                }

                writer.WriteLine("ok");
            }
            catch (Exception ex)
            {
                // 脚本提前关掉管道属于正常流程，只记一条不做重试风暴。
                Log.Info("DevBridge：" + ex.Message);
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>记下“这次重启要还原哪些窗口”。只在 UI 线程调用。</summary>
    private static void SaveSession(AppHost host)
    {
        try
        {
            File.WriteAllText(SessionFile, JsonSerializer.Serialize(host.CaptureDevSession()));
        }
        catch (Exception ex)
        {
            Log.Info("DevBridge 保存窗口状态失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 把上一次退出时开着的窗口还原回来。<returns>true</returns> 表示确实有会话可还原；
    /// 为 false 时调用方（AppHost）会自行决定开发期要不要默认弹一个窗口出来。
    /// </summary>
    public static bool RestoreSession(AppHost host, DispatcherQueue queue)
    {
        try
        {
            if (!File.Exists(SessionFile))
            {
                return false;
            }

            var json = File.ReadAllText(SessionFile);
            // 先删再还原：崩溃 / 强杀留下的旧文件不该反复触发还原。
            File.Delete(SessionFile);
            var state = JsonSerializer.Deserialize<DevSessionState>(json);
            if (state == null)
            {
                return false;
            }

            // 延后一帧再开窗，避免和 Start 里正在做的注册热键抢 UI 线程。
            return queue.TryEnqueue(() => host.RestoreDevSession(state));
        }
        catch (Exception ex)
        {
            Log.Info("DevBridge 恢复窗口状态失败：" + ex.Message);
            return false;
        }
    }
}

/// <summary>开发期热重载需要跨进程延续的窗口状态。</summary>
internal sealed class DevSessionState
{
    public bool Settings { get; set; }
    public bool History { get; set; }
    public bool Vault { get; set; }
}
#endif
