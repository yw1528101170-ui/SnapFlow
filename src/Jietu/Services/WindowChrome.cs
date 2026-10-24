using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Jietu;

internal static class WindowChrome
{
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
            window.SystemBackdrop = new MicaBackdrop();
            root.Background = new SolidColorBrush(Colors.Transparent);
        }
        catch (Exception ex)
        {
            Log.Info("窗口背景材质不可用：" + ex.Message);
        }
    }
}
