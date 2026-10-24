using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Jietu;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--self-test"))
        {
            AttachParentConsole();
            Environment.Exit(SelfTest.Run());
        }

        if (args.Contains("--capture-test"))
        {
            AttachParentConsole();
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Environment.Exit(CaptureService.CheckPrimary());
        }

        var previewIndex = Array.IndexOf(args, "--ui-preview");
        if (previewIndex >= 0)
        {
            AttachParentConsole();
            var directory = previewIndex + 1 < args.Length ? args[previewIndex + 1] : Path.GetTempPath();
            Environment.Exit(OverlayPreview.Run(directory));
        }

        if (!SingleInstance.Acquire())
        {
            SingleInstance.SignalExisting();
            return;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        NativeMethods.EnableDarkModeSupport();
        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });

        // 兜底强退：Application.Start 返回说明 UI 已退出，但个别组件若遗留前台线程
        // （硬件监控采样 / ETW 会话等），进程不会自然结束，会变成「无托盘、无热键、
        // 却占着 exe 与 history.db」的僵尸，还会导致卸载时文件被锁删不干净。
        Environment.Exit(0);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    private static void AttachParentConsole()
    {
        try
        {
            AttachConsole(0xFFFFFFFF);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
        catch
        {
            // 没有父控制台时，自测仍把结果写到临时文件。
        }
    }
}
