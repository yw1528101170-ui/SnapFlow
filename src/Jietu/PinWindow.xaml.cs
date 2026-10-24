using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.System;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Jietu;

public sealed partial class PinWindow : Window
{
    private const int MinEdge = 48;

    private const string ScaleXPath = "(UIElement.RenderTransform).(CompositeTransform.ScaleX)";
    private const string ScaleYPath = "(UIElement.RenderTransform).(CompositeTransform.ScaleY)";
    private const string TranslateYPath = "(UIElement.RenderTransform).(CompositeTransform.TranslateY)";

    private string? _path;
    private double _aspect = 1;
    private int _baseWidth = 1;
    private int _baseHeight = 1;
    private int _lastWidth;
    private int _lastHeight;
    private double _zoom = 1;
    private double _opacity = 1.0;
    private double _savedOpacity = 1.0;
    private bool _fitting;
    private bool _animating;
    private bool _drag;
    private bool _note;
    private int _originX;
    private int _originY;
    private int _windowX;
    private int _windowY;
    private long _lastClickTicks;
    private int _lastClickX;
    private int _lastClickY;
    private bool _barVisible;

    private DispatcherQueueTimer? _rectTimer;
    private DispatcherQueueTimer? _hudTimer;
    private Storyboard? _ghostBreath;
    private int _hudToken;
    private int _barToken;

    public PinWindow()
    {
        InitializeComponent();
        AppIcon.Apply(this);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
            presenter.IsResizable = true;
        }

        ExtendsContentIntoTitleBar = true;
        try { AppWindow.IsShownInSwitchers = true; } catch { }
        AppWindow.Changed += OnWindowChanged;
        ApplyRoundedCorners();

