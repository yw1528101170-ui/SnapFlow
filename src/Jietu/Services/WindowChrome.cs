using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Jietu;

internal static class WindowChrome
{
    /// <summary>DWM 属性：窗口跟随系统明暗（标题栏 / 材质配色）。</summary>
    private const int DwmwaUseImmersiveDarkMode = 20;

    /// <summary>DWM 属性：系统材质种类。要求 Windows 11 22H2+，低版本返回错误码。</summary>
    private const int DwmwaSystemBackdropType = 38;

    /// <summary>系统材质档位：3 = 弹层亚克力（Windows 11 弹出菜单那种，最透的一档实时模糊）、2 = Mica。</summary>
    private const int DwmsbtTransientWindow = 3;
    private const int DwmsbtMainWindow = 2;

    public static void Apply(Window window, UIElement titleBar, Grid root)
    {
        AppIcon.Apply(window);
        window.ExtendsContentIntoTitleBar = true;
        window.SetTitleBar(titleBar);
        var bar = window.AppWindow.TitleBar;
        bar.PreferredHeightOption = TitleBarHeightOption.Tall;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        try
        {
            // 背景材质走 DWM 直通：DWM 合成器在 GPU 上实时模糊窗口背后的桌面——
            // 这就是苹果 vibrancy 的 Windows 对应物。应用零成本，拖动/缩放/背景变化全程满帧，
            // 自研抓屏管线（BitBlt + CPU 模糊 + PNG 编码）拖动必卡，已退场。
            // 之所以不走 XAML 的 DesktopAcrylicBackdrop：2.x 里它只剩默认外观（浅色下偏实的
            // 底面亚克力），可定制的控制器 API 被移除了；DWM 直通取的是弹层亚克力，最透。
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var dark = root.ActualTheme == ElementTheme.Dark ? 1 : 0;
            _ = NativeMethods.DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
            var backdrop = DwmsbtTransientWindow;
            if (NativeMethods.DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int)) != 0)
            {
                // 22H2 以下没有系统材质属性：退回 XAML Mica（21H2 可用，Win10 无效果但不报错）
                backdrop = DwmsbtMainWindow;
                if (NativeMethods.DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int)) != 0)
                {
                    window.SystemBackdrop = new MicaBackdrop();
                }
            }

            root.Background = new SolidColorBrush(Colors.Transparent);
        }
        catch (Exception ex)
        {
            Log.Info("窗口背景材质不可用：" + ex.Message);
        }
    }
}
