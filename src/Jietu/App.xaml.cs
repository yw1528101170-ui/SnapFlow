using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Jietu;

public partial class App : Application
{
    private AppHost? _host;

    // 全局异常分级响应：偶发异常吞掉并 Toast 提示；短窗口内反复异常说明某组件已进入
    // 持续的坏状态，继续吞只会变成“界面失灵但进程存活”的僵尸，升级为弹窗 + 干净退出。
    private const int ErrorBurstCount = 3;
    private static readonly TimeSpan ErrorBurstWindow = TimeSpan.FromSeconds(60);
    private readonly Queue<DateTimeOffset> _recentErrors = new();

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        // WinUI 的 UnhandledException 只接 UI 线程；后台线程异常会直接杀进程，
        // 挂 AppDomain 钩子只为在日志里留下根因（救不回进程）。
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error(e.ExceptionObject as Exception
                      ?? new Exception(e.ExceptionObject?.ToString() ?? "非异常致命错误"));
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception);
        e.Handled = true;

        var host = _host;
        if (host is null)
        {
            // AppHost 还没建出来就出错（如消息窗口注册失败）：托盘、热键、任何 UI 都不存在，
            // 进程继续活着只会是一个占着单实例互斥体的空壳，还会把二次启动拦在门外。
            // 必须让用户看见原因并以非零码退出。
            try
            {
                NativeMethods.MessageBox(
                    IntPtr.Zero,
                    "SnapFlow 启动失败：\n\n"
                    + (e.Exception?.Message ?? "未知错误")
                    + "\n\n详情请查看日志：%LOCALAPPDATA%\\SnapFlow\\log.txt",
                    "SnapFlow - 启动错误",
                    NativeMethods.MbIconError);
            }
            catch
            {
                // 连弹窗都失败时只能直接退出。
            }

            Environment.Exit(1);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        _recentErrors.Enqueue(now);
        while (_recentErrors.Count > 0 && now - _recentErrors.Peek() > ErrorBurstWindow)
            _recentErrors.Dequeue();

        if (_recentErrors.Count >= ErrorBurstCount)
        {
            _recentErrors.Clear();
            PromptFatalError(host, e.Exception);
            return;
        }

        try
        {
            host.Toast.Show("SnapFlow 遇到内部错误，已自动恢复；若反复出现请查看日志");
        }
        catch
        {
            // Toast 自身失败时不再放大异常
        }
    }

    private void PromptFatalError(AppHost host, Exception ex)
    {
        try
        {
            var res = NativeMethods.MessageBox(
                IntPtr.Zero,
                "SnapFlow 连续出现内部错误，可能已处于异常状态。\n\n"
                + "建议立即重启应用以恢复正常功能。\n\n"
                + ex.Message,
                "SnapFlow - 内部错误",
                NativeMethods.MbOkCancel | NativeMethods.MbIconError);
            if (res == NativeMethods.IdOk)
                host.Restart();
            else
                host.Shutdown();
        }
        catch
        {
            try { host.Shutdown(); } catch { /* 退出失败只能交给进程自然结束 */ }
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _host = new AppHost(DispatcherQueue.GetForCurrentThread());
        _host.Start();
    }
}
