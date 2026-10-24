using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Jietu;

/// <summary>
/// 系统级 Toast 胶囊宿主：负责生命周期与动效驱动，像素交给 <see cref="ToastRenderer"/>。
/// 入场为阻尼弹簧的「缩放 + 上浮 + 淡入」，退场快速下沉淡出；
/// 文案切换时胶囊宽度做弹性形变，符合 Apple 通知的形变节奏。
/// </summary>
public sealed class ToastWindow : IDisposable, IToastSink
{
    public struct AppWindowProxy
    {
        private readonly ToastWindow _owner;
        public AppWindowProxy(ToastWindow owner) => _owner = owner;
        public bool IsVisible => _owner.IsVisible;
    }

    private const float FrameIntervalMs = 16f;

    private readonly IntPtr _hwnd;
    private readonly NativeMethods.WndProc _wndProc;
    private readonly nuint _gdiplusToken;
    private readonly DispatcherQueueTimer? _frameTimer;
    private readonly DispatcherQueueTimer? _dwellTimer;
    private readonly LayeredCanvas _canvas = new();
    private readonly LayeredCanvas _probe = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _lock = new();

    private string _message = string.Empty;
    private ToastKind _kind = ToastKind.Success;
    private bool _busy;
    private bool _autoHide;
    private bool _dwellArmed;
    private float _scale = 1f;
    private int _anchorRight;
    private int _anchorBottom;
    private float _width;
    private float _widthTarget = 200f;
    private float _widthVelocity;
    private float _presence;
    private float _presenceTarget;
    private float _presenceVelocity;
    private float _spin;
    private bool _ticking;
    private double _lastTickMs;
    private TaskCompletionSource? _hideCompletion;

    public AppWindowProxy AppWindow => new(this);
    public bool IsVisible { get; private set; }

    public ToastWindow()
    {
        var input = OverlayGdi.GdiplusStartupInput.Default;
        OverlayGdi.GdiplusStartup(out _gdiplusToken, ref input, IntPtr.Zero);

        _wndProc = (hwnd, msg, wParam, lParam) => NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
        var hInst = NativeMethods.GetModuleHandle(null);
        var wc = new NativeMethods.WndClass
        {
            Style = 0,
            LpfnWndProc = _wndProc,
            HInstance = hInst,
            LpszClassName = "SnapFlow.ToastCapsuleWindow",
        };
        NativeMethods.RegisterClass(ref wc);

        _hwnd = NativeMethods.CreateWindowEx(
            NativeMethods.WsExLayered | NativeMethods.WsExTopmost | NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate | NativeMethods.WsExTransparent,
            wc.LpszClassName,
            "SnapFlowToast",
            NativeMethods.WsPopup,
            0, 0, 0, 0,
            IntPtr.Zero,
            IntPtr.Zero,
            hInst,
            IntPtr.Zero);

        var queue = DispatcherQueue.GetForCurrentThread();
        if (queue != null)
        {
            _frameTimer = queue.CreateTimer();
            _frameTimer.Interval = TimeSpan.FromMilliseconds(FrameIntervalMs);
            _frameTimer.IsRepeating = true;
            _frameTimer.Tick += (_, _) => OnFrame();

            _dwellTimer = queue.CreateTimer();
            _dwellTimer.IsRepeating = false;
            _dwellTimer.Tick += (_, _) =>
            {
                _dwellTimer?.Stop();
                Hide();
            };
        }

        // 让 Toast 不进入任何抓屏/录屏（Win10 2004+）；失败只记日志，绝不影响通知本身。
        ExcludeFromCapture();
    }

