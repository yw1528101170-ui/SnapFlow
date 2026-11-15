using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Jietu;

/// <summary>
/// ContentDialog 与 InfoBar 的玻璃面。
/// 框架模板内部的资源引用不回落到应用资源（App.xaml 覆盖框架键实测无效），
/// 所以这两类控件只能在打开后进视觉树，把写死的实底卡面换成玻璃薄面。
/// </summary>
internal static class GlassOverlays
{
    /// <summary>对话框卡面：与 Flyout 弹层同一套玻璃配色。</summary>
    public static void Glassify(this ContentDialog dialog)
    {
        dialog.Opened += (_, _) =>
        {
            try
            {
                if (FindNamed(dialog, "DialogSpace") is Border surface)
                {
                    var light = dialog.ActualTheme == ElementTheme.Light;
                    surface.Background = Fill(light);
                    surface.BorderBrush = Stroke(light);
                    surface.BorderThickness = new Thickness(1);
                    surface.CornerRadius = new CornerRadius(14);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        };
    }

    /// <summary>提示条卡面：保留框架的严重级别描边与图标，只把实底换玻璃。</summary>
    public static void Glassify(InfoBar bar)
    {
        // InfoBar 关闭状态下没有视觉树，等 IsOpen 翻 true、模板铺好后再进树换脸
        bar.RegisterPropertyChangedCallback(InfoBar.IsOpenProperty, (_, _) =>
        {
            if (!bar.IsOpen)
            {
                return;
            }

            _ = bar.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (FindNamed(bar, "ContentRoot") is Grid root)
                    {
                        root.Background = Fill(bar.ActualTheme == ElementTheme.Light);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex);
                }
            });
        });
    }

    private static SolidColorBrush Fill(bool light) => new(light
        ? Color.FromArgb(0xD9, 0xFB, 0xFC, 0xFF)
        : Color.FromArgb(0xEE, 0x1B, 0x20, 0x27));

    private static SolidColorBrush Stroke(bool light) => new(light
        ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)
        : Color.FromArgb(0x2E, 0xDC, 0xE6, 0xF2));

    private static FrameworkElement? FindNamed(FrameworkElement root, string name)
    {
        if (root.Name == name)
        {
            return root;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(root, i) is FrameworkElement child)
            {
                var hit = FindNamed(child, name);
                if (hit is not null)
                {
                    return hit;
                }
            }
        }

        return null;
    }
}
