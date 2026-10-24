using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;

namespace Jietu;

public sealed partial class OverlayWindow : Window
{
    private readonly List<AnnotateStroke> _strokes = [];
    private readonly List<AnnotateStroke> _redo = [];
    private readonly List<(int X, int Y)> _draft = [];
    private TaskCompletionSource<PickedShot?>? _pick;
    private byte[] _source = [];
    private int _loupePixelX = int.MinValue;
    private int _loupePixelY;
    private DisplayMonitor _monitor;
    private int _frameWidth;
    private int _frameHeight;
    private bool _dragging;
    private bool _pressing;
    private bool _dragMoved;
    private bool _locked;
    private bool _moving;
    private int _resize = -1;
    private double _selX;
    private double _selY;
    private double _selW;
    private double _selH;
    private Point _dragStart;
    private Point _lastClickPoint;
    private long _lastClickTicks;
    private (int X, int Y) _pixelStart;
    private PixelRect? _current;
    private AnnotateKind? _tool;
    private bool _allowClose;
    private bool _hiding;
    private TextBox? _textInput;
    private static readonly (byte R, byte G, byte B)[] Palette =
    [
        (45, 110, 250),
        (255, 214, 10),
        (16, 185, 129),
        (26, 26, 26),
        (255, 255, 255),
        (239, 68, 68),
    ];

    private const int LoupeSample = 31;
    private const int LoupeZoom = 4;
    private const int LoupePixels = LoupeSample * LoupeZoom;
    private const double CompactToolbarWidth = 800;
    private byte _blue = 68;
    private byte _green = 68;
    private byte _red = 239;
    private int _colorIndex = 5;
    private int _strokeDip = 3;
    private int _fontDip = 20;
    private bool _pinRequested;
    private bool _extractText;
    private bool _extractCode;
    private bool _longCapture;
    private bool _translateRequested;
    private bool _shareRequested;
    private bool _aiVisionRequested;
    private BarcodeResult? _detectedBarcode;
    private CancellationTokenSource? _barcodeCts;
    private string _selectedEmoji = "😊";
    private bool _toolSync;
    private int _elementLevel;
    private IntPtr _chainRoot;
    private ElementStack _chain;
    private int _hoverPixelX = int.MinValue;
    private int _hoverPixelY = int.MinValue;
    private object? _chromeSource;
    private bool _chromeHit;
    private readonly Border[] _handles = new Border[8];
    private readonly Border[] _swatches = new Border[Palette.Length];
    private readonly Border[] _swatchRings = new Border[Palette.Length];
    private readonly SolidColorBrush _glyphOffBrush = new(Color.FromArgb(242, 255, 255, 255));
    private readonly SolidColorBrush _glyphOnBrush = new(Microsoft.UI.Colors.White);
    private readonly byte[] _loupePixels = new byte[LoupePixels * LoupePixels * 4];
    private WriteableBitmap? _loupeBitmap;
    private readonly SolidColorBrush _loupeColorBrush = new(Color.FromArgb(255, 0, 0, 0));
    private Stream? _loupeStream;
    private long _mosaicStamp;

    public event Action<string>? CopyTextRequested;

    public OverlayWindow()
    {
        InitializeComponent();
        AppIcon.Apply(this);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
        }