    /// <summary>
    /// 把窗口标记为「不参与抓屏」（WDA_EXCLUDEFROMCAPTURE）。
    /// 低版本 Windows 或句柄无效时调用会失败，这里静默降级——Toast 照常显示，只是可能被截进图里。
    /// </summary>
    private void ExcludeFromCapture()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (!NativeMethods.SetWindowDisplayAffinity(_hwnd, NativeMethods.WdaExcludeFromCapture))
            {
                Log.Info("Toast 无法从抓屏中排除：" + Marshal.GetLastWin32Error());
            }
        }
        catch (Exception ex)
        {
            Log.Info("Toast 无法从抓屏中排除：" + ex.Message);
        }
    }

    #region 对外接口

    public void AllowClose()
    {
        lock (_lock)
        {
            StopTicking();
            _dwellTimer?.Stop();
            HideInternal();
        }
    }

    public void Show(string message) => Present(message, autoHide: true);

    public void ShowSticky(string message) => Present(message, autoHide: false);

    /// <summary>带退场动画的隐藏。</summary>
    public void Hide()
    {
        lock (_lock)
        {
            _dwellTimer?.Stop();
            _dwellArmed = false;
            _presenceTarget = 0f;
            if (_frameTimer == null)
            {
                _presence = 0f;
                HideInternal();
                return;
            }

            StartTicking();
        }
    }

    /// <summary>
    /// 带退场动画的隐藏，并等待窗口真正隐藏（抓屏前调用，确保 Toast 不会出现在截图里）。
    /// 与 <see cref="Hide"/> 的动画完全一致，只是把「动画跑完」变成可等待的；
    /// 超时兜底保证任何异常环境（无帧定时器、动画被打断）下都不会无限等待。
    /// </summary>
    public async Task HideAsync(int timeoutMs = 800)
    {
        Task hidden;
        TaskCompletionSource tcs;
        lock (_lock)
        {
            _dwellTimer?.Stop();
            _dwellArmed = false;
            _presenceTarget = 0f;

            // 已经隐藏（或根本没有帧定时器）：没有可等的动画，直接同步隐藏并返回。
            if (_frameTimer == null || !IsVisible)
            {
                _presence = 0f;
                _presenceVelocity = 0f;
                StopTicking();
                HideInternal();
                return;
            }

            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_hideCompletion is { } previous)
            {
                // 并发的第二次隐藏等待：转发到本次等待上，避免上一个等待者永远悬挂。
                _ = ForwardHideAsync(previous, tcs.Task);
            }

            _hideCompletion = tcs;
            StartTicking();
            hidden = tcs.Task;
        }

        // 锁外等待：HideInternal 在动画帧里持锁完成 TCS，若持锁等待会死锁。
        await Task.WhenAny(hidden, Task.Delay(timeoutMs));

        lock (_lock)
        {
            if (ReferenceEquals(_hideCompletion, tcs))
            {
                // 超时或动画被打断：清掉自己的等待者，别让下一次隐藏去完成一个没人等的旧 TCS。
                _hideCompletion = null;
            }

            // 兜底落定：已完成时是 no-op；超时路径在这里把 TCS 置为完成，
            // 否则若 HideInternal 稍后才真正执行，它会因为字段已被清掉而无处完成这个 TCS，
            // ForwardHideAsync 的转发链（以及任何直接 await hidden 的调用方）就此悬挂。
            tcs.TrySetResult();
        }
    }

    /// <summary>把被顶替的等待者接到新一次隐藏上（隐藏一完成就唤醒它）。</summary>
    private static async Task ForwardHideAsync(TaskCompletionSource previous, Task current)
    {
        await current;
        previous.TrySetResult();
    }

    #endregion

    private void Present(string message, bool autoHide)
    {
        if (string.IsNullOrEmpty(message) || _hwnd == IntPtr.Zero)
        {
            return;
        }

        lock (_lock)
        {
            bool fresh = _presence < 0.06f;
            _message = message;
            _busy = !autoHide;
            _autoHide = autoHide;
            _kind = Classify(message, autoHide);

            _dwellArmed = false;
            _dwellTimer?.Stop();

            if (!MeasureTarget())
            {
                return;
            }

            if (fresh)
            {
                _presence = 0f;
                _presenceVelocity = 0f;
                _width = _widthTarget;
                _widthVelocity = 0f;
                _spin = 0f;
            }
            else
            {
                // 已在显示中：胶囊宽度弹性过渡到新文案，形成 Apple 式的形变
                _widthVelocity = 0f;
            }

            _presenceTarget = 1f;
            StartTicking();
        }
    }

    private bool MeasureTarget()
    {
        if (NativeMethods.GetCursorPos(out var pt))
        {
            var area = DisplayArea.GetFromPoint(new PointInt32(pt.X, pt.Y), DisplayAreaFallback.Nearest);
            var monitor = Win32Interop.GetMonitorFromDisplayId(area.DisplayId);
            float scale = 1f;
            if (monitor != IntPtr.Zero
                && NativeMethods.GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0
                && dpiX >= 96)
            {
                scale = dpiX / 96f;
            }

            _scale = scale;
            var work = area.WorkArea;
            int margin = (int)Math.Round(24 * scale);
            _anchorRight = work.X + work.Width - margin;
            _anchorBottom = work.Y + work.Height - margin;
        }

        if (!_probe.Ensure(8, 8))
        {
            return false;
        }

        _widthTarget = ToastRenderer.MeasureWidth(_probe.Graphics, _scale, _message);
        return _widthTarget > 0f;
    }

    private static ToastKind Classify(string message, bool sticky)
    {
        if (sticky)
        {
            return ToastKind.Info;
        }

        if (message.Contains("失败") || message.Contains("错误") || message.Contains("占用")
            || message.Contains("异常") || message.Contains("未识别"))
        {
            return ToastKind.Warning;
        }

        return ToastKind.Success;
    }

    #region 动效驱动

    private void StartTicking()
    {
        if (_frameTimer == null)
        {
            _presence = _presenceTarget;
            if (_presenceTarget <= 0.5f)
            {
                HideInternal();
            }
            else
            {
                Render();
            }

            return;
        }

        if (_ticking)
        {
            return;
        }

        _lastTickMs = _clock.Elapsed.TotalMilliseconds;
        _ticking = true;
        _frameTimer.Start();
    }

    private void StopTicking()
    {
        _ticking = false;
        _frameTimer?.Stop();
    }

    private void OnFrame()
    {
        lock (_lock)
        {
            double now = _clock.Elapsed.TotalMilliseconds;
            float dt = (float)Math.Clamp((now - _lastTickMs) / 1000.0, 0.001, 0.05);
            _lastTickMs = now;

            bool showing = _presenceTarget > 0.5f;
            OverlayGdi.SpringStep(ref _presence, ref _presenceVelocity, _presenceTarget, dt,
                showing ? 320f : 460f,
                showing ? 21f : 42f);

            if (showing)
            {
                OverlayGdi.SpringStep(ref _width, ref _widthVelocity, _widthTarget, dt, 380f, 27f);
            }

            if (_busy)
            {
                _spin = (_spin + dt * 250f) % 360f;
            }

            bool settled = MathF.Abs(_presence - _presenceTarget) <= 0.004f && MathF.Abs(_presenceVelocity) <= 0.06f;
            if (settled)
            {
                _presence = _presenceTarget;
                _presenceVelocity = 0f;
            }

            if (!showing && settled)
            {
                StopTicking();
                HideInternal();
                return;
            }

            Render();

            if (settled && !_busy)
            {
                StopTicking();
                if (showing && _autoHide && !_dwellArmed)
                {
                    ArmDwell();
                }
            }
        }
    }

    private void ArmDwell()
    {
        _dwellArmed = true;
        if (_dwellTimer == null)
        {
            return;
        }

        // 文案越长停留越久，但收敛在 1.8s ~ 4.2s，避免长时间遮挡
        int ms = (int)Math.Clamp(1500 + _message.Length * 55, 1800, 4200);
        _dwellTimer.Interval = TimeSpan.FromMilliseconds(ms);
        _dwellTimer.Start();
    }

    #endregion

    #region 渲染

    private void Render()
    {
        float bufferWidth = MathF.Max(_width, _widthTarget) * 1.06f;
        var (w, h) = ToastRenderer.BufferSize(_scale, bufferWidth);
        if (!_canvas.Ensure(w, h))
        {
            return;
        }

        var state = new ToastRenderer.State(_message, _kind, _busy, _width, _presence, _spin);
        ToastRenderer.Draw(_canvas.Graphics, _scale, state, w, h);

        float pad = ToastRenderer.ShadowPadDip * _scale;
        float rise = ToastRenderer.RiseDip * _scale;
        int x = _anchorRight - w + (int)MathF.Round(pad);
        int y = _anchorBottom - h + (int)MathF.Round(pad + rise);

        if (!_canvas.Present(_hwnd, x, y))
        {
            return;
        }

        if (!IsVisible)
        {
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HwndTopmost, x, y, w, h,
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
            IsVisible = true;
        }
    }

    private void HideInternal()
    {
        if (_hwnd != IntPtr.Zero && IsVisible)
        {
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SwHide);
        }

        IsVisible = false;
        // 唤醒 HideAsync 的等待者；每次隐藏后清掉旧的 TCS，避免上一个等待者悬挂或被重复完成。
        _hideCompletion?.TrySetResult();
        _hideCompletion = null;
    }

    #endregion

    public void Dispose()
    {
        lock (_lock)
        {
            StopTicking();
            _dwellTimer?.Stop();
            HideInternal();
        }

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
        }

        _canvas.Dispose();
        _probe.Dispose();
        if (_gdiplusToken != 0)
        {
            OverlayGdi.GdiplusShutdown(_gdiplusToken);
        }
    }
}