        Closed += (_, _) =>
        {
            _rectTimer?.Stop();
            _hudTimer?.Stop();
            _ghostBreath?.Stop();
            Dismissed?.Invoke(this);
        };
    }

    public event Action<PinWindow>? Dismissed;

    public event Action<string>? CopyRequested;
    public event Action<string>? CopyTextRequested;
    public event Action<byte[]>? AiVisionRequested;

    private bool _isGhost;
    public bool IsGhost => _isGhost;

    public void OnToggleGhost(object sender, RoutedEventArgs e) => ToggleGhost();

    public void ToggleGhost() => SetGhost(!_isGhost);

    /// <summary>应用 Windows 11 圆角（DWMWCP_ROUND），让贴图与便签呈现 Apple 式圆角视窗。</summary>
    private void ApplyRoundedCorners()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var pref = NativeMethods.DwmwcpRound;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DwmwaWindowCornerPreference, ref pref, sizeof(int));
        }
        catch
        {
            // 旧系统忽略
        }
    }

    public void SetOpacity(double opacity)
    {
        _opacity = Math.Clamp(opacity, 0.15, 1.0);
        ApplyWindowOpacity();
    }

    private void ApplyWindowOpacity()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle);

        byte bAlpha = (byte)Math.Clamp((int)Math.Round(_opacity * 255), 38, 255);
        bool needLayered = _isGhost || bAlpha < 255;
        nint newStyle = exStyle;

        if (needLayered)
        {
            newStyle |= (nint)NativeMethods.WsExLayered;
        }
        else
        {
            newStyle &= ~(nint)NativeMethods.WsExLayered;
        }

        if (_isGhost)
        {
            newStyle |= (nint)NativeMethods.WsExTransparent;
        }
        else
        {
            newStyle &= ~(nint)NativeMethods.WsExTransparent;
        }

        if (newStyle != exStyle)
        {
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GwlExStyle, newStyle);
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate | NativeMethods.SwpFrameChanged);
        }

        if (needLayered)
        {
            NativeMethods.SetLayeredWindowAttributes(hwnd, 0, bAlpha, NativeMethods.LwaAlpha);
        }

        Root.Opacity = 1.0;
        UpdateStatusText();
    }

    public void SetGhost(bool ghost)
    {
        _isGhost = ghost;

        if (ghost)
        {
            _savedOpacity = _opacity;
            if (_opacity > 0.8)
            {
                _opacity = 0.65;
            }

            HideHoverBar(false);
            _barVisible = false;
            SetGrabber(false, false);
            GhostGlow.Visibility = Visibility.Visible;
            GhostIndicator.Visibility = Visibility.Visible;
            Fade(GhostIndicator, 0, 1, 220);
            StartGhostBreath();
            ShowHud("已进入穿透模式 · 托盘可恢复");
        }
        else
        {
            _opacity = _savedOpacity;
            _ghostBreath?.Stop();
            GhostGlow.Visibility = Visibility.Collapsed;
            GhostGlow.Opacity = 0.7;
            GhostDot.Opacity = 1;
            Fade(GhostIndicator, GhostIndicator.Opacity, 0, 160, () => GhostIndicator.Visibility = Visibility.Collapsed);
            ShowHud("已恢复鼠标交互");
        }

        ApplyWindowOpacity();
        UpdateBarLayout();
    }

    public void ShowText(string text)
    {
        _note = true;
        _path = null;
        Picture.Visibility = Visibility.Collapsed;
        NoteContainer.Visibility = Visibility.Visible;
        NoteTextBox.Text = text;
        UpdateNoteMeta();

        EdgeLine.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x24, 0x6B, 0x53, 0x1F));
        EdgeLine.Opacity = 1;
        EdgeGloss.Visibility = Visibility.Collapsed;

        // 便签不提供图像相关工具
        OcrBtn.Visibility = Visibility.Collapsed;
        AiBtn.Visibility = Visibility.Collapsed;
        PickColorBtn.Visibility = Visibility.Collapsed;

        var area = ScreenUtil.CursorWorkArea();
        var width = 340;
        var height = 240;
        _baseWidth = width;
        _baseHeight = height;
        _aspect = width / (double)height;
        _zoom = 1;

        NativeMethods.GetCursorPos(out var cursor);
        var x = Math.Clamp(cursor.X - 170, area.X, area.X + area.Width - width);
        var y = Math.Clamp(cursor.Y - 120, area.Y, area.Y + area.Height - height);
        Fit(x, y, width, height);
        UpdateBarLayout();
        UpdateStatusText();
        Activate();
        PlayAppear();
        NoteTextBox.Focus(FocusState.Programmatic);
    }

    public bool UsesFile(string path) =>
        !string.IsNullOrEmpty(_path) && string.Equals(_path, path, StringComparison.OrdinalIgnoreCase);

    public void ShowFile(string path)
    {
        _note = false;
        _path = path;
        var image = new BitmapImage();
        // 先把文件字节读入内存流再解码：FileStream 会在 using 结束时释放，
        // 而 SetSource 的解码可能尚未完成，流先没了会出现偶发空白图。
        // 内存流不主动 Dispose，由 BitmapImage 持有的包装流保持其存活。
        var source = new MemoryStream(File.ReadAllBytes(path));
        image.SetSource(source.AsRandomAccessStream());

        Picture.Source = image;
        var pixelWidth = Math.Max(1, image.PixelWidth);
        var pixelHeight = Math.Max(1, image.PixelHeight);
        _aspect = pixelWidth / (double)pixelHeight;
        var area = ScreenUtil.CursorWorkArea();
        var maxWidth = Math.Max(160, (int)(area.Width * 0.86));
        var maxHeight = Math.Max(120, (int)(area.Height * 0.86));
        var width = pixelWidth;
        var height = pixelHeight;
        if (width > maxWidth || height > maxHeight)
        {
            var scale = Math.Min(maxWidth / (double)width, maxHeight / (double)height);
            width = Math.Max(1, (int)Math.Round(width * scale));
            height = Math.Max(1, (int)Math.Round(height * scale));
        }

        _baseWidth = width;
        _baseHeight = height;
        _zoom = 1;
        var x = area.X + Math.Max(0, (area.Width - width) / 2);
        var y = area.Y + Math.Max(0, (area.Height - height) / 2);
        Fit(x, y, width, height);
        UpdateBarLayout();
        UpdateStatusText();
        Activate();
        PlayAppear();
        Root.Focus(FocusState.Programmatic);
    }

    // ===== 入场：Quick Look 式轻微回弹落定 =====

    private void PlayAppear()
    {
        Stage.Opacity = 0;
        StageTr.ScaleX = 1.05;
        StageTr.ScaleY = 1.05;

        Fade(Stage, 0, 1, 190);
        Settle(ScaleXPath);
        Settle(ScaleYPath);

        // 兜底：动画若因合成器异常未推进，直接落在最终状态，避免出现「空白窗口」
        var queue = DispatcherQueue.GetForCurrentThread();
        if (queue is null)
        {
            Stage.Opacity = 1;
            StageTr.ScaleX = 1;
            StageTr.ScaleY = 1;
            return;
        }

        var guard = queue.CreateTimer();
        guard.Interval = TimeSpan.FromMilliseconds(700);
        guard.Tick += (t, _) =>
        {
            t.Stop();
            if (Stage.Opacity < 0.9)
            {
                Stage.Opacity = 1;
                StageTr.ScaleX = 1;
                StageTr.ScaleY = 1;
            }
        };
        guard.Start();
    }

    private void Settle(string path)
    {
        Anim(Stage, path, 1.05, 0.996, 210, EaseOut(), () =>
            Anim(Stage, path, 0.996, 1, 150, EaseOut()));
    }

    // ===== 指针进出：工具条 / 抓握条 / 边缘高光 =====

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        AnimateEdge(1, 0.92);
        SetGrabber(true, true);
        ShowHoverBar(true);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_drag)
        {
            return;
        }

        AnimateEdge(0.75, 0.62);
        SetGrabber(false, true);
        HideHoverBar(true);
    }

    private void AnimateEdge(double line, double gloss)
    {
        if (EdgeGloss.Visibility != Visibility.Visible)
        {
            EdgeLine.Opacity = line;
            return;
        }

        Fade(EdgeLine, EdgeLine.Opacity, line, 200);
        Fade(EdgeGloss, EdgeGloss.Opacity, gloss, 200);
    }

    private void SetGrabber(bool show, bool animate)
    {
        if (Grabber.Visibility != Visibility.Visible && !show)
        {
            return;
        }

        var target = show ? 1 : 0;
        if (!animate)
        {
            Grabber.Opacity = target;
            return;
        }

        Fade(Grabber, Grabber.Opacity, target, 200);
    }

    private void ShowHoverBar(bool animate)
    {
        if (_isGhost)
        {
            return;
        }

        _barVisible = true;
        _barToken++;
        UpdateBarLayout();

        var appeared = HoverBar.Visibility != Visibility.Visible;
        HoverBar.Visibility = Visibility.Visible;

        if (!animate)
        {
            HoverBar.Opacity = 1;
            BarTr.ScaleX = 1;
            BarTr.ScaleY = 1;
            BarTr.TranslateY = 0;
            return;
        }

        if (appeared)
        {
            HoverBar.Opacity = 0;
            BarTr.ScaleX = 0.93;
            BarTr.ScaleY = 0.93;
            BarTr.TranslateY = -5;
        }

        Fade(HoverBar, HoverBar.Opacity, 1, 130);
        Anim(HoverBar, ScaleXPath, BarTr.ScaleX, 1, 300, Spring(0.34));
        Anim(HoverBar, ScaleYPath, BarTr.ScaleY, 1, 300, Spring(0.34));
        Anim(HoverBar, TranslateYPath, BarTr.TranslateY, 0, 260, EaseOut());
    }

    private void HideHoverBar(bool animate)
    {
        _barVisible = false;
        if (HoverBar.Visibility != Visibility.Visible)
        {
            return;
        }

        if (!animate)
        {
            HoverBar.Visibility = Visibility.Collapsed;
            HoverBar.Opacity = 0;
            return;
        }

        var token = ++_barToken;
        Fade(HoverBar, HoverBar.Opacity, 0, 130, () =>
        {
            if (token == _barToken && !_barVisible)
            {
                HoverBar.Visibility = Visibility.Collapsed;
            }
        });
        Anim(HoverBar, ScaleXPath, BarTr.ScaleX, 0.96, 150, EaseIn());
        Anim(HoverBar, ScaleYPath, BarTr.ScaleY, 0.96, 150, EaseIn());
        Anim(HoverBar, TranslateYPath, BarTr.TranslateY, -3, 150, EaseIn());
    }

    private void UpdateStatusText()
    {
        var zoomPct = (int)Math.Round(_zoom * 100);
        var opacityPct = (int)Math.Round(_opacity * 100);
        StatusText.Text = opacityPct < 100 ? $"{zoomPct}% · {opacityPct}%" : $"{zoomPct}%";
    }

    /// <summary>浮层 HUD（Apple 式操作反馈）。</summary>
    private void ShowFeedback(string message) => ShowHud(message);

    private void ShowHud(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        _hudToken++;
        HudText.Text = message;

        var appearing = Hud.Visibility != Visibility.Visible || Hud.Opacity < 0.3;
        Hud.Visibility = Visibility.Visible;
        if (appearing)
        {
            Hud.Opacity = 0;
            HudTr.ScaleX = 0.9;
            HudTr.ScaleY = 0.9;
            Fade(Hud, 0, 1, 120);
            Anim(Hud, ScaleXPath, 0.9, 1, 300, Spring(0.4));
            Anim(Hud, ScaleYPath, 0.9, 1, 300, Spring(0.4));
        }
        else
        {
            Fade(Hud, Hud.Opacity, 1, 90);
        }

        var queue = DispatcherQueue.GetForCurrentThread();
        if (queue is null)
        {
            return;
        }

        _hudTimer ??= queue.CreateTimer();
        _hudTimer.Stop();
        _hudTimer.Interval = TimeSpan.FromMilliseconds(1250);
        _hudTimer.Tick -= OnHudTick;
        _hudTimer.Tick += OnHudTick;
        _hudTimer.Start();
    }

    private void OnHudTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        var token = _hudToken;
        Fade(Hud, Hud.Opacity, 0, 220, () =>
        {
            if (token == _hudToken)
            {
                Hud.Visibility = Visibility.Collapsed;
            }
        });
        Anim(Hud, ScaleXPath, HudTr.ScaleX, 0.97, 220, EaseIn());
        Anim(Hud, ScaleYPath, HudTr.ScaleY, 0.97, 220, EaseIn());
    }

    private void UpdateHoverBarPosition()
    {
        var pos = AppWindow.Position;
        if (pos.Y < 50)
        {
            HoverBar.VerticalAlignment = VerticalAlignment.Bottom;
            HoverBar.HorizontalAlignment = HorizontalAlignment.Center;
            HoverBar.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.9);
        }
        else
        {
            HoverBar.VerticalAlignment = VerticalAlignment.Top;
            HoverBar.HorizontalAlignment = HorizontalAlignment.Right;
            HoverBar.RenderTransformOrigin = new Windows.Foundation.Point(0.85, 0.2);
        }

        UpdateBarLayout();
    }

    /// <summary>窗口尺寸很小时收敛工具条内容，避免溢出。</summary>
    private void UpdateBarLayout()
    {
        var size = AppWindow.Size;
        var compact = size.Width < 340;
        var mini = size.Width < 210;

        ToolGroup.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ToolDivider.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        StatusBadge.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        Grabber.Visibility = size.Height < 74 || _isGhost ? Visibility.Collapsed : Visibility.Visible;
        GhostBtn.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateNoteMeta()
    {
        var count = NoteTextBox.Text?.Length ?? 0;
        NoteMeta.Text = count == 0 ? "空白" : $"{count} 字";
    }

    // ===== 便签文字变化 =====

    private void OnNoteTextChanged(object sender, TextChangedEventArgs e) => UpdateNoteMeta();

    private async void OnPickColor(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_path) || !File.Exists(_path))
        {
            return;
        }

        try
        {
            NativeMethods.GetCursorPos(out var cursor);
            var pos = AppWindow.Position;
            var size = AppWindow.Size;
            if (size.Width <= 0 || size.Height <= 0)
            {
                return;
            }

            var relX = Math.Clamp((cursor.X - pos.X) / (double)size.Width, 0.0, 1.0);
            var relY = Math.Clamp((cursor.Y - pos.Y) / (double)size.Height, 0.0, 1.0);

            var png = await File.ReadAllBytesAsync(_path);
            using var bitmap = await ImageBytes.FromPngAsync(png);
            var pixels = ImageBytes.ReadBgra(bitmap);
            var pw = bitmap.PixelWidth;
            var ph = bitmap.PixelHeight;
            if (pw <= 0 || ph <= 0 || pixels.Length < pw * ph * 4)
            {
                return;
            }

            var px = Math.Clamp((int)(relX * pw), 0, pw - 1);
            var py = Math.Clamp((int)(relY * ph), 0, ph - 1);
            var idx = ((py * pw) + px) * 4;
            var b = pixels[idx];
            var g = pixels[idx + 1];
            var r = pixels[idx + 2];

            var isShift = (NativeMethods.GetKeyState(0x10) & 0x8000) != 0;
            var code = isShift ? $"rgb({r}, {g}, {b})" : $"#{r:X2}{g:X2}{b:X2}";
            CopyTextRequested?.Invoke(code);
            ShowFeedback($"已拾色 {code}");
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            ShowFeedback("取色异常");
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        Copy();
        ShowFeedback(_note ? "已复制便签文字" : "已复制图片");
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => ZoomStep(1.15);

    private void OnZoomOut(object sender, RoutedEventArgs e) => ZoomStep(1 / 1.15);

    private async void OnSaveAs(object sender, RoutedEventArgs e) => await SaveAsAsync();

    private async void OnAiVision(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_path) || !File.Exists(_path))
        {
            return;
        }

        try
        {
            var png = await File.ReadAllBytesAsync(_path);
            AiVisionRequested?.Invoke(png);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    }

    private async void OnOcr(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_path) || !File.Exists(_path))
        {
            return;
        }

        ShowFeedback("正在识别文字…");
        try
        {
            var png = await File.ReadAllBytesAsync(_path);
            var text = await OcrService.ReadAsync(png);
            if (!string.IsNullOrWhiteSpace(text))
            {
                CopyTextRequested?.Invoke(text);
                ShowFeedback("已复制文字");
            }
            else
            {
                ShowFeedback("未识别到文字");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            ShowFeedback("识别异常");
        }
    }

    private async Task SaveAsAsync()
    {
        if (NoteContainer.Visibility == Visibility.Visible)
        {
            try
            {
                var picker = new FileSavePicker
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                    SuggestedFileName = "SnapFlow-Note-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
                };
                picker.FileTypeChoices.Add("文本文件", [".txt"]);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                var file = await picker.PickSaveFileAsync();
                if (file != null)
                {
                    await File.WriteAllTextAsync(file.Path, NoteTextBox.Text);
                    ShowFeedback("已保存");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }

            return;
        }
        if (string.IsNullOrEmpty(_path) || !File.Exists(_path))
        {
            return;
        }

        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                SuggestedFileName = "SnapFlow-Pin-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
            };
            picker.FileTypeChoices.Add("PNG 图片", [".png"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file == null)
            {
                return;
            }

            var bytes = await File.ReadAllBytesAsync(_path);
            await File.WriteAllBytesAsync(file.Path, bytes);
            ShowFeedback("已保存到 " + Path.GetFileName(file.Path));
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        _opacity = 1.0;
        ApplyWindowOpacity();
        OnResetInternal(sender, e);
    }

    private void OnResetInternal(object sender, RoutedEventArgs e)
    {
        Root.Opacity = 1.0;
        if (_baseWidth > 0 && _baseHeight > 0)
        {
            _zoom = 1.0;
            var pos = AppWindow.Position;
            var size = AppWindow.Size;
            var x = pos.X + ((size.Width - _baseWidth) / 2);
            var y = pos.Y + ((size.Height - _baseHeight) / 2);
            AnimateRect(x, y, _baseWidth, _baseHeight, 260);
        }

        UpdateStatusText();
        ShowFeedback("已重置");
    }

    private void ZoomStep(double factor)
    {
        if (_baseWidth < 1 || _baseHeight < 1)
        {
            return;
        }

        var next = Math.Clamp(_zoom * factor, 0.2, 8);
        if (Math.Abs(next - _zoom) < 0.001)
        {
            return;
        }

        var pos = AppWindow.Position;
        var size = AppWindow.Size;
        var width = Math.Max(MinEdge, (int)Math.Round(_baseWidth * next));
        var height = Math.Max(MinEdge, (int)Math.Round(width / _aspect));
        _zoom = next;
        AnimateRect(
            pos.X + ((size.Width - width) / 2),
            pos.Y + ((size.Height - height) / 2),
            width,
            height,
            190);
        UpdateStatusText();
        ShowFeedback($"{(int)Math.Round(_zoom * 100)}%");
    }

    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        var isCtrl = (NativeMethods.GetKeyState(0x11) & 0x8000) != 0;
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key is VirtualKey.F3 or VirtualKey.G && NoteTextBox.FocusState == FocusState.Unfocused)
        {
            e.Handled = true;
            ToggleGhost();
        }
        else if (e.Key == VirtualKey.C && !isCtrl && NoteTextBox.FocusState == FocusState.Unfocused)
        {
            e.Handled = true;
            OnPickColor(this, e);
        }
        else if (isCtrl && e.Key == VirtualKey.C)
        {
            e.Handled = true;
            Copy();
            ShowFeedback(_note ? "已复制便签文字" : "已复制图片");
        }
        else if (isCtrl && e.Key == VirtualKey.S)
        {
            e.Handled = true;
            _ = SaveAsAsync();
        }
        else if (isCtrl && (e.Key is VirtualKey.Up or VirtualKey.Add or (VirtualKey)187))
        {
            e.Handled = true;
            SetOpacity(_opacity + 0.08);
        }
        else if (isCtrl && (e.Key is VirtualKey.Down or VirtualKey.Subtract or (VirtualKey)189))
        {
            e.Handled = true;
            SetOpacity(_opacity - 0.08);
        }
        else if (e.Key is VirtualKey.Add or (VirtualKey)187)
        {
            e.Handled = true;
            ZoomStep(1.1);
        }
        else if (e.Key is VirtualKey.Subtract or (VirtualKey)189)
        {
            e.Handled = true;
            ZoomStep(1 / 1.1);
        }
        else if (e.Key is VirtualKey.Number0 or VirtualKey.NumberPad0)
        {
            e.Handled = true;
            OnReset(this, e);
        }
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root);
        if (!point.Properties.IsLeftButtonPressed || e.OriginalSource is Microsoft.UI.Xaml.Controls.Button)
        {
            return;
        }

        NativeMethods.GetCursorPos(out var cursor);
        var now = Environment.TickCount64;
        if (now - _lastClickTicks is > 0 and < 400
            && Math.Abs(cursor.X - _lastClickX) < 8
            && Math.Abs(cursor.Y - _lastClickY) < 8)
        {
            _drag = false;
            _lastClickTicks = 0;
            Root.ReleasePointerCaptures();
            Copy();
            ShowFeedback(_note ? "已复制便签文字" : "已复制图片");
            e.Handled = true;
            return;
        }

        _lastClickTicks = now;
        _lastClickX = cursor.X;
        _lastClickY = cursor.Y;
        var position = AppWindow.Position;
        _drag = true;
        _originX = cursor.X;
        _originY = cursor.Y;
        _windowX = position.X;
        _windowY = position.Y;
        Root.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_drag)
        {
            return;
        }

        NativeMethods.GetCursorPos(out var cursor);
        AppWindow.Move(new PointInt32(_windowX + cursor.X - _originX, _windowY + cursor.Y - _originY));
        UpdateHoverBarPosition();
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_drag)
        {
            return;
        }

        _drag = false;
        Root.ReleasePointerCaptures();

        NativeMethods.GetCursorPos(out var cursor);
        var pos = AppWindow.Position;
        var size = AppWindow.Size;
        var inside = cursor.X >= pos.X && cursor.X < pos.X + size.Width
                     && cursor.Y >= pos.Y && cursor.Y < pos.Y + size.Height;
        if (inside)
        {
            ShowHoverBar(true);
        }
        else
        {
            HideHoverBar(true);
        }
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(Root).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        var isCtrl = (NativeMethods.GetKeyState(0x11) & 0x8000) != 0;
        var isAlt = (NativeMethods.GetKeyState(0x12) & 0x8000) != 0;
        var isShift = (NativeMethods.GetKeyState(0x10) & 0x8000) != 0;
        var isBadge = e.OriginalSource is DependencyObject dep && (dep == StatusBadge || dep == StatusText);
        if (isCtrl || isAlt || isShift || isBadge)
        {
            var step = delta > 0 ? 0.08 : -0.08;
            SetOpacity(_opacity + step);
            ShowFeedback($"不透明度 {(int)Math.Round(_opacity * 100)}%");
            e.Handled = true;
            return;
        }

        if (_baseWidth < 1 || _baseHeight < 1)
        {
            return;
        }

        var next = Math.Clamp(_zoom * (delta > 0 ? 1.1 : 1 / 1.1), 0.2, 8);
        if (Math.Abs(next - _zoom) < 0.001)
        {
            return;
        }

        NativeMethods.GetCursorPos(out var cursor);
        var position = AppWindow.Position;
        var size = AppWindow.Size;
        var width = Math.Max(MinEdge, (int)Math.Round(_baseWidth * next));
        var height = Math.Max(MinEdge, (int)Math.Round(width / _aspect));
        var anchorX = size.Width <= 0 ? 0.5 : (cursor.X - position.X) / (double)size.Width;
        var anchorY = size.Height <= 0 ? 0.5 : (cursor.Y - position.Y) / (double)size.Height;
        anchorX = Math.Clamp(anchorX, 0, 1);
        anchorY = Math.Clamp(anchorY, 0, 1);
        _zoom = next;
        AnimateRect(
            (int)Math.Round(cursor.X - (anchorX * width)),
            (int)Math.Round(cursor.Y - (anchorY * height)),
            width,
            height,
            150);
        UpdateStatusText();
        ShowFeedback($"{(int)Math.Round(_zoom * 100)}%");
        e.Handled = true;
    }

    private void OnWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange)
        {
            UpdateHoverBarPosition();
        }

        if (_animating || _fitting || _aspect <= 0 || !args.DidSizeChange)
        {
            return;
        }

        var size = sender.Size;
        var widthDelta = Math.Abs(size.Width - _lastWidth);
        var heightDelta = Math.Abs(size.Height - _lastHeight);
        if (widthDelta == 0 && heightDelta == 0)
        {
            return;
        }

        int width;
        int height;
        if (widthDelta >= heightDelta)
        {
            width = Math.Max(MinEdge, size.Width);
            height = Math.Max(MinEdge, (int)Math.Round(width / _aspect));
        }
        else
        {
            height = Math.Max(MinEdge, size.Height);
            width = Math.Max(MinEdge, (int)Math.Round(height * _aspect));
        }

        if (Math.Abs(width - size.Width) <= 1 && Math.Abs(height - size.Height) <= 1)
        {
            _lastWidth = size.Width;
            _lastHeight = size.Height;
            UpdateBarLayout();
            return;
        }

        var position = sender.Position;
        Fit(position.X, position.Y, width, height);
        if (_baseWidth > 0)
        {
            _zoom = width / (double)_baseWidth;
            UpdateStatusText();
        }
    }

    private void Fit(int x, int y, int width, int height)
    {
        _fitting = true;
        _lastWidth = width;
        _lastHeight = height;
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        _fitting = false;
        UpdateHoverBarPosition();
    }

    /// <summary>以缓动曲线平滑改变窗口矩形（Apple 式缩放过渡）。</summary>
    private void AnimateRect(int x, int y, int width, int height, int milliseconds)
    {
        _rectTimer?.Stop();
        var queue = DispatcherQueue.GetForCurrentThread();
        if (queue is null || milliseconds <= 0)
        {
            _animating = false;
            Fit(x, y, width, height);
            return;
        }

        var startPos = AppWindow.Position;
        var startSize = AppWindow.Size;
        var clock = Stopwatch.StartNew();
        _animating = true;

        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(15);
        timer.Tick += (t, _) =>
        {
            var p = Math.Clamp(clock.Elapsed.TotalMilliseconds / milliseconds, 0, 1);
            if (p >= 1)
            {
                t.Stop();
                _animating = false;
                Fit(x, y, width, height);
                return;
            }

            var e = 1 - Math.Pow(1 - p, 4);
            Fit(
                (int)Math.Round(startPos.X + ((x - startPos.X) * e)),
                (int)Math.Round(startPos.Y + ((y - startPos.Y) * e)),
                (int)Math.Round(startSize.Width + ((width - startSize.Width) * e)),
                (int)Math.Round(startSize.Height + ((height - startSize.Height) * e)));
        };

        _rectTimer = timer;
        timer.Start();
    }

    private void Copy()
    {
        if (NoteContainer.Visibility == Visibility.Visible)
        {
            CopyTextRequested?.Invoke(NoteTextBox.Text);
            return;
        }

        if (!string.IsNullOrEmpty(_path))
        {
            CopyRequested?.Invoke(_path);
        }
    }

    // ===== 动画基础件 =====

    /// <summary>Apple 风格的快出慢停曲线。</summary>
    private static EasingFunctionBase EaseOut() => new QuinticEase { EasingMode = EasingMode.EaseOut };

    /// <summary>收起/淡出使用的加速曲线。</summary>
    private static EasingFunctionBase EaseIn() => new CubicEase { EasingMode = EasingMode.EaseIn };

    private static BackEase Spring(double amplitude) => new() { EasingMode = EasingMode.EaseOut, Amplitude = amplitude };


    private static Storyboard Fade(UIElement target, double from, double to, int milliseconds, Action? completed = null)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        return Run(target, "Opacity", animation, completed);
    }

    private static Storyboard Anim(UIElement target, string path, double from, double to, int milliseconds,
        EasingFunctionBase? easing = null, Action? completed = null)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EasingFunction = easing,
        };

        return Run(target, path, animation, completed);
    }

    private static Storyboard Run(UIElement target, string path, DoubleAnimation animation, Action? completed)
    {
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, path);
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        if (completed != null)
        {
            storyboard.Completed += (_, _) => completed();
        }

        storyboard.Begin();
        return storyboard;
    }

    private void StartGhostBreath()
    {
        _ghostBreath?.Stop();
        var storyboard = new Storyboard();

        var glow = new DoubleAnimation
        {
            From = 0.4,
            To = 0.95,
            Duration = new Duration(TimeSpan.FromMilliseconds(1500)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(glow, GhostGlow);
        Storyboard.SetTargetProperty(glow, "Opacity");

        var dot = new DoubleAnimation
        {
            From = 0.3,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(1200)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(dot, GhostDot);
        Storyboard.SetTargetProperty(dot, "Opacity");

        storyboard.Children.Add(glow);
        storyboard.Children.Add(dot);
        _ghostBreath = storyboard;
        storyboard.Begin();
    }
}
