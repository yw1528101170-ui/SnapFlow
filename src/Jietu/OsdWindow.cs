using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Jietu;

/// <summary>
/// 屏幕性能悬浮窗（OSD）宿主：负责窗口生命周期、屏幕/DPI 定位与动效驱动，
/// 具体像素由 <see cref="OsdRenderer"/> 绘制。
/// 采用 Apple 式「阻尼弹簧」：展开与收起带轻微过冲，数值之间指数趋近，避免任何跳变；
/// 静止后立即停表，保证常驻时零占用。
/// </summary>
public sealed class OsdWindow : IDisposable
{
    private const float FrameIntervalMs = 16f;

    /// <summary>数值追赶阶段的出帧间隔：形体已经静止，只是让数字平滑靠拢，30fps 足够。</summary>
    private const float ValueFrameIntervalMs = 33f;

    private readonly IntPtr _hwnd;
    private readonly NativeMethods.WndProc _wndProc;
    private readonly nuint _gdiplusToken;
    private readonly DispatcherQueueTimer? _frameTimer;
    private readonly LayeredCanvas _canvas = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _lock = new();
    // 渲染专用锁：GDI+ 绘制已移出 _lock（见 OnFrame），用它串行化 OnFrame 与 SetPosition/SetTargetDisplay 的绘制。
    private readonly object _renderLock = new();

    private bool _enabled;
    private bool _hiddenByCapture;
    private string _position = "TopRight";
    private string _targetDisplay = "Primary";
    private HardwareMetrics? _lastMetrics;

    private OsdRenderer.Metrics _display;
    private (int Gpu, int Temp, int Vram, int Cpu, int Ghz, int Ram, int RamUsed, long Down, long Up, float Fps) _renderKey;
    private bool _seeded;
    private float _reveal;
    private float _revealVelocity;
    private float _revealTarget;
    private bool _ticking;
    private double _lastTickMs;
    private TaskCompletionSource? _hideCompletion;

    public bool IsVisible { get; private set; }

    /// <summary>最近一次采样值（供设置页预览复用，避免重复采样 NVML / WMI）。</summary>
    internal HardwareMetrics? LastMetrics
    {
        get
        {
            lock (_lock)
            {
                return _lastMetrics;
            }
        }
    }