        ExtendsContentIntoTitleBar = true;
        try { AppWindow.IsShownInSwitchers = false; } catch { }
        SystemBackdrop = null;
        AppWindow.Closing += (_, e) =>
        {
            if (_allowClose || _hiding)
            {
                return;
            }

            e.Cancel = true;
            Complete(null);
        };
        Root.PointerPressed += OnPointerPressed;
        Root.PointerMoved += OnPointerMoved;
        Root.PointerReleased += OnPointerReleased;
        Root.PointerWheelChanged += OnWheel;
        Root.PreviewKeyDown += OnKeyDown;
        SetCursor(Root, InputSystemCursor.Create(InputSystemCursorShape.Cross));
        SetCursor(Toolbar, InputSystemCursor.Create(InputSystemCursorShape.Arrow));
        for (var i = 0; i < _handles.Length; i++)
        {
            var handle = new Border
            {
                Width = 9,
                Height = 9,
                CornerRadius = new CornerRadius(4.5),
                Background = new SolidColorBrush(Microsoft.UI.Colors.White),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 10, 132, 255)),
                BorderThickness = new Thickness(1.6),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            _handles[i] = handle;
            Shade.Children.Add(handle);
        }

        _glyphOffBrush.Color = GlyphColor;
        _glyphOnBrush.Color = EmphasisColor;
        Root.ActualThemeChanged += (_, _) =>
        {
            _glyphOffBrush.Color = GlyphColor;
            _glyphOnBrush.Color = EmphasisColor;
            PaintTools();
            PaintSwatches();
            PaintWidths();
            PaintFonts();
        };
        BuildColors();
        _loupeBitmap = new WriteableBitmap(LoupePixels, LoupePixels);
        LoupeImage.Source = _loupeBitmap;
        LoupeColorBlock.Background = _loupeColorBrush;
        PaintTools();
        PaintSwatches();
        PaintWidths();
        PaintFonts();
        PaintHistory();
    }

    public void AllowClose() => _allowClose = true;

    public void CancelPick() => Complete(null);

    internal async Task<PickedShot?> PickAsync(SoftwareBitmap frame, byte[]? pixels, DisplayMonitor monitor)
    {
        try
        {
            Complete(null);
            _monitor = monitor;
            _frameWidth = frame.PixelWidth;
            _frameHeight = frame.PixelHeight;
            _source = pixels ?? ImageBytes.ReadBgra(frame);
            _loupePixelX = int.MinValue;
            _dragging = false;
            _pressing = false;
            _dragMoved = false;
            _locked = false;
            _moving = false;
            _resize = -1;
            _tool = null;
            _current = null;
            _lastClickTicks = 0;
            _strokes.Clear();
            _redo.Clear();
            _pinRequested = false;
            _extractText = false;
            _extractCode = false;
            _longCapture = false;
            _elementLevel = 0;
            _chainRoot = IntPtr.Zero;
            _chain = default;
            _hoverPixelX = int.MinValue;
            _hoverPixelY = int.MinValue;
            _chromeSource = null;
            _draft.Clear();
            SetHint(null);
            ResetLiveMosaic();
            Ink.Children.Clear();
            CloseText(false);
            Toolbar.Visibility = Visibility.Collapsed;
            BarcodeCard.Visibility = Visibility.Collapsed;
            _aiVisionRequested = false;
            _detectedBarcode = null;
            _barcodeCts?.Cancel();
            Loupe.Visibility = Visibility.Collapsed;
            PaintTools();
            PaintSwatches();
            PaintHistory();
            ClearSelection();
            try
            {
                Shot.Source = await ImageBytes.ToBitmapSourceAsync(frame);
            }
            catch (Exception ex)
            {
                Log.Info("直接显示截屏失败，改用图片编码：" + ex.Message);
                var png = await ImageBytes.ToPngAsync(frame);
                Shot.Source = await ImageBytes.PngToBitmapAsync(png);
            }
        }
        finally
        {
            frame.Dispose();
        }

        AppWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(monitor.X, monitor.Y, monitor.Width, monitor.Height));
        var pick = new TaskCompletionSource<PickedShot?>();
        _pick = pick;
        Activate();
        Root.Focus(FocusState.Programmatic);
        // WinUI 的 Activate 在“热键来自后台/前台锁未放开”时不一定能拿到前台（HistoryWindow 已有同样兜底），
        // 拿不到前台就会导致 Esc/Enter/Ctrl+Z 等遮罩快捷键打到别的程序。
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        NativeMethods.BringToForeground(hwnd);
        if (NativeMethods.GetForegroundWindow() != hwnd)
        {
            DispatcherQueue.TryEnqueue(() => NativeMethods.BringToForeground(hwnd));
        }
        AnimateIn(Hint, 12, 520, 0.96);
        DispatcherQueue.TryEnqueue(HighlightAtCursor);
        return await pick.Task;
    }

    private void HighlightAtCursor()
    {
        if (_pick == null || _locked || _pressing || Root.XamlRoot?.RasterizationScale is not > 0)
        {
            return;
        }

        if (!NativeMethods.GetCursorPos(out var point))
        {
            return;
        }

        var dip = new Point(DipX(point.X), DipY(point.Y));
        UpdateWindow(dip);
        MoveLoupe(dip);
    }

    private void LockFullScreen()
    {
        if (_pick == null)
        {
            return;
        }

        var width = Root.ActualWidth > 1 ? Root.ActualWidth : _monitor.Width / Scale;
        var height = Root.ActualHeight > 1 ? Root.ActualHeight : _monitor.Height / Scale;
        SetDipRect(0, 0, width, height);
        LockSelection();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (IsOnChrome(e.OriginalSource))
        {
            return;
        }

        var point = e.GetCurrentPoint(Root);
        if (point.Properties.IsRightButtonPressed)
        {
            Complete(null);
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (_textInput != null)
        {
            CloseText(true);
        }

        var dip = point.Position;
        if (_locked)
        {
            if (SpaceHeld() && _current != null)
            {
                _moving = true;
                _dragStart = dip;
                Root.CapturePointer(e.Pointer);
                e.Handled = true;
                return;
            }

            var handle = HitHandle(dip);
            if (handle >= 0)
            {
                _resize = handle;
                _dragStart = dip;
                Root.CapturePointer(e.Pointer);
                e.Handled = true;
                return;
            }

            if (InsideSelection(dip) && _tool == AnnotateKind.Number)
            {
                PlaceNumber(ToPixel(dip));
                e.Handled = true;
                return;
            }

            if (InsideSelection(dip) && _tool == AnnotateKind.Emoji)
            {
                PlaceEmoji(_selectedEmoji, ToPixel(dip));
                e.Handled = true;
                return;
            }

            if (InsideSelection(dip) && _tool != null)
            {
                _dragging = true;
                _pixelStart = ToPixel(_tool == AnnotateKind.Spotlight ? ClampToSelection(dip) : dip);
                _draft.Clear();
                _draft.Add(_pixelStart);
                _mosaicTruncated = false;
                if (_tool == AnnotateKind.Text)
                {
                    _dragging = false;
                    OpenText(_pixelStart);
                }
                else
                {
                    Root.CapturePointer(e.Pointer);
                }

                e.Handled = true;
                return;
            }

            if (InsideSelection(dip) && _tool == null && IsRepeatClick(dip))
            {
                e.Handled = true;
                Confirm();
                return;
            }

            if (InsideSelection(dip) && _tool == null)
            {
                _moving = true;
                _dragStart = dip;
                Root.CapturePointer(e.Pointer);
                e.Handled = true;
                return;
            }

            // 选区外按下只「解锁并进入准备新建选区」，先不清标注：
            // 旧实现按下 1px 就 _strokes.Clear() + _redo.Clear()，误点一下几十笔标注全丢且不可撤销。
            // 真正的清空推迟到指针确实开始拖出新选区（OnPointerMoved 里走 DiscardAnnotations）。
            _locked = false;
            Toolbar.Visibility = Visibility.Collapsed;
            BarcodeCard.Visibility = Visibility.Collapsed;
            _aiVisionRequested = false;
            _detectedBarcode = null;
            _barcodeCts?.Cancel();
            CloseText(false);
        }

        _pressing = true;
        _dragMoved = false;
        _dragging = false;
        _dragStart = dip;
        Root.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var dip = e.GetCurrentPoint(Root).Position;
        if (_resize >= 0)
        {
            ResizeTo(dip);
            MoveLoupe(dip);
            return;
        }

        if (_moving)
        {
            var dx = dip.X - _dragStart.X;
            var dy = dip.Y - _dragStart.Y;
            _dragStart = dip;
            SetDipRect(_selX + dx, _selY + dy, _selW, _selH);
            MoveLoupe(dip);
            return;
        }

        if (_dragging && _locked && _tool != null)
        {
            if (_tool == AnnotateKind.Mosaic && Environment.TickCount64 - _mosaicStamp < 32)
            {
                return;
            }

            if (_tool == AnnotateKind.Mosaic)
            {
                _mosaicStamp = Environment.TickCount64;
            }

            var liveDip = _tool == AnnotateKind.Spotlight ? ClampToSelection(dip) : dip;
            var pixel = ToPixel(liveDip);
            if (_tool is AnnotateKind.Pen or AnnotateKind.Mosaic or AnnotateKind.Highlighter && !(_tool is (AnnotateKind.Pen or AnnotateKind.Highlighter) && ShiftDown()))
            {
                var last = _draft[^1];
                if (Math.Abs(pixel.X - last.X) + Math.Abs(pixel.Y - last.Y) >= 2)
                {
                    _draft.Add(pixel);
                }
            }

            var start = DipFromPixel(_pixelStart.X, _pixelStart.Y);
            DrawTemp(start, ConstrainDip(start, liveDip));
            return;
        }

        if (_pressing && !_locked)
        {
            var travel = Math.Abs(dip.X - _dragStart.X) + Math.Abs(dip.Y - _dragStart.Y);
            if (travel >= 4)
            {
                if (!_dragging)
                {
                    // 指针确实拖出了新选区（超过阈值）才丢弃上一份选区的标注；单纯点一下不会走到这里。
                    DiscardAnnotations();
                }

                _dragging = true;
                _dragMoved = true;
                UpdateRegion(_dragStart, dip);
            }

            MoveLoupe(dip);
            return;
        }

        if (!_locked)
        {
            if (IsOnChrome(e.OriginalSource))
            {
                Loupe.Visibility = Visibility.Collapsed;
                return;
            }

            UpdateWindow(dip);
            MoveLoupe(dip);
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(Root).Properties.PointerUpdateKind == PointerUpdateKind.RightButtonReleased)
        {
            Complete(null);
            return;
        }

        if (_resize >= 0 || _moving)
        {
            _resize = -1;
            _moving = false;
            Root.ReleasePointerCaptures();
            CheckBarcodeAsync();
            return;
        }

        if (_dragging && _locked && _tool != null)
        {
            _dragging = false;
            Root.ReleasePointerCaptures();
            var startDip = DipFromPixel(_pixelStart.X, _pixelStart.Y);
            var rawDip = e.GetCurrentPoint(Root).Position;
            var liveDip = _tool == AnnotateKind.Spotlight ? ClampToSelection(rawDip) : rawDip;
            var end = ToPixel(ConstrainDip(startDip, liveDip));
            TempInk.Children.Clear();
            if ((_tool == AnnotateKind.Pen || _tool == AnnotateKind.Highlighter) && ShiftDown())
            {
                if (Math.Abs(end.X - _pixelStart.X) + Math.Abs(end.Y - _pixelStart.Y) >= 4)
                {
                    RememberStroke(Stroke(_tool.Value, [_pixelStart, end], null));
                }
            }
            else if (_tool is AnnotateKind.Pen or AnnotateKind.Mosaic or AnnotateKind.Highlighter)
            {
                if (_tool == AnnotateKind.Mosaic && (_draft.Count == 0 || _draft[^1] != end))
                {
                    // DrawTemp 的实时预览会把「当前指尖点」补进 live 点列（见 Ink.cs DrawTemp），
                    // 提交时必须带上抬手前的最后一个指尖点，否则预览已遮住、导出尾部漏遮（隐私风险）。
                    _draft.Add(end);
                }

                if (_draft.Count >= 2 || (_tool == AnnotateKind.Mosaic && _draft.Count >= 1))
                {
                    RememberStroke(Stroke(_tool.Value, [.. _draft], null));
                }
            }
            else if (Math.Abs(end.X - _pixelStart.X) >= 4 || Math.Abs(end.Y - _pixelStart.Y) >= 4)
            {
                RememberStroke(Stroke(_tool.Value, [_pixelStart, end], null));
            }

            _draft.Clear();
            RebuildInk();
            return;
        }

        if (!_pressing)
        {
            return;
        }

        _pressing = false;
        var moved = _dragMoved;
        _dragging = false;
        _dragMoved = false;
        Root.ReleasePointerCaptures();
        var dip = e.GetCurrentPoint(Root).Position;
        if (moved)
        {
            UpdateRegion(_dragStart, dip);
        }
        else
        {
            UpdateWindow(dip);
        }

        if (_current is PixelRect { Width: >= 8, Height: >= 8 })
        {
            LockSelection();
            _lastClickTicks = Environment.TickCount64;
            _lastClickPoint = dip;
        }
        else
        {
            ClearSelection();
        }
    }

    private bool IsRepeatClick(Point dip)
    {
        var elapsed = Environment.TickCount64 - _lastClickTicks;
        return elapsed is > 0 and < 400
            && Math.Abs(dip.X - _lastClickPoint.X) < 12
            && Math.Abs(dip.Y - _lastClickPoint.Y) < 12;
    }

    private void LockSelection()
    {
        _locked = true;
        Loupe.Visibility = Visibility.Collapsed;
        Toolbar.Visibility = Visibility.Visible;
        PlaceHandles();
        PaintHistory();
        PlaceToolbar();
        AnimateIn(Toolbar, 14, 460, 0.94);
        CheckBarcodeAsync();
    }

    /// <summary>
    /// 丢弃上一份选区的标注——只有「指针真的拖出新选区」时才调用。
    /// 「点选区外」按下时不再清空（旧行为会误丢全部标注且不可撤销），单纯点一下不会走到这里。
    /// </summary>
    private void DiscardAnnotations()
    {
        if (_strokes.Count == 0 && _redo.Count == 0 && Ink.Children.Count == 0)
        {
            return;
        }

        _strokes.Clear();
        _redo.Clear();
        ResetLiveMosaic();
        Ink.Children.Clear();
        PaintHistory();
    }

    private void UpdateRegion(Point start, Point end)
    {
        var x = Math.Min(start.X, end.X);
        var y = Math.Min(start.Y, end.Y);
        SetDipRect(x, y, Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));
    }

    private void UpdateWindow(Point local)
    {
        var pixel = ToPixel(local);
        // 高回报率鼠标在同一个像素内会连发好几次移动事件，命中结果不可能变化，
        // 没必要每次都重新扫一遍窗口树（Chain 里还有 List + Sort + ToArray 的分配）。
        if (pixel.X == _hoverPixelX && pixel.Y == _hoverPixelY)
        {
            return;
        }

        _hoverPixelX = pixel.X;
        _hoverPixelY = pixel.Y;
        var screenX = ScreenX(local.X);
        var screenY = ScreenY(local.Y);
        var stack = WindowPicker.Chain(screenX, screenY);
        if (stack.Root != _chainRoot)
        {
            _chainRoot = stack.Root;
            _elementLevel = 0;
        }

        _chain = stack;
        if (stack.Levels.Length == 0)
        {
            ClearSelection();
            return;
        }

        var index = Math.Clamp(_elementLevel, 0, stack.Levels.Length - 1);
        _elementLevel = index;
        ShowWindow(stack.Levels[index]);
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_pick == null || _locked || _pressing || _dragging || _chain.Levels.Length == 0)
        {
            return;
        }

        var delta = e.GetCurrentPoint(Root).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        _elementLevel = Math.Clamp(_elementLevel + (delta > 0 ? 1 : -1), 0, _chain.Levels.Length - 1);
        ShowWindow(_chain.Levels[_elementLevel]);
        e.Handled = true;
    }

    private void ShowWindow(WindowBounds window)
    {
        var left = Math.Max(window.X, _monitor.X);
        var top = Math.Max(window.Y, _monitor.Y);
        var right = Math.Min(window.X + window.Width, _monitor.X + _monitor.Width);
        var bottom = Math.Min(window.Y + window.Height, _monitor.Y + _monitor.Height);
        if (right - left < 8 || bottom - top < 8)
        {
            ClearSelection();
            return;
        }

        SetDipRect(DipX(left), DipY(top), DipX(right) - DipX(left), DipY(bottom) - DipY(top));
    }

    private static bool SpaceHeld() => (NativeMethods.GetKeyState(0x20) & 0x8000) != 0;

    private void SetDipRect(double x, double y, double w, double h)
    {
        var fullW = FullWidth();
        var fullH = FullHeight();
        w = Math.Max(1, w);
        h = Math.Max(1, h);
        if (x < 0) x = 0;
        if (y < 0) y = 0;
        if (x + w > fullW) x = Math.Max(0, fullW - w);
        if (y + h > fullH) y = Math.Max(0, fullH - h);
        var pixels = ToPixels(x, y, w, h);
        if (_current is PixelRect current
            && current == pixels
            && Math.Abs(_selX - x) < 0.4
            && Math.Abs(_selY - y) < 0.4
            && Math.Abs(_selW - w) < 0.4
            && Math.Abs(_selH - h) < 0.4)
        {
            return;
        }

        _selX = x;
        _selY = y;
        _selW = w;
        _selH = h;
        _current = pixels;
        ShowSelection(x, y, w, h, pixels.Width, pixels.Height);
        if (_locked)
        {
            PlaceToolbar();
        }
    }

    private void ResizeTo(Point dip)
    {
        var x = _selX;
        var y = _selY;
        var r = _selX + _selW;
        var b = _selY + _selH;
        if (_resize is 0 or 3 or 5) x = dip.X;
        if (_resize is 0 or 1 or 2) y = dip.Y;
        if (_resize is 2 or 4 or 7) r = dip.X;
        if (_resize is 5 or 6 or 7) b = dip.Y;
        SetDipRect(Math.Min(x, r), Math.Min(y, b), Math.Abs(r - x), Math.Abs(b - y));
    }

    private int HitHandle(Point dip)
    {
        var points = HandlePoints();
        for (var i = 0; i < points.Length; i++)
        {
            if (Math.Abs(dip.X - points[i].X) <= 10 && Math.Abs(dip.Y - points[i].Y) <= 10)
            {
                return i;
            }
        }

        return -1;
    }

    private Point[] HandlePoints()
    {
        var x = _selX;
        var y = _selY;
        var r = _selX + _selW;
        var b = _selY + _selH;
        var mx = x + _selW / 2;
        var my = y + _selH / 2;
        return [new(x, y), new(mx, y), new(r, y), new(x, my), new(r, my), new(x, b), new(mx, b), new(r, b)];
    }

    private Point ClampToSelection(Point dip)
    {
        if (!_locked)
        {
            return dip;
        }

        var minX = _selX;
        var maxX = _selX + _selW;
        var minY = _selY;
        var maxY = _selY + _selH;

        return new Point(
            Math.Clamp(dip.X, minX, maxX),
            Math.Clamp(dip.Y, minY, maxY)
        );
    }

    private bool InsideSelection(Point dip)
    {
        return dip.X >= _selX && dip.Y >= _selY && dip.X <= _selX + _selW && dip.Y <= _selY + _selH;
    }

    private void ShowSelection(double x, double y, double w, double h, int pixelW, int pixelH)
    {
        var fullW = FullWidth();
        var fullH = FullHeight();
        FullDim.Visibility = Visibility.Collapsed;
        Shade.Visibility = Visibility.Visible;
        Place(DimTop, 0, 0, fullW, Math.Max(0, y));
        Place(DimLeft, 0, y, Math.Max(0, x), h);
        Place(DimRight, x + w, y, Math.Max(0, fullW - (x + w)), h);
        Place(DimBottom, 0, y + h, fullW, Math.Max(0, fullH - (y + h)));
        Place(Box, x, y, w, h);
        SizeLabel.Text = $"{pixelW} × {pixelH}";
        SizeHost.Visibility = Visibility.Visible;
        Hint.Visibility = Visibility.Collapsed;
        var labelY = y - 26;
        if (labelY < 4)
        {
            labelY = y + 4;
        }

        Place(SizeHost, x, labelY, double.NaN, double.NaN);
        PlaceHandles();
        // 预览与导出对齐：导出是「先把画面裁到选区、再画标注」，所以预览也把 Ink 画布裁到选区。
        // 这样长文本/贴边标注不会再「预览完整、导出半截」，而且移动/缩放选区时裁剪自动跟随。
        ApplyInkClip(x, y, w, h);
    }

    /// <summary>把标注预览画布（Ink）裁剪到选区（dip 坐标 = Ink 画布自身坐标）。</summary>
    private void ApplyInkClip(double x, double y, double w, double h)
    {
        if (w <= 0 || h <= 0)
        {
            Ink.Clip = null;
            return;
        }

        Ink.Clip = new RectangleGeometry { Rect = new Rect(x, y, w, h) };
    }

    private void PlaceHandles()
    {
        var points = HandlePoints();
        var show = _locked ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < _handles.Length; i++)
        {
            _handles[i].Visibility = show;
            Canvas.SetLeft(_handles[i], points[i].X - 4.5);
            Canvas.SetTop(_handles[i], points[i].Y - 4.5);
        }
    }

    private void PlaceToolbar()
    {
        // 三段式胶囊完整排布约 790dip；选区窄于该宽度时把智能工具组收纳为一个入口，
        // 保证工具栏不会过分超出选区、也不会遮挡待标注内容。
        var isCompact = _selW < CompactToolbarWidth;
        if (isCompact)
        {
            if (SmartGroup.Visibility != Visibility.Collapsed) SmartGroup.Visibility = Visibility.Collapsed;
            if (MoreToolsButton.Visibility != Visibility.Visible) MoreToolsButton.Visibility = Visibility.Visible;
        }
        else
        {
            if (SmartGroup.Visibility != Visibility.Visible) SmartGroup.Visibility = Visibility.Visible;
            if (MoreToolsButton.Visibility != Visibility.Collapsed) MoreToolsButton.Visibility = Visibility.Collapsed;
        }

        Toolbar.UpdateLayout();
        var mainW = MainBar.ActualWidth > 1 ? MainBar.ActualWidth : (isCompact ? 626 : 790);
        var mainH = MainBar.ActualHeight > 1 ? MainBar.ActualHeight : 38;
        var styleH = 0.0;
        var barW = mainW;
        if (StyleBar.Visibility == Visibility.Visible)
        {
            var popupW = StyleBar.ActualWidth > 1 ? StyleBar.ActualWidth : 220;
            styleH = (StyleBar.ActualHeight > 1 ? StyleBar.ActualHeight : 40) + 8;
            barW = Math.Max(mainW, popupW);
        }

        var x = _selX + ((_selW - barW) / 2);
        if (x < 8)
        {
            x = 8;
        }

        if (x + barW > FullWidth() - 8)
        {
            x = Math.Max(8, FullWidth() - barW - 8);
        }

        var y = _selY + _selH + 8;
        if (y + mainH + styleH > FullHeight() - 8)
        {
            y = _selY - mainH - styleH - 8;
        }

        if (y < 4)
        {
            y = 4;
        }

        Canvas.SetLeft(Toolbar, x);
        Canvas.SetTop(Toolbar, y);
        PlaceBarcodeCard();
    }

    private void CheckBarcodeAsync()
    {
        _barcodeCts?.Cancel();
        _barcodeCts = new CancellationTokenSource();
        var ct = _barcodeCts.Token;

        if (_current is not PixelRect rect || rect.Width < 20 || rect.Height < 20)
        {
            BarcodeCard.Visibility = Visibility.Collapsed;
            return;
        }

        var sourceCopy = _source;
        var frameW = _frameWidth;

        Task.Run(() =>
        {
            try
            {
                if (ct.IsCancellationRequested) return;
                var cropped = Crop(sourceCopy, frameW, rect);
                var result = BarcodeService.Scan(cropped, rect.Width, rect.Height);
                if (ct.IsCancellationRequested) return;

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    UpdateBarcodeUi(result);
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }, ct);
    }

    private void UpdateBarcodeUi(BarcodeResult? result)
    {
        _detectedBarcode = result;
        if (result == null || !_locked)
        {
            BarcodeCard.Visibility = Visibility.Collapsed;
            return;
        }

        BarcodeFormatText.Text = $"侦测到条码 ({result.Format})";
        BarcodeContentText.Text = result.Text;
        ToolTipService.SetToolTip(BarcodeContentText, result.Text);
        BarcodeOpenBtn.Visibility = result.IsUrl ? Visibility.Visible : Visibility.Collapsed;
        var wasHidden = BarcodeCard.Visibility != Visibility.Visible;
        BarcodeCard.Visibility = Visibility.Visible;
        PlaceBarcodeCard();
        if (wasHidden)
        {
            AnimateIn(BarcodeCard, 9, 360, 0.97);
        }
    }

    private void PlaceBarcodeCard()
    {
        if (BarcodeCard.Visibility != Visibility.Visible)
        {
            return;
        }

        BarcodeCard.UpdateLayout();
        var cardW = BarcodeCard.ActualWidth > 1 ? BarcodeCard.ActualWidth : 260;
        var cardH = BarcodeCard.ActualHeight > 1 ? BarcodeCard.ActualHeight : 38;

        var cx = _selX + ((_selW - cardW) / 2);
        cx = Math.Clamp(cx, 8, Math.Max(8, FullWidth() - cardW - 8));

        var cy = _selY - cardH - 10;
        if (cy < 8)
        {
            cy = _selY + 8;
        }

        Canvas.SetLeft(BarcodeCard, cx);
        Canvas.SetTop(BarcodeCard, cy);
    }

    private void OnBarcodeCopy(object sender, RoutedEventArgs e)
    {
        if (_detectedBarcode != null)
        {
            CopyTextRequested?.Invoke(_detectedBarcode.Text);
            SetHint($"已复制条码内容：{_detectedBarcode.Text}");
        }
    }

    private void OnBarcodeOpen(object sender, RoutedEventArgs e)
    {
        if (_detectedBarcode is { IsUrl: true } barcode)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(barcode.Text)
                {
                    UseShellExecute = true
                });
                SetHint($"已在浏览器中打开链接：{barcode.Text}");
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }
    }

    private void OnBarcodeDismiss(object sender, RoutedEventArgs e)
    {
        BarcodeCard.Visibility = Visibility.Collapsed;
    }

    private static bool ShiftDown() => (NativeMethods.GetKeyState(0x10) & 0x8000) != 0;

    private Point ConstrainDip(Point start, Point end)
    {
        if (!ShiftDown() || _tool is not AnnotateKind kind)
        {
            return end;
        }

        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        if (kind is AnnotateKind.Rectangle or AnnotateKind.Ellipse or AnnotateKind.Spotlight)
        {
            var side = Math.Max(Math.Abs(dx), Math.Abs(dy));
            if (side < 1)
            {
                return end;
            }

            return new Point(start.X + (dx < 0 ? -side : side), start.Y + (dy < 0 ? -side : side));
        }

        if (kind is AnnotateKind.Arrow or AnnotateKind.Pen or AnnotateKind.Highlighter)
        {
            var len = Math.Sqrt((dx * dx) + (dy * dy));
            if (len < 1)
            {
                return end;
            }

            var snap = Math.PI / 4;
            var angle = Math.Round(Math.Atan2(dy, dx) / snap) * snap;
            return new Point(start.X + (Math.Cos(angle) * len), start.Y + (Math.Sin(angle) * len));
        }

        return end;
    }

    private void ClearSelection()
    {
        _current = null;
        FullDim.Visibility = Visibility.Visible;
        Shade.Visibility = Visibility.Collapsed;
        SizeHost.Visibility = Visibility.Collapsed;
        Toolbar.Visibility = Visibility.Collapsed;
        // 没有选区就没有「导出边界」，去掉预览裁剪，避免标注被上一份选区永久裁掉。
        Ink.Clip = null;
        SetHint(null);
    }

    private double FullWidth() => Root.ActualWidth > 1 ? Root.ActualWidth : _monitor.Width / Scale;

    private double FullHeight() => Root.ActualHeight > 1 ? Root.ActualHeight : _monitor.Height / Scale;

    private static void Place(FrameworkElement element, double x, double y, double width, double height)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        if (!double.IsNaN(width))
        {
            element.Width = Math.Max(0, width);
        }

        if (!double.IsNaN(height))
        {
            element.Height = Math.Max(0, height);
        }
    }

    private PixelRect ToPixels(double dipX, double dipY, double dipW, double dipH)
    {
        var fullW = Math.Max(1, FullWidth());
        var fullH = Math.Max(1, FullHeight());
        var x = (int)Math.Round(dipX / fullW * _frameWidth);
        var y = (int)Math.Round(dipY / fullH * _frameHeight);
        var w = Math.Max(1, (int)Math.Round(dipW / fullW * _frameWidth));
        var h = Math.Max(1, (int)Math.Round(dipH / fullH * _frameHeight));
        x = Math.Clamp(x, 0, Math.Max(0, _frameWidth - 1));
        y = Math.Clamp(y, 0, Math.Max(0, _frameHeight - 1));
        w = Math.Clamp(w, 1, _frameWidth - x);
        h = Math.Clamp(h, 1, _frameHeight - y);
        return new PixelRect(x, y, w, h);
    }

    private (int X, int Y) ToPixel(Point dip)
    {
        var rect = ToPixels(dip.X, dip.Y, 1, 1);
        return (rect.X, rect.Y);
    }

    private Point DipFromPixel(int x, int y)
    {
        return new Point(x / (double)Math.Max(1, _frameWidth) * FullWidth(), y / (double)Math.Max(1, _frameHeight) * FullHeight());
    }

    private int ScreenX(double dipX) => _monitor.X + (int)Math.Round(dipX / Math.Max(1, FullWidth()) * _frameWidth);

    private int ScreenY(double dipY) => _monitor.Y + (int)Math.Round(dipY / Math.Max(1, FullHeight()) * _frameHeight);

    private double DipX(int screenX) => (screenX - _monitor.X) / (double)Math.Max(1, _frameWidth) * FullWidth();

    private double DipY(int screenY) => (screenY - _monitor.Y) / (double)Math.Max(1, _frameHeight) * FullHeight();

    private double Scale => Root.XamlRoot?.RasterizationScale > 0 ? Root.XamlRoot.RasterizationScale : 1;

    private void OnRect(object sender, RoutedEventArgs e) => ChooseTool(AnnotateKind.Rectangle, RectButton);
    private void OnEllipse(object sender, RoutedEventArgs e) => ChooseTool(AnnotateKind.Ellipse, EllipseButton);
    private void OnEmoji(object sender, RoutedEventArgs e) => ChooseTool(AnnotateKind.Emoji, EmojiButton);
    private void OnArrow(object sender, RoutedEventArgs e) => ChooseTool(AnnotateKind.Arrow, ArrowButton);
    private void OnPen(object sender, RoutedEventArgs e) => ChooseTool(AnnotateKind.Pen, PenButton);
    private void OnMosaic(object sender, RoutedEventArgs e) => ChooseTool(AnnotateKind.Mosaic, MosaicButton);
    private void OnText(object sender, RoutedEventArgs e) => ChooseTool(AnnotateKind.Text, TextButton);
    private void OnNumber(object sender, RoutedEventArgs e) => ChooseTool(AnnotateKind.Number, NumberButton);
    private void OnHighlighter(object sender, RoutedEventArgs e) => ChooseTool(AnnotateKind.Highlighter, HighlighterButton);
    private void OnSpotlight(object sender, RoutedEventArgs e) => ChooseTool(AnnotateKind.Spotlight, SpotlightButton);

    private void OnSelectEmoji(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string emoji })
        {
            _selectedEmoji = emoji;
            _tool = AnnotateKind.Emoji;
            PaintTools();
        }
    }

    private void ChooseTool(AnnotateKind kind, ToggleButton button)
    {
        if (_toolSync)
        {
            return;
        }

        _toolSync = true;
        if (button.IsChecked == true)
        {
            _tool = kind;
            foreach (var other in new[] { RectButton, EllipseButton, EmojiButton, ArrowButton, PenButton, HighlighterButton, SpotlightButton, MosaicButton, TextButton, NumberButton })
            {
                if (other != null && !ReferenceEquals(other, button))
                {
                    other.IsChecked = false;
                }
            }
        }
        else
        {
            _tool = null;
        }

        _toolSync = false;
        PaintTools();
    }

    private void PaintTools()
    {
        PaintTool(RectButton, AnnotateKind.Rectangle);
        PaintTool(EllipseButton, AnnotateKind.Ellipse);
        if (EmojiButton != null) PaintTool(EmojiButton, AnnotateKind.Emoji);
        PaintTool(ArrowButton, AnnotateKind.Arrow);
        PaintTool(PenButton, AnnotateKind.Pen);
        if (HighlighterButton != null) PaintTool(HighlighterButton, AnnotateKind.Highlighter);
        if (SpotlightButton != null) PaintTool(SpotlightButton, AnnotateKind.Spotlight);
        PaintTool(MosaicButton, AnnotateKind.Mosaic);
        PaintTool(TextButton, AnnotateKind.Text);
        if (NumberButton != null) PaintTool(NumberButton, AnnotateKind.Number);
        UpdateStyle();
    }

    private static void SetCursor(UIElement element, InputCursor cursor)
    {
        typeof(UIElement).GetProperty(
            "ProtectedCursor",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(element, cursor);
    }

    private void PaintTool(ToggleButton button, AnnotateKind kind)
    {
        var on = _tool == kind;
        if (button.IsChecked != on)
        {
            _toolSync = true;
            button.IsChecked = on;
            _toolSync = false;
        }

        TintGlyph(button, on ? _glyphOnBrush : _glyphOffBrush);
    }

    private static void TintGlyph(DependencyObject root, Brush brush)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Microsoft.UI.Xaml.Shapes.Path path && path.Tag as string != "fixed")
            {
                if (path.Tag as string == "fill")
                {
                    path.Fill = brush;
                }
                else
                {
                    path.Stroke = brush;
                }
            }
            else if (child is TextBlock block)
            {
                block.Foreground = brush;
            }

            TintGlyph(child, brush);
        }
    }

    private Color GlyphColor => IsDark
        ? Color.FromArgb(242, 255, 255, 255)
        : Color.FromArgb(230, 0, 0, 0);

    /// <summary>选中态（玻璃片上的高对比前景）与次要前景，跟随明暗主题。</summary>
    private Color EmphasisColor => IsDark
        ? Color.FromArgb(255, 255, 255, 255)
        : Color.FromArgb(255, 0, 0, 0);

    private Color MutedColor => IsDark
        ? Color.FromArgb(140, 255, 255, 255)
        : Color.FromArgb(130, 0, 0, 0);

    private Color SelectionChipColor => IsDark
        ? Color.FromArgb(38, 255, 255, 255)
        : Color.FromArgb(20, 0, 0, 0);

    private bool IsDark => Root.ActualTheme != ElementTheme.Light;

    /// <summary>显示首屏键帽提示；传入消息文本时改为显示单行状态消息。</summary>
    private void SetHint(string? message)
    {
        var hasMessage = !string.IsNullOrEmpty(message);
        HintTips.Visibility = hasMessage ? Visibility.Collapsed : Visibility.Visible;
        HintMessage.Visibility = hasMessage ? Visibility.Visible : Visibility.Collapsed;
        if (hasMessage)
        {
            HintText.Text = message!;
        }

        Hint.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 苹果式入场：轻微缩放 + 上浮 + 淡入。位置用 BackEase 制造一次克制的回弹（弹簧感），
    /// 透明度单独用快速的指数缓出，避免回弹阶段出现闪烁。
    /// </summary>
    private static void AnimateIn(FrameworkElement element, double offsetY, double milliseconds, double fromScale = 0.97)
    {
        var scale = new ScaleTransform { ScaleX = fromScale, ScaleY = fromScale };
        var shift = new TranslateTransform { Y = offsetY };
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(shift);
        element.RenderTransformOrigin = new Point(0.5, 0);
        element.RenderTransform = group;
        element.Opacity = 0;

        var spring = new BackEase { Amplitude = 0.42, EasingMode = EasingMode.EaseOut };
        var soft = new ExponentialEase { Exponent = 4.2, EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var fade = new DoubleAnimation { To = 1, Duration = TimeSpan.FromMilliseconds(milliseconds * 0.55), EasingFunction = soft };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var growX = new DoubleAnimation { To = 1, Duration = duration, EasingFunction = spring };
        Storyboard.SetTarget(growX, scale);
        Storyboard.SetTargetProperty(growX, "ScaleX");
        var growY = new DoubleAnimation { To = 1, Duration = duration, EasingFunction = spring };
        Storyboard.SetTarget(growY, scale);
        Storyboard.SetTargetProperty(growY, "ScaleY");
        var rise = new DoubleAnimation { To = 0, Duration = duration, EasingFunction = spring };
        Storyboard.SetTarget(rise, shift);
        Storyboard.SetTargetProperty(rise, "Y");

        var story = new Storyboard();
        story.Children.Add(fade);
        story.Children.Add(growX);
        story.Children.Add(growY);
        story.Children.Add(rise);
        story.Completed += (_, _) =>
        {
            shift.Y = 0;
            scale.ScaleX = 1;
            scale.ScaleY = 1;
            element.Opacity = 1;
            story.Stop();
        };
        story.Begin();
    }

    private void UpdateStyle()
    {
        var isEmoji = _tool == AnnotateKind.Emoji;
        var showColor = _tool is AnnotateKind.Rectangle or AnnotateKind.Ellipse or AnnotateKind.Arrow or AnnotateKind.Pen or AnnotateKind.Text or AnnotateKind.Number or AnnotateKind.Highlighter;
        var showWidth = _tool is AnnotateKind.Rectangle or AnnotateKind.Ellipse or AnnotateKind.Arrow or AnnotateKind.Pen or AnnotateKind.Mosaic or AnnotateKind.Highlighter;
        var showFont = _tool is AnnotateKind.Text or AnnotateKind.Number;
        var showOneClick = _tool == AnnotateKind.Mosaic;
        var showStyle = showColor || showWidth || isEmoji;
        if (showStyle && StyleBar.Visibility != Visibility.Visible)
        {
            StyleBar.Visibility = Visibility.Visible;
            AnimateIn(StyleBar, 9, 360, 0.96);
        }
        else if (!showStyle)
        {
            StyleBar.Visibility = Visibility.Collapsed;
        }
        WidthRow.Visibility = showWidth ? Visibility.Visible : Visibility.Collapsed;
        FontRow.Visibility = showFont ? Visibility.Visible : Visibility.Collapsed;
        ColorRow.Visibility = showColor ? Visibility.Visible : Visibility.Collapsed;
        OneClickButton.Visibility = showOneClick ? Visibility.Visible : Visibility.Collapsed;
        if (EmojiRow != null) EmojiRow.Visibility = isEmoji ? Visibility.Visible : Visibility.Collapsed;
        StyleSplit.Visibility = (showWidth || showFont) && (showColor || showOneClick) ? Visibility.Visible : Visibility.Collapsed;
        if (_locked)
        {
            PlaceToolbar();
        }
    }

    private void BuildColors()
    {
        for (var i = 0; i < Palette.Length; i++)
        {
            var index = i;
            var color = Palette[i];
            var isWhite = color.R > 240 && color.G > 240 && color.B > 240;
            var dot = new Border
            {
                Width = 15,
                Height = 15,
                CornerRadius = new CornerRadius(7.5),
                Background = new SolidColorBrush(Color.FromArgb(255, color.R, color.G, color.B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 200, 200, 200)),
                BorderThickness = new Thickness(isWhite ? 1 : 0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var ring = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var cell = new Grid { Width = 22, Height = 22 };
            cell.Children.Add(ring);
            cell.Children.Add(dot);
            var hit = new Button
            {
                Style = (Style)Root.Resources["OverlayAction"],
                Width = 26,
                Height = 30,
                Content = cell,
            };
            ToolTipService.SetToolTip(hit, ColorName(index));
            hit.Click += (_, _) => SelectColor(index);
            ColorRow.Children.Add(hit);
            _swatches[i] = dot;
            _swatchRings[i] = ring;
        }
    }

    private static string ColorName(int index) => index switch
    {
        0 => "极光蓝",
        1 => "明黄",
        2 => "翠绿",
        3 => "墨黑",
        4 => "纯白",
        _ => "朱红",
    };

    private void SelectColor(int index)
    {
        var color = Palette[index];
        _red = color.R;
        _green = color.G;
        _blue = color.B;
        _colorIndex = index;
        PaintSwatches();
        PaintWidths();
        if (_textInput != null)
        {
            StyleTextBox(_textInput);
        }
    }

    private void PaintSwatches()
    {
        for (var i = 0; i < _swatches.Length; i++)
        {
            if (_swatches[i] == null)
            {
                continue;
            }

            var on = i == _colorIndex;
            var white = Palette[i].R > 240 && Palette[i].G > 240 && Palette[i].B > 240;
            _swatches[i].BorderThickness = new Thickness(on ? 0 : (white ? 1 : 0));
            _swatches[i].BorderBrush = new SolidColorBrush(Color.FromArgb(255, 190, 190, 190));
            if (_swatchRings[i] is Border ring)
            {
                ring.BorderBrush = new SolidColorBrush(on ? EmphasisColor : Microsoft.UI.Colors.Transparent);
            }
        }
    }

    private void MoveLoupe(Point dip)
    {
        var cropping = !_locked || _resize >= 0 || _moving;
        var drawing = _dragging && _locked && _tool != null;
        if (!cropping || drawing || _textInput != null || _source.Length == 0 || _loupeBitmap == null)
        {
            Loupe.Visibility = Visibility.Collapsed;
            return;
        }

        var pixel = ToPixel(dip);
        if (pixel.X != _loupePixelX || pixel.Y != _loupePixelY)
        {
            _loupePixelX = pixel.X;
            _loupePixelY = pixel.Y;
            PaintLoupe(pixel.X, pixel.Y);
        }

        Loupe.Visibility = Visibility.Visible;
        const double boxW = 154;
        const double boxH = 194;
        var x = dip.X + 22;
        var y = dip.Y + 22;
        if (x + boxW > FullWidth() - 8)
        {
            x = dip.X - boxW - 18;
        }

        if (y + boxH > FullHeight() - 8)
        {
            y = dip.Y - boxH - 18;
        }

        if (x < 8)
        {
            x = 8;
        }

        if (y < 8)
        {
            y = 8;
        }

        Canvas.SetLeft(Loupe, x);
        Canvas.SetTop(Loupe, y);
    }

    private void PaintLoupe(int pixelX, int pixelY)
    {
        var half = LoupeSample / 2;
        var rowBytes = LoupeSample * LoupeZoom * 4;
        Span<byte> zoomedRow = stackalloc byte[rowBytes];
        Span<uint> zoomedRowUints = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(zoomedRow);
        for (var sy = 0; sy < LoupeSample; sy++)
        {
            var py = Math.Clamp(pixelY - half + sy, 0, _frameHeight - 1);
            var srcRow = py * _frameWidth * 4;
            var dst = 0;
            for (var sx = 0; sx < LoupeSample; sx++)
            {
                var px = Math.Clamp(pixelX - half + sx, 0, _frameWidth - 1);
                var source = srcRow + (px * 4);
                uint b = _source[source];
                uint g = _source[source + 1];
                uint r = _source[source + 2];
                uint pixelVal = b | (g << 8) | (r << 16) | 0xFF000000;
                for (var dx = 0; dx < LoupeZoom; dx++)
                {
                    zoomedRowUints[dst++] = pixelVal;
                }
            }

            var rowStart = sy * LoupeZoom * LoupePixels * 4;
            for (var dy = 0; dy < LoupeZoom; dy++)
            {
                zoomedRow.CopyTo(_loupePixels.AsSpan(rowStart + (dy * LoupePixels * 4), rowBytes));
            }
        }

        _loupeStream ??= _loupeBitmap!.PixelBuffer.AsStream();
        _loupeStream.Position = 0;
        _loupeStream.Write(_loupePixels, 0, _loupePixels.Length);
        _loupeBitmap!.Invalidate();
        var center = (Math.Clamp(pixelY, 0, _frameHeight - 1) * _frameWidth + Math.Clamp(pixelX, 0, _frameWidth - 1)) * 4;
        var cr = _source[center + 2];
        var cg = _source[center + 1];
        var cb = _source[center];
        LoupeLabel.Text = $"#{cr:X2}{cg:X2}{cb:X2}";
        _loupeColorBrush.Color = Color.FromArgb(255, cr, cg, cb);
        LoupePosLabel.Text = $"({pixelX}, {pixelY})";
    }
    private bool IsOnChrome(object source)
    {
        // 命中结果只取决于元素在视觉树里的祖先链，同一个 source 反复问结果必然一致
        // （原来每次 PointerMoved 都要走两遍视觉树）。工具栏显隐不改变祖先关系，缓存安全。
        if (ReferenceEquals(source, _chromeSource))
        {
            return _chromeHit;
        }

        var hit = false;
        var node = source as DependencyObject;
        while (node != null)
        {
            if (node == Toolbar || node is TextBox)
            {
                hit = true;
                break;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        _chromeSource = source;
        _chromeHit = hit;
        return hit;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_textInput != null)
        {
            if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                CloseText(false);
            }

            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            Complete(null);
        }
        else if ((e.Key == VirtualKey.Y || (e.Key == VirtualKey.Z && (NativeMethods.GetKeyState(0x10) & 0x8000) != 0)) && (NativeMethods.GetKeyState(0x11) & 0x8000) != 0)
        {
            e.Handled = true;
            OnRedo(this, e);
        }
        else if (e.Key == VirtualKey.Z && (NativeMethods.GetKeyState(0x11) & 0x8000) != 0)
        {
            e.Handled = true;
            OnUndo(this, e);
        }
        else if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            if (!_locked)
            {
                LockFullScreen();
            }

            Confirm();
        }
        else if (e.Key == VirtualKey.C && (NativeMethods.GetKeyState(0x11) & 0x8000) != 0)
        {
            e.Handled = true;
            if (!_locked)
            {
                LockFullScreen();
            }

            Confirm();
        }
        else if (e.Key == VirtualKey.C && (NativeMethods.GetKeyState(0x11) & 0x8000) == 0 && (NativeMethods.GetKeyState(0x12) & 0x8000) == 0)
        {
            var shift = (NativeMethods.GetKeyState(0x10) & 0x8000) != 0;
            if (CopyColorAtCursor(shift))
            {
                e.Handled = true;
            }
        }
        else if (e.Key == VirtualKey.Space)
        {
            e.Handled = true;
        }
        else if (_locked && e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)
        {
            e.Handled = true;
            var fast = (NativeMethods.GetKeyState(0x10) & 0x8000) != 0;
            Nudge(e.Key, fast ? 10 : 1);
        }
        else if (_locked && TryToolKey(e.Key, out var kind))
        {
            e.Handled = true;
            _tool = _tool == kind ? null : kind;
            PaintTools();
        }
    }

    private bool CopyColorAtCursor(bool rgbFormat)
    {
        if (_source.Length == 0 || _frameWidth <= 0 || _frameHeight <= 0)
        {
            return false;
        }

        var px = _loupePixelX;
        var py = _loupePixelY;
        if (px < 0 || px >= _frameWidth || py < 0 || py >= _frameHeight)
        {
            NativeMethods.GetCursorPos(out var pt);
            px = Math.Clamp(pt.X - _monitor.X, 0, _frameWidth - 1);
            py = Math.Clamp(pt.Y - _monitor.Y, 0, _frameHeight - 1);
        }

        var idx = ((py * _frameWidth) + px) * 4;
        if (idx + 2 >= _source.Length)
        {
            return false;
        }

        var b = _source[idx];
        var g = _source[idx + 1];
        var r = _source[idx + 2];

        var code = rgbFormat ? $"rgb({r}, {g}, {b})" : $"#{r:X2}{g:X2}{b:X2}";
        CopyTextRequested?.Invoke(code);
        SetHint($"已复制颜色：{code}");
        return true;
    }

    private void Nudge(VirtualKey key, int step)
    {
        var dx = key == VirtualKey.Left ? -step : key == VirtualKey.Right ? step : 0;
        var dy = key == VirtualKey.Up ? -step : key == VirtualKey.Down ? step : 0;
        SetDipRect(_selX + dx, _selY + dy, _selW, _selH);
    }

    private static bool TryToolKey(VirtualKey key, out AnnotateKind kind)
    {
        switch (key)
        {
            case VirtualKey.R:
            case VirtualKey.Number1:
            case VirtualKey.NumberPad1:
                kind = AnnotateKind.Rectangle;
                return true;
            case VirtualKey.E:
            case VirtualKey.Number2:
            case VirtualKey.NumberPad2:
                kind = AnnotateKind.Ellipse;
                return true;
            case VirtualKey.A:
            case VirtualKey.Number3:
            case VirtualKey.NumberPad3:
                kind = AnnotateKind.Arrow;
                return true;
            case VirtualKey.P:
            case VirtualKey.Number4:
            case VirtualKey.NumberPad4:
                kind = AnnotateKind.Pen;
                return true;
            case VirtualKey.M:
            case VirtualKey.Number5:
            case VirtualKey.NumberPad5:
                kind = AnnotateKind.Mosaic;
                return true;
            case VirtualKey.T:
            case VirtualKey.Number6:
            case VirtualKey.NumberPad6:
                kind = AnnotateKind.Text;
                return true;
            case VirtualKey.N:
            case VirtualKey.Number7:
            case VirtualKey.NumberPad7:
                kind = AnnotateKind.Number;
                return true;
            case VirtualKey.H:
            case VirtualKey.Number8:
            case VirtualKey.NumberPad8:
                kind = AnnotateKind.Highlighter;
                return true;
            case VirtualKey.S:
            case VirtualKey.Number9:
            case VirtualKey.NumberPad9:
                kind = AnnotateKind.Spotlight;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private void Complete(PickedShot? value)
    {
        var pick = _pick;
        if (pick == null)
        {
            return;
        }

        _pick = null;
        _dragging = false;
        _hiding = true;
        AppWindow.Hide();
        _hiding = false;
        _loupeStream?.Dispose();
        _loupeStream = null;
        // 拾取结束：释放全屏像素缓冲与显示位图，避免 4K BGRA(约 33MB)常驻到下次截图。
        // 条码检测等后台任务在 Complete 前已拿到 _source 的引用(数组本身不会被修改)，清空引用不影响它们。
        // 下次 PickAsync 会重新赋值 _source 与 Shot.Source。
        _source = [];
        Shot.Source = null;
        pick.TrySetResult(value);
    }
}
