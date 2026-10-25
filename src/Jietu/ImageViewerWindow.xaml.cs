using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace Jietu;

/// <summary>
/// 全尺寸截图查看器：AI 视觉窗口左栏预览放不下的大图，点击后在这里看。
/// 滚轮以光标为锚点缩放，按住拖拽平移，双击在「适应窗口 / 1:1」间切换，Esc 关闭。
/// </summary>
public sealed partial class ImageViewerWindow : Window
{
    private const double MinZoom = 0.05;
    private const double MaxZoom = 8;
    private const double ZoomStep = 1.15;

    private double _imgWidth;
    private double _imgHeight;
    private double _zoom = 1;
    private bool _fitMode = true;
    private bool _positioned;

    private bool _panning;
    private Windows.Foundation.Point _panLast;

    public ImageViewerWindow()
    {
        InitializeComponent();
        AppIcon.Apply(this);
        Root.SizeChanged += (_, _) =>
        {
            // 适应模式下窗口尺寸变化（含首次布局）时重新适配
            if (_fitMode)
            {
                SetFit();
            }
        };
    }

    /// <summary>显示/更新图片。首次调用时把窗口定位到光标所在屏幕并按图幅取尺寸。</summary>
    public async void ShowImage(byte[] png, bool activate = true)
    {
        try
        {
            var image = await ImageBytes.PngToBitmapAsync(png);
            _imgWidth = image.PixelWidth;
            _imgHeight = image.PixelHeight;
            View.Source = image;
            Title = $"截图查看 · {_imgWidth:N0} × {_imgHeight:N0}";
            if (!_positioned)
            {
                _positioned = true;
                PositionWindow();
            }

            SetFit();
            if (activate)
            {
                Activate();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    }

    // ── 窗口定位：光标所在屏幕的工作区，窗口随图幅收缩但不铺满 ─────────

    private void PositionWindow()
    {
        NativeMethods.GetCursorPos(out var cursor);
        var area = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest);
        var work = area.WorkArea;
        var width = (int)Math.Clamp(Math.Round(_imgWidth * 1.06) + 24, 560, work.Width * 78 / 100);
        var height = (int)Math.Clamp(Math.Round(_imgHeight * 1.06) + 72, 420, work.Height * 80 / 100);
        AppWindow.MoveAndResize(new RectInt32(
            work.X + (work.Width - width) / 2,
            work.Y + (work.Height - height) / 2,
            width,
            height));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 420;
            presenter.PreferredMinimumHeight = 320;
        }
    }

    // ── 缩放 ──────────────────────────────────────────────────────────

    /// <summary>缩放到 next；锚点（视口坐标）指向的图像像素缩放前后保持不动。</summary>
    private void ZoomTo(double next, double? anchorX = null, double? anchorY = null)
    {
        if (_imgWidth < 1 || _imgHeight < 1)
        {
            return;
        }

        next = Math.Clamp(next, MinZoom, MaxZoom);
        if (Math.Abs(next - _zoom) < 0.0005)
        {
            return;
        }

        var relX = anchorX ?? Canvas.ActualWidth / 2;
        var relY = anchorY ?? Canvas.ActualHeight / 2;
        var imgX = (Canvas.HorizontalOffset + relX) / _zoom;
        var imgY = (Canvas.VerticalOffset + relY) / _zoom;

        _zoom = next;
        _fitMode = false;
        ApplySize();
        Canvas.ChangeView(ClampOffset(imgX * _zoom - relX, Canvas.ScrollableWidth), ClampOffset(imgY * _zoom - relY, Canvas.ScrollableHeight), null, true);
        UpdateZoomText();
    }

    private void SetFit()
    {
        if (_imgWidth < 1 || _imgHeight < 1 || Canvas.ActualWidth < 1 || Canvas.ActualHeight < 1)
        {
            return;
        }

        _fitMode = true;
        var fit = Math.Min(Canvas.ActualWidth / _imgWidth, Canvas.ActualHeight / _imgHeight);
        _zoom = Math.Clamp(fit, MinZoom, MaxZoom);
        ApplySize();
        Canvas.ChangeView(Canvas.ScrollableWidth / 2, Canvas.ScrollableHeight / 2, null, true);
        UpdateZoomText();
    }

    private void ApplySize()
    {
        View.Width = _imgWidth * _zoom;
        View.Height = _imgHeight * _zoom;
        Canvas.UpdateLayout();
    }

    private static double ClampOffset(double value, double max)
        => Math.Clamp(value, 0, Math.Max(0, max));

    private void UpdateZoomText()
        => ZoomText.Text = $"{(int)Math.Round(_zoom * 100)}%";

    private bool IsAtFit()
        => _fitMode || (_imgWidth > 0 && Math.Abs(_zoom - Math.Min(Canvas.ActualWidth / _imgWidth, Canvas.ActualHeight / _imgHeight)) < 0.01);

    // ── 交互 ─────────────────────────────────────────────────────────

    private void OnCanvasWheel(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Canvas);
        var delta = point.Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        var position = point.Position;
        ZoomTo(_zoom * (delta > 0 ? ZoomStep : 1 / ZoomStep), position.X, position.Y);
        e.Handled = true;
    }

    private void OnViewPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(View).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _panning = true;
        _panLast = e.GetCurrentPoint(Canvas).Position;
        View.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnViewPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_panning)
        {
            return;
        }

        var position = e.GetCurrentPoint(Canvas).Position;
        Canvas.ChangeView(
            ClampOffset(Canvas.HorizontalOffset - (position.X - _panLast.X), Canvas.ScrollableWidth),
            ClampOffset(Canvas.VerticalOffset - (position.Y - _panLast.Y), Canvas.ScrollableHeight),
            null,
            true);
        _panLast = position;
        e.Handled = true;
    }

    private void OnViewPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_panning)
        {
            return;
        }

        _panning = false;
        View.ReleasePointerCaptures();
        e.Handled = true;
    }

    private void OnViewDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (IsAtFit())
        {
            ZoomTo(1);
        }
        else
        {
            SetFit();
        }
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => ZoomTo(_zoom * ZoomStep);

    private void OnZoomOut(object sender, RoutedEventArgs e) => ZoomTo(_zoom / ZoomStep);

    private void OnFitClick(object sender, RoutedEventArgs e) => SetFit();

    private void OnActualClick(object sender, RoutedEventArgs e) => ZoomTo(1);

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        Close();
        args.Handled = true;
    }
}