    public OsdWindow()
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
            LpszClassName = "SnapFlow.OsdWindow",
        };
        NativeMethods.RegisterClass(ref wc);

        _hwnd = NativeMethods.CreateWindowEx(
            NativeMethods.WsExLayered | NativeMethods.WsExTopmost | NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate | NativeMethods.WsExTransparent,
            wc.LpszClassName,
            "SnapFlowOsd",
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
        }
    }

    #region 对外状态

    public void SetEnabled(bool enabled)
    {
        lock (_lock)
        {
            _enabled = enabled;
            if (!enabled)
            {
                _revealTarget = 0f;
                if (_frameTimer == null || _reveal <= 0.01f)
                {
                    HideInternal();
                }
                else
                {
                    StartTicking();
                }
            }
            else
            {
                _revealTarget = 1f;
                if (_reveal <= 0.01f)
                {
                    _revealVelocity = 0f;
                }

                StartTicking();
            }
        }
    }

    public void SetPosition(string position)
    {
        lock (_lock)
        {
            _position = string.Equals(position, "TopLeft", StringComparison.OrdinalIgnoreCase) ? "TopLeft" : "TopRight";
            if (IsVisible && _seeded)
            {
                RenderFrame();
            }
        }
    }

    public void SetTargetDisplay(string targetDisplay)
    {
        lock (_lock)
        {
            _targetDisplay = string.Equals(targetDisplay, "FollowCursor", StringComparison.OrdinalIgnoreCase) ? "FollowCursor" : "Primary";
            if (IsVisible && _seeded)
            {
                RenderFrame();
            }
        }
    }

    /// <summary>
    /// 截图前退场：弹簧缩回屏幕边缘，隐藏完成后才返回（带上限超时）。
    /// 这样「退出视野」是有动效的，同时保证真正抓屏时画面上已经没有它。
    /// </summary>
    public async Task RetractAsync(int timeoutMs = 320)
    {
        Task hidden;
        lock (_lock)
        {
            _hiddenByCapture = true;
            _revealTarget = 0f;
            if (_frameTimer == null || !IsVisible || _reveal <= 0.02f)
            {
                _reveal = 0f;
                _revealVelocity = 0f;
                StopTicking();
                HideInternal();
                return;
            }

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _hideCompletion = tcs;
            StartTicking();
            hidden = tcs.Task;
        }

        await Task.WhenAny(hidden, Task.Delay(timeoutMs));
    }

    /// <summary>截图结束：以弹簧展开动画复位。</summary>
    public void OnCaptureEnded()
    {
        lock (_lock)
        {
            _hiddenByCapture = false;
            if (_enabled && _seeded)
            {
                _reveal = 0f;
                _revealVelocity = 0f;
                _revealTarget = 1f;
                StartTicking();
            }
        }
    }

    public void UpdateMetrics(HardwareMetrics metrics)
    {
        lock (_lock)
        {
            _lastMetrics = metrics;
            if (!_seeded)
            {
                _display = OsdRenderer.Metrics.From(metrics);
                _seeded = true;
            }

            if (!_enabled || _hiddenByCapture)
            {
                return;
            }

            _revealTarget = 1f;
            StartTicking();
        }
    }

    #endregion

    #region 动效驱动

    private void StartTicking()
    {
        if (_frameTimer == null)
        {
            _reveal = _revealTarget;
            _revealVelocity = 0f;
            if (_revealTarget <= 0.5f)
            {
                HideInternal();
            }
            else
            {
                RenderFrame();
            }

            return;
        }

        if (_ticking)
        {
            return;
        }

        _lastTickMs = _clock.Elapsed.TotalMilliseconds;
        SetFrameInterval(FrameIntervalMs);
        _ticking = true;
        _frameTimer.Start();
    }

    private void StopTicking()
    {
        _ticking = false;
        _frameTimer?.Stop();
    }

    /// <summary>切换出帧间隔；差异小于 0.5ms 时忽略，避免每帧都写属性。</summary>
    private void SetFrameInterval(float ms)
    {
        if (_frameTimer == null)
        {
            return;
        }

        if (MathF.Abs((float)_frameTimer.Interval.TotalMilliseconds - ms) < 0.5f)
        {
            return;
        }

        _frameTimer.Interval = TimeSpan.FromMilliseconds(ms);
    }

    /// <summary>
    /// 只保留「会改变画面」的位数：百分比取整、显存/内存到 0.1G、速率按显示精度分档（见 <see cref="SpeedKey"/>）。
    /// 指数趋近过程中的亚单位变化因此不会触发重绘——采样是每秒一次，绝大多数帧画出来是一样的。
    /// </summary>
    private static (int Gpu, int Temp, int Vram, int Cpu, int Ghz, int Ram, int RamUsed, long Down, long Up, float Fps) RenderKey(in OsdRenderer.Metrics m)
        => (
            (int)MathF.Round(m.Gpu),
            (int)MathF.Round(m.Temp),
            (int)MathF.Round(m.VramGb * 10f),
            (int)MathF.Round(m.Cpu),
            (int)MathF.Round(m.Ghz * 100f),
            (int)MathF.Round(m.Ram),
            (int)MathF.Round(m.RamUsedGb * 10f),
            SpeedKey(m.Down),
            SpeedKey(m.Up),
            MathF.Round(m.Fps));

    /// <summary>
    /// 速率的显示精度：低于 1KB 按字节、低于 1MB 按 KB、再往上按 0.1MB / 0.1GB。
    /// 分档与 OsdRenderer.FormatSpeed 的输出精度一致，档内变化不会改变任何一个字。
    /// </summary>
    private static long SpeedKey(float bytesPerSec)
    {
        var value = (long)MathF.Max(0f, bytesPerSec);
        if (value < 1024)
        {
            return value;
        }

        if (value < 1024L * 1024)
        {
            return 1_000_000 + (value / 1024);
        }

        if (value < 1024L * 1024 * 1024)
        {
            return 2_000_000 + (value / (1024L * 102));
        }

        return 3_000_000 + (value / (1024L * 1024 * 1024 / 10));
    }

    private void OnFrame()
    {
        bool render;
        lock (_lock)
        {
            double now = _clock.Elapsed.TotalMilliseconds;
            float dt = (float)Math.Clamp((now - _lastTickMs) / 1000.0, 0.001, 0.05);
            _lastTickMs = now;

            bool showing = _revealTarget > 0.5f;
            // 出场比入场更「果断」：收起要像缩回屏幕边缘，不能拖泥带水
            OverlayGdi.SpringStep(ref _reveal, ref _revealVelocity, _revealTarget, dt,
                showing ? 300f : 620f,
                showing ? 21f : 40f);

            bool valuesMoving = false;
            if (_seeded && _lastMetrics is { } m)
            {
                valuesMoving |= OverlayGdi.Approach(ref _display.Gpu, m.GpuUsagePercent, dt, 7.5f);
                valuesMoving |= OverlayGdi.Approach(ref _display.Temp, m.GpuTemperature, dt, 4.5f);
                valuesMoving |= OverlayGdi.Approach(ref _display.VramGb, (float)(m.GpuMemoryUsedBytes / (1024.0 * 1024.0 * 1024.0)), dt, 6f);
                valuesMoving |= OverlayGdi.Approach(ref _display.Cpu, m.CpuUsagePercent, dt, 7.5f);
                valuesMoving |= OverlayGdi.Approach(ref _display.Ghz, m.CpuFrequencyGhz, dt, 4f);
                valuesMoving |= OverlayGdi.Approach(ref _display.Ram, m.RamUsagePercent, dt, 7.5f);
                valuesMoving |= OverlayGdi.Approach(ref _display.RamUsedGb, (float)(m.RamUsedBytes / (1024.0 * 1024.0 * 1024.0)), dt, 6f);
                valuesMoving |= OverlayGdi.Approach(ref _display.Down, m.NetDownloadSpeedBytesPerSec, dt, 9f);
                valuesMoving |= OverlayGdi.Approach(ref _display.Up, m.NetUploadSpeedBytesPerSec, dt, 9f);
            }

            // 收尾时贴近 0 就直接判定「已退出视野」：不必等弹簧完全静止，
            // 否则截图前的退场会白白多等一百多毫秒。
            if (!showing && _reveal <= 0.008f)
            {
                _reveal = 0f;
                _revealVelocity = 0f;
                StopTicking();
                HideInternal();
                return;
            }

            bool settled = MathF.Abs(_reveal - _revealTarget) <= 0.004f && MathF.Abs(_revealVelocity) <= 0.05f;
            if (settled)
            {
                _reveal = _revealTarget;
                _revealVelocity = 0f;
            }

            // 形体收敛后不能立刻停表：采样值每秒才来一次，还要靠出帧把数字追上目标。
            // 所以先判断要不要画，再决定是否停表（原先这里提前 return，导致 OSD 数值永远停在入场那一帧）。
            // 只有「画出来的东西真的变了」才重绘：指数趋近每帧只挪一点点，大多数帧四舍五入后的数字是一样的。
            var key = RenderKey(_display);
            render = !settled || key != _renderKey;
            if (render)
            {
                _renderKey = key;
            }

            // 形体还在动时保持 60fps；只剩数值追赶时降到 ~30fps，肉眼依旧连贯但省一半绘制。
            SetFrameInterval(settled ? ValueFrameIntervalMs : FrameIntervalMs);

            if (settled && !valuesMoving)
            {
                StopTicking();
            }
        }

        // GDI+ 绘制放在 _lock 外：整帧渲染耗时（约几 ms）会阻塞同锁的 UpdateMetrics/TryEnqueue 与设置页读取。
        // 上面已在锁内完成全部动画推进与「要不要画」的判定，这里只消费快照语义的字段。
        if (render)
        {
            RenderFrame();
        }
    }

    #endregion

    #region 渲染与定位

    private void RenderFrame()
    {
        // 用独立渲染锁串行化（OnFrame 已在 _lock 外调用这里，SetPosition/SetTargetDisplay 在持有 _lock 时调用）。
        // 该锁内不再获取 _lock，两把锁不会嵌套成环。
        lock (_renderLock)
        {
            if (_hwnd == IntPtr.Zero || !_seeded)
            {
                return;
            }

            DisplayArea area;
            if (string.Equals(_targetDisplay, "FollowCursor", StringComparison.OrdinalIgnoreCase)
                && NativeMethods.GetCursorPos(out var pt))
            {
                area = DisplayArea.GetFromPoint(new PointInt32(pt.X, pt.Y), DisplayAreaFallback.Nearest);
            }
            else
            {
                area = DisplayArea.Primary;
            }

            float scale = 1f;
            var monitorHandle = Win32Interop.GetMonitorFromDisplayId(area.DisplayId);
            if (monitorHandle != IntPtr.Zero
                && NativeMethods.GetDpiForMonitor(monitorHandle, 0, out var dpiX, out _) == 0
                && dpiX >= 96)
            {
                scale = dpiX / 96f;
            }

            bool topRight = !string.Equals(_position, "TopLeft", StringComparison.OrdinalIgnoreCase);
            var layout = OsdRenderer.Layout(scale, topRight, _display);
            if (layout.BufferWidth <= 0 || layout.BufferHeight <= 0)
            {
                return;
            }

            if (!_canvas.Ensure(layout.BufferWidth, layout.BufferHeight))
            {
                return;
            }

            var frame = layout with { Reveal = _reveal };
            OsdRenderer.Draw(_canvas.Graphics, frame, _display);

            var bounds = area.OuterBounds;
            int targetX = topRight ? bounds.X + bounds.Width - layout.BufferWidth : bounds.X;
            int targetY = bounds.Y;

            if (!_canvas.Present(_hwnd, targetX, targetY))
            {
                return;
            }

            if (!IsVisible)
            {
                NativeMethods.ShowWindow(_hwnd, NativeMethods.SwShowNoActivate);
                IsVisible = true;
            }
        }
    }

    private void HideInternal()
    {
        if (_hwnd != IntPtr.Zero && IsVisible)
        {
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SwHide);
        }

        IsVisible = false;
        _hideCompletion?.TrySetResult();
        _hideCompletion = null;
    }

    #endregion

    public void Dispose()
    {
        lock (_lock)
        {
            StopTicking();
            HideInternal();
        }

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
        }

        _canvas.Dispose();
        if (_gdiplusToken != 0)
        {
            OverlayGdi.GdiplusShutdown(_gdiplusToken);
        }
    }
}
