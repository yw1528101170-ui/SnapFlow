using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Jietu;

/// <summary>
/// WinUI 没有自带 WrapPanel：按可用宽度把子项逐行排列、放不下就换行，
/// 用于标签胶囊这类宽度不一、数量不定内容的自动换行布局。
/// </summary>
public sealed class WrapPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        double rowWidth = 0, rowHeight = 0, totalHeight = 0, widest = 0;
        foreach (var child in Children)
        {
            child.Measure(availableSize);
            var w = child.DesiredSize.Width;
            var h = child.DesiredSize.Height;
            widest = Math.Max(widest, w);
            if (rowWidth > 0 && rowWidth + w > availableSize.Width)
            {
                totalHeight += rowHeight;
                rowWidth = 0;
                rowHeight = 0;
            }

            rowWidth += w;
            rowHeight = Math.Max(rowHeight, h);
        }

        totalHeight += rowHeight;
        var width = double.IsInfinity(availableSize.Width) ? widest : Math.Min(widest, availableSize.Width);
        return new Size(width, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, rowHeight = 0;
        foreach (var child in Children)
        {
            var w = child.DesiredSize.Width;
            var h = child.DesiredSize.Height;
            if (x > 0 && x + w > finalSize.Width)
            {
                x = 0;
                y += rowHeight;
                rowHeight = 0;
            }

            child.Arrange(new Rect(x, y, w, h));
            x += w;
            rowHeight = Math.Max(rowHeight, h);
        }

        return finalSize;
    }
}
