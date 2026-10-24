using Microsoft.UI.Xaml;

namespace Jietu;

internal static class AppIcon
{
    public static string FilePath { get; } = Path.Combine(AppContext.BaseDirectory, "Assets", "App.ico");

    public static void Apply(Window window)
    {
        try
        {
            if (File.Exists(FilePath))
            {
                window.AppWindow.SetIcon(FilePath);
            }
        }
        catch (Exception ex)
        {
            Log.Info("窗口图标未设置：" + ex.Message);
        }
    }

    public static IntPtr LoadTray()
    {
        if (!File.Exists(FilePath))
        {
            return IntPtr.Zero;
        }

        var size = NativeMethods.GetSystemMetrics(49);
        if (size < 16)
        {
            size = 16;
        }

        return NativeMethods.LoadImage(IntPtr.Zero, FilePath, 1, size, size, 0x10);
    }
}
