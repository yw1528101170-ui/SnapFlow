using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;
using Windows.System;

namespace Jietu;

public sealed partial class AiVisionWindow : Window
{
    private const int WindowWidth = 1040;
    private const int WindowHeight = 690;
    private const double DefaultPreviewWidth = 372;

    private readonly AppHost _host;
    private byte[]? _currentPng;
    private bool _busy;
    private bool _allowClose;
    private bool _entrancePlayed;
    private string? _lastAnswer;
    private int _askToken;
    private CancellationTokenSource? _cts;
    private string? _pendingAsk;

    private Storyboard? _loaderStoryboard;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _copyTimer;
    private Brush? _gripIdleBrush;

    public AiVisionWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        WindowChrome.Apply(this, TitleBar, Root);
        AppWindow.Resize(new SizeInt32(WindowWidth, WindowHeight));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 760;
            presenter.PreferredMinimumHeight = 520;
        }

        AppWindow.Closing += (_, e) =>
        {
            if (_allowClose)
            {
                return;
            }

            e.Cancel = true;
            AppWindow.Hide();
        };

        _copyTimer = DispatcherQueue.CreateTimer();
        _copyTimer.Interval = TimeSpan.FromMilliseconds(1250);
        _copyTimer.IsRepeating = false;
        _copyTimer.Tick += (_, _) => RestoreCopyIcon();
        _gripIdleBrush = SplitterGrip.Background;
    }

    public void AllowClose() => _allowClose = true;

    public async void Analyze(byte[] png, string? customPrompt = null)
    {
        _currentPng = png;
        _lastAnswer = null;
        ResultText.Text = string.Empty;

        var settings = _host.Settings;
        var model = string.IsNullOrWhiteSpace(settings.AiModel) ? AppSettings.DefaultAiModel : settings.AiModel.Trim();
        ModelBadgeText.Text = model;
        TitleMetaText.Text = model + " · 截屏理解";

        var prompt = string.IsNullOrWhiteSpace(customPrompt)
            ? (string.IsNullOrWhiteSpace(settings.AiPrompt) ? AppSettings.DefaultAiPrompt : settings.AiPrompt)
            : customPrompt;

        PromptInput.Text = prompt;
        ShowWindow();

        try
        {
            PreviewImage.Source = await ImageBytes.PngToBitmapAsync(png);
            using var sbm = await ImageBytes.FromPngAsync(png);
            ImageInfoLabel.Text = $"{sbm.PixelWidth} × {sbm.PixelHeight} · {png.Length / 1024:N0} KB";
            PopPreview();
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            ImageInfoLabel.Text = "截屏图像已载入";
        }

        await AskAsync(prompt);
    }

    private void ShowWindow()
    {
        bool wasVisible = AppWindow.IsVisible;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindow.MoveAndResize(ScreenUtil.PlaceNearCursor(hwnd, WindowWidth, WindowHeight));
        Activate();
        if (!wasVisible || !_entrancePlayed)
        {
            _entrancePlayed = true;
            PlayEntrance();
        }
    }

    // ── 入场编排：玻璃面板整体浮现，各区块按阅读顺序错峰上浮 ────────────

    private void PlayEntrance()
    {
        Motion.WindowIn(Root);

        var leftTr = Motion.Prep(LeftPanel);
        Motion.Fade(LeftPanel, 0, 1, 260, Motion.Ease, 20);
        Motion.MoveX(LeftPanel, leftTr, -16, 0, 460, Motion.Spring(0.12), 20);

        var cardTr = Motion.Prep(ResultCard);
        Motion.Fade(ResultCard, 0, 1, 280, Motion.Ease, 80);
        Motion.MoveY(ResultCard, cardTr, 12, 0, 460, Motion.Spring(0.12), 80);

        int index = 0;
        foreach (var child in ChipsHost.Children)
        {
            if (child is not FrameworkElement chip)
            {
                continue;
            }

            int delay = 60 + (index++ * 38);
            var chipTr = Motion.Prep(chip);
            Motion.Fade(chip, 0, 1, 220, Motion.Ease, delay);
            Motion.MoveY(chip, chipTr, 9, 0, 400, Motion.Spring(0.18), delay);
        }

        var composerTr = Motion.Prep(ComposerHost);
        Motion.Fade(ComposerHost, 0, 1, 280, Motion.Ease, 150);
        Motion.MoveY(ComposerHost, composerTr, 18, 0, 500, Motion.Spring(0.14), 150);
    }

    /// <summary>新截图落位：轻微的「照片弹入」。</summary>
    private void PopPreview()
    {
        var transform = Motion.Prep(PreviewImage);
        Motion.Fade(PreviewImage, 0, 1, 300, Motion.Ease, 30);
        Motion.Scale(PreviewImage, transform, 0.985, 1, 440, Motion.Spring(0.1), 30);
    }

    // ── 询问 ───────────────────────────────────────────────────────────

    private async Task AskAsync(string prompt)
    {
        if (_currentPng == null)
        {
            return;
        }

        if (_busy)
        {
            // 分析中又来了新请求（新截图）：记下待办并取消当前请求，
            // 由收尾中的旧调用接力重新发起，保证最新的截图总会被分析。
            _pendingAsk = prompt;
            _cts?.Cancel();
            return;
        }

        _busy = true;
        int token = ++_askToken;
        _cts = new CancellationTokenSource();
        var cancellation = _cts;

        EnterBusyState(prompt);

        string answer;
        try
        {
            answer = await AiVisionService.AskVisionAsync(_currentPng, prompt, _host.Settings, cancellation.Token);
        }
        catch (Exception ex)
        {
            answer = AiVisionContract.InvokeErrorPrefix + ": " + ex.Message;
        }

        if (token != _askToken)
        {
            return;
        }

        bool cancelled = cancellation.IsCancellationRequested;
        cancellation.Dispose();
        _cts = null;
        _busy = false;
        LeaveBusyState();

        // 收尾时若有排队的新请求，跳过本次结果展示，立即按最新内容重新发起
        if (_pendingAsk is { } pending)
        {
            _pendingAsk = null;
            await AskAsync(pending);
            return;
        }

        if (cancelled)
        {
            if (_lastAnswer != null)
            {
                RevealAnswer(_lastAnswer, animate: true);
            }
            else
            {
                SetStatus("已停止", "AiSkeletonBrush");
                EmptyHost.Visibility = Visibility.Visible;
                Motion.Fade(EmptyHost, 0, 1, 200, Motion.Ease);
            }

            return;
        }

        _lastAnswer = answer;
        RevealAnswer(answer, animate: true);
    }

    // ── 状态机：就绪 / 分析中 / 完成 / 失败 ────────────────────────────

    private void EnterBusyState(string prompt)
    {
        SetStatus("分析中", "SnapFlowAccentBrush");
        EmptyHost.Visibility = Visibility.Collapsed;
        ResultText.Visibility = Visibility.Collapsed;
        ResultScroller.Visibility = Visibility.Collapsed;

        LoadingStatusText.Text = $"正在用 {ModelBadgeText.Text} 解析这张截屏…";
        LoadingHost.Visibility = Visibility.Visible;
        LoadingHost.Opacity = 0;
        var transform = Motion.Prep(LoadingHost);
        Motion.Fade(LoadingHost, 0, 1, 240, Motion.Ease, 50);
        Motion.Scale(LoadingHost, transform, 0.97, 1, 400, Motion.Spring(0.16), 50);

        StartLoader();
        SetBusyChrome(true);
    }

    private void LeaveBusyState()
    {
        StopLoader();
        int token = _askToken;
        Motion.Fade(LoadingHost, 1, 0, 150, Motion.EaseIn, 0, () =>
        {
            if (token == _askToken && !_busy)
            {
                LoadingHost.Visibility = Visibility.Collapsed;
            }
        });

        SetBusyChrome(false);
    }

    private void RevealAnswer(string answer, bool animate)
    {
        var (label, brushKey) = Classify(answer);
        SetStatus(label, brushKey);

        ResultText.Text = answer;
        ResultScroller.Visibility = Visibility.Visible;
        ResultText.Visibility = Visibility.Visible;
        ResultScroller.ChangeView(null, 0, null, true);

        if (!animate)
        {
            ResultText.Opacity = 1;
            return;
        }

        var transform = Motion.Prep(ResultText);
        Motion.Fade(ResultText, 0, 1, 320, Motion.Ease, 40);
        Motion.MoveY(ResultText, transform, 10, 0, 470, Motion.Spring(0.1), 40);
    }

    private static (string Label, string BrushKey) Classify(string answer)
    {
        if (answer.StartsWith(AiVisionContract.HintPrefix, StringComparison.Ordinal))
        {
            return ("需要配置", "SnapFlowWarningBrush");
        }

        if (answer.StartsWith(AiVisionContract.RequestFailedPrefix, StringComparison.Ordinal)
            || answer.StartsWith(AiVisionContract.NetworkErrorPrefix, StringComparison.Ordinal)
            || answer.StartsWith(AiVisionContract.InvokeErrorPrefix, StringComparison.Ordinal)
            || answer.StartsWith(AiVisionContract.NoContentPrefix, StringComparison.Ordinal))
        {
            return ("请求失败", "SnapFlowDangerBrush");
        }

        return ("已完成", "SnapFlowSuccessBrush");
    }

    private void SetStatus(string label, string brushKey)
    {
        StatusText.Text = label;
        if (Application.Current.Resources.TryGetValue(brushKey, out var brush) && brush is Brush b)
        {
            StatusDot.Fill = b;
        }
    }

    private void SetBusyChrome(bool busy)
    {
        SendGlyph.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        StopGlyph.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ComposerHint.Text = busy ? "分析中… 点击右侧按钮可立即停止" : "Enter 发送 · Shift + Enter 换行";

        foreach (var child in ChipsHost.Children)
        {
            if (child is not FrameworkElement chip)
            {
                continue;
            }

            chip.IsHitTestVisible = !busy;
            Motion.Fade(chip, busy ? 1 : 0.45, busy ? 0.45 : 1, 200, Motion.Ease);
        }
    }

    // ── 加载动效 ───────────────────────────────────────────────────────

    private void StartLoader()
    {
        StopLoader();

        var spin = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = new Duration(TimeSpan.FromMilliseconds(1100)),
            RepeatBehavior = RepeatBehavior.Forever,
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(spin, LoaderRing);
        Storyboard.SetTargetProperty(spin, "(UIElement.RenderTransform).(RotateTransform.Angle)");

        var breathe = new DoubleAnimation
        {
            From = 0.5,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(900)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(breathe, LoadingStatusText);
        Storyboard.SetTargetProperty(breathe, "Opacity");

        var skeleton = new DoubleAnimation
        {
            From = 0.22,
            To = 0.6,
            Duration = new Duration(TimeSpan.FromMilliseconds(760)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromMilliseconds(140),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(skeleton, SkeletonA);
        Storyboard.SetTargetProperty(skeleton, "Opacity");

        var skeleton2 = new DoubleAnimation
        {
            From = 0.22,
            To = 0.6,
            Duration = new Duration(TimeSpan.FromMilliseconds(760)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromMilliseconds(280),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(skeleton2, SkeletonB);
        Storyboard.SetTargetProperty(skeleton2, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(spin);
        storyboard.Children.Add(breathe);
        storyboard.Children.Add(skeleton);
        storyboard.Children.Add(skeleton2);
        storyboard.Begin();

        _loaderStoryboard = storyboard;
    }

    private void StopLoader()
    {
        _loaderStoryboard?.Stop();
        _loaderStoryboard = null;
        LoadingStatusText.Opacity = 1;
        SkeletonA.Opacity = 0.5;
        SkeletonB.Opacity = 0.5;
    }

    // ── 交互 ───────────────────────────────────────────────────────────

    private async void OnSendClick(object sender, RoutedEventArgs e)
    {
        PopSendButton();

        if (_busy)
        {
            _cts?.Cancel();
            return;
        }

        var text = PromptInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            EmptyHost.Visibility = Visibility.Visible;
            var emptyTr = Motion.Prep(EmptyHost);
            Motion.Fade(EmptyHost, 0, 1, 200, Motion.Ease);
            Motion.Scale(EmptyHost, emptyTr, 0.97, 1, 340, Motion.Spring(0.2));
            return;
        }

        await AskAsync(text);
    }

    private void PopSendButton()
    {
        var transform = Motion.Prep(SendButton);
        Motion.Scale(SendButton, transform, 0.92, 1, 400, Motion.Spring(0.26));
    }

    private async void OnPromptKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        var shift = (NativeMethods.GetKeyState(0x10) & 0x8000) != 0;
        if (shift)
        {
            return;
        }

        e.Handled = true;
        if (_busy)
        {
            _cts?.Cancel();
            return;
        }

        var text = PromptInput.Text.Trim();
        if (!string.IsNullOrWhiteSpace(text))
        {
            PopSendButton();
            await AskAsync(text);
        }
    }

    private async void OnQuickPromptClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string prompt } button)
        {
            return;
        }

        var transform = Motion.Prep(button);
        Motion.Scale(button, transform, 0.94, 1, 400, Motion.Spring(0.26));

        PromptInput.Text = prompt;
        if (_busy)
        {
            return;
        }

        await AskAsync(prompt);
    }

    private void OnPromptFocus(object sender, RoutedEventArgs e)
    {
        Motion.Fade(ComposerGlow, 0, 1, 200, Motion.Ease);
    }

    private void OnPromptFocusOut(object sender, RoutedEventArgs e)
    {
        Motion.Fade(ComposerGlow, 1, 0, 220, Motion.EaseIn);
    }

    private void OnCopyResultClick(object sender, RoutedEventArgs e)
    {
        var text = _lastAnswer ?? ResultText.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _host.Clipboard.SetText(text);
        _host.Toast.Show("已复制 AI 分析结果");

        CopyGlyph.Visibility = Visibility.Collapsed;
        CopyDoneGlyph.Visibility = Visibility.Visible;
        var transform = Motion.Prep(CopyDoneGlyph);
        Motion.Fade(CopyDoneGlyph, 0, 1, 150, Motion.Ease);
        Motion.Scale(CopyDoneGlyph, transform, 0.6, 1, 380, Motion.Spring(0.32));
        _copyTimer.Stop();
        _copyTimer.Start();
    }

    private void RestoreCopyIcon()
    {
        CopyDoneGlyph.Visibility = Visibility.Collapsed;
        CopyGlyph.Visibility = Visibility.Visible;
        Motion.Fade(CopyGlyph, 0, 1, 160, Motion.Ease);
    }

    private async void OnCopyImageClick(object sender, RoutedEventArgs e)
    {
        if (_currentPng == null)
        {
            return;
        }

        try
        {
            var path = ScreenshotFiles.TempPngPath();
            await File.WriteAllBytesAsync(path, _currentPng);
            _host.Clipboard.SetFiles([path]);
            _host.Toast.Show("已复制截图图片");
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    }

    private async void OnPinClick(object sender, RoutedEventArgs e)
    {
        if (_currentPng == null)
        {
            return;
        }

        try
        {
            var path = ScreenshotFiles.TempPngPath();
            await File.WriteAllBytesAsync(path, _currentPng);
            _host.PinFile(path);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        _host.ShowSettings();
    }

    /// <summary>Esc：分析中先停止请求，闲置时收起面板。</summary>
    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape)
        {
            return;
        }

        e.Handled = true;
        if (_busy)
        {
            _cts?.Cancel();
            return;
        }

        AppWindow.Hide();
    }

    // ── 弹性分隔条：悬停浮出高光，拖拽跟手 ──────────────────────────────

    private bool _splitterDragging;
    private double _splitterStartX;
    private double _splitterStartWidth;

    private void OnSplitterPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        Motion.Fade(SplitterHoverLine, 0, 1, 160, Motion.Ease);
        Motion.Scale(SplitterGrip, GripTr, 1, 1.2, 340, Motion.Spring(0.2));
        SplitterGrip.Background = (Brush)Application.Current.Resources["SnapFlowAccentBrush"];
    }

    private void OnSplitterPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_splitterDragging)
        {
            return;
        }

        Motion.Fade(SplitterHoverLine, 1, 0, 190, Motion.EaseIn);
        Motion.Scale(SplitterGrip, GripTr, 1.2, 1, 260, Motion.Ease);
        RestoreGripBrush();
    }

    private void OnSplitterPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _splitterDragging = true;
        _splitterStartX = e.GetCurrentPoint(Root).Position.X;
        _splitterStartWidth = LeftCol.ActualWidth;
        Splitter.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnSplitterPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_splitterDragging)
        {
            return;
        }

        var currentX = e.GetCurrentPoint(Root).Position.X;
        var delta = currentX - _splitterStartX;
        var targetWidth = Math.Clamp(_splitterStartWidth + delta, 236, Math.Max(300, Root.ActualWidth - 360));
        LeftCol.Width = new GridLength(targetWidth, GridUnitType.Pixel);
    }

    private void OnSplitterPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_splitterDragging)
        {
            return;
        }

        _splitterDragging = false;
        Splitter.ReleasePointerCaptures();
        Motion.Fade(SplitterHoverLine, 1, 0, 190, Motion.EaseIn);
        Motion.Scale(SplitterGrip, GripTr, 1.2, 1, 260, Motion.Ease);
        RestoreGripBrush();
    }

    private void RestoreGripBrush()
    {
        if (_gripIdleBrush != null)
        {
            SplitterGrip.Background = _gripIdleBrush;
        }
    }

    private void OnSplitterDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        LeftCol.Width = new GridLength(DefaultPreviewWidth, GridUnitType.Pixel);
        Motion.Scale(SplitterGrip, GripTr, 1.2, 1, 300, Motion.Spring(0.24));
    }
}
