using System.Runtime.InteropServices;

namespace Jietu;

internal static partial class NativeMethods
{
    /// <summary>
    /// WDA_EXCLUDEFROMCAPTURE：窗口从抓屏/录屏里完全消失（BitBlt / DXGI 桌面复制 / WGC 都拿不到它）。
    /// Windows 10 2004（19041）及以上才支持；更低版本设置会失败，此时只能退化为「照常显示、可能被截进图」。
    /// </summary>
    public const uint WdaExcludeFromCapture = 0x00000011;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
}
