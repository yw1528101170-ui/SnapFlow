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

    /// <summary>
    /// 顶部居中停靠时的「待机轮询」间隔：静止后仍以 20Hz 调一次 GetCursorPos 判断悬停，
    /// 否则帧循环一停表就再也没有机会发现鼠标上来了（穿透窗口收不到任何鼠标消息）。
    /// 50ms 与 dt 上限 0.05s 对齐，滞回时长不会因为停表被拉长。
    /// </summary>
    private const float HoverPollIntervalMs = 50f;

    /// <summary>顶部居中：鼠标在胶囊上停留多久才展开（避免扫过顶部就弹开）。</summary>
    private const float HoverExpandSeconds = 0.12f;

    /// <summary>鼠标离开多久才收回紧凑态（留一点容错，展开途中抖动不会立刻收起）。</summary>
    private const float HoverCollapseSeconds = 0.5f;

    /// <summary>命中判定在卡片外侧的容差，让 22dip 的小胶囊也好瞄准。</summary>
    private const int HoverInflatePx = 8;

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

    // 顶部居中的「紧凑态 ↔ 详细态」：展开度由弹簧驱动，悬停时长用滞回判定
    private float _expand = 1f;
    private float _expandVelocity;
    private float _expandTarget = 1f;
    private float _hoverSeconds;
    private float _awaySeconds;

    /// <summary>首次出现时的「自动展开」剩余时长：让用户看见一次详细版，之后缩回紧凑态。</summary>
    private float _hintSeconds;

    // 最近一帧卡片的屏幕矩形（悬停命中用；分层窗口是穿透的，收不到鼠标消息，只能轮询光标）
    private int _cardX;
    private int _cardY;
    private int _cardW;
    private int _cardH;
    private bool _cardValid;

    // 液态玻璃：卡面来自「OSD 背后那块桌面」的实时取景。玻璃是重磨砂，不需要高刷，
    // 按 GlassRefreshMs 间隔重抓即可，中间几帧直接拉伸上一张。
    // 背景变化不用我们操心节流：ScreenGlass 内部有采样签名比对，画面没变就直接返回 Unchanged，
    // 真正的磨砂合成只在背后画面真的动了才跑。
    private const long GlassRefreshMs = 140;

    private ScreenGlass? _glass;
    private long _glassAt;
    private int _glassFailedStreak;
    private bool _glassAllowed;
    private long _dumpedAt;

    /// <summary>调试用：给了路径就把每帧画布存成 PNG（OSD 不进任何截图，只能自己导出）。</summary>
    private static readonly string DumpPath = Environment.GetEnvironmentVariable("SNAPTEST_DUMPOSD") ?? string.Empty;

    /// <summary>抓屏取景能不能用：SetWindowDisplayAffinity 失败时禁用玻璃，否则会把卡面自己抓进去形成回授。</summary>
    public bool GlassEnabled => _glassAllowed;

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

        // 液态玻璃的前置条件：OSD 必须从「抓屏」里消失。
        // 一是抓背景时不会把自己的卡面抓进去（否则模糊后面就是一块自己的影子，无限回授），
        // 二是顺便让 OSD 不出现在本工具的截图和录屏里——它本来就不该入镜。
        _glassAllowed = _hwnd != IntPtr.Zero
            && NativeMethods.SetWindowDisplayAffinity(_hwnd, NativeMethods.WdaExcludeFromCapture);

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
                ResetExpand();
                if (_reveal <= 0.01f)
                {
                    _revealVelocity = 0f;
                }

                StartTicking();
            }
        }
    }

    /// <summary>
    /// 展开度归位：常态是紧凑态。首次出现先展开一小段当提示（让用户知道这里能展开），
    /// 之后落到紧凑态，等鼠标悬停再展开。
    /// </summary>
    private void ResetExpand()
    {
        _hoverSeconds = 0f;
        _awaySeconds = 0f;
        _hintSeconds = 1.6f;
        _expandTarget = IsVisible ? 0f : 1f;
        if (!IsVisible)
        {
            _expand = _expandTarget;
            _expandVelocity = 0f;
        }
    }

    public void SetTargetDisplay(string targetDisplay)
    {
        bool needsRender;
        lock (_lock)
        {
            _targetDisplay = string.Equals(targetDisplay, "FollowCursor", StringComparison.OrdinalIgnoreCase) ? "FollowCursor" : "Primary";
            needsRender = IsVisible && _seeded;
        }

        // 与 OnFrame 相同的纪律：整帧渲染（几 ms）不要在持有 _lock 时做，
        // 否则会阻塞 UpdateMetrics 入队与设置页读取。
        if (needsRender)
        {
            RenderFrame();
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
            // 截图退场时不保留展开态：回来时从常态（紧凑）重新开始
            _expandTarget = 0f;
            _hoverSeconds = 0f;
            _awaySeconds = 0f;
            _hintSeconds = 0f;
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

    /// <summary>截图结束：以弹簧展开动画复位（顶部居中从紧凑态重新长出来）。</summary>
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
                _expandTarget = 0f;
                _expand = _expandTarget;
                _expandVelocity = 0f;
                _hintSeconds = 0f;
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
            _expand = _expandTarget;
            _expandVelocity = 0f;
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
    /// 只保留「会改变画面」的位数：百分比取整、显存/内存到 0.1G、速率按显示精度分档（见 <see cref="OsdRenderer.SpeedTextKey"/>）。
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
            OsdRenderer.SpeedTextKey(m.Down),
            OsdRenderer.SpeedTextKey(m.Up),
            MathF.Round(m.Fps));

    /// <summary>
    /// 速率的显示精度分档走 <see cref="OsdRenderer.SpeedTextKey"/>：整除截断和 FormatSpeed 的
    /// 四舍五入在档边界上会错位（例如 1.5K/s 附近），键没变、字已经变了，画面就会滞留旧值。
    /// </summary>

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

            UpdateHoverAndExpand(dt);

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

            bool expandSettled = MathF.Abs(_expand - _expandTarget) <= 0.004f && MathF.Abs(_expandVelocity) <= 0.05f;
            if (expandSettled)
            {
                _expand = _expandTarget;
                _expandVelocity = 0f;
            }

            // 形体收敛后不能立刻停表：采样值每秒才来一次，还要靠出帧把数字追上目标。
            // 所以先判断要不要画，再决定是否停表（原先这里提前 return，导致 OSD 数值永远停在入场那一帧）。
            // 只有「画出来的东西真的变了」才重绘：指数趋近每帧只挪一点点，大多数帧四舍五入后的数字是一样的。
            var key = RenderKey(_display);
            render = !settled || !expandSettled || key != _renderKey;
            if (render)
            {
                _renderKey = key;
            }

            // 没有「真正静止」：待机也要留着低频轮询，否则悬停永远触发不了
            bool hoverWatching = _enabled && !_hiddenByCapture;

            // 形体或数值还在动 → 60fps；都静止时待机只要 20Hz 轮询就够
            SetFrameInterval(settled && expandSettled && !valuesMoving
                ? (hoverWatching ? HoverPollIntervalMs : ValueFrameIntervalMs)
                : FrameIntervalMs);

            if (settled && expandSettled && !valuesMoving && !hoverWatching)
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

            var layout = OsdRenderer.Layout(scale, _expand, _display);
            if (layout.BufferWidth <= 0 || layout.BufferHeight <= 0)
            {
                return;
            }

            if (!_canvas.EnsureAtLeast(layout.BufferWidth, layout.BufferHeight))
            {
                return;
            }

            var frame = layout with { Reveal = _reveal };

            var bounds = area.OuterBounds;
            // 画布两侧留白对称，窗口整体贴着屏幕顶部、按屏幕中线对齐
            int targetX = bounds.X + (bounds.Width - layout.BufferWidth) / 2;
            int targetY = bounds.Y;

            // 记录卡片的屏幕矩形，供悬停命中（穿透窗口收不到鼠标消息，只能自己算）
            var (cardOffsetX, cardWidth) = OsdRenderer.CardHorizontal(layout);
            _cardX = targetX + (int)MathF.Round(cardOffsetX);
            _cardY = targetY;
            _cardW = (int)MathF.Round(cardWidth);
            _cardH = (int)MathF.Round(layout.CardHeight);
            _cardValid = true;

            var glassImage = UpdateGlass(scale);

            OsdRenderer.Draw(_canvas.Graphics, frame, _display, glassImage);

            DumpCanvasOnce();

            if (!_canvas.Present(_hwnd, targetX, targetY, layout.BufferHeight))
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

    /// <summary>
    /// 调试用：OSD 因为 WDA_EXCLUDEFROMCAPTURE 不会出现在任何截图里，想看真实渲染结果
    /// 只能由窗口自己把这一帧画布存成 PNG。SNAPTEST_DUMPOSD=<路径> 时存一次。
    /// </summary>
    private void DumpCanvasOnce()
    {
        if (DumpPath.Length == 0)
        {
            return;
        }

        long now = Environment.TickCount64;
        if (now - _dumpedAt < 500)
        {
            return;
        }

        _dumpedAt = now;
        var encoder = new Guid("557CF406-1A04-11D3-9A73-0000F81EF32E");
        if (_canvas.Image != IntPtr.Zero)
        {
            OverlayGdi.GdipSaveImageToFile(_canvas.Image, DumpPath, ref encoder, IntPtr.Zero);
        }
    }

    /// <summary>
    /// 抓一次 OSD 背后的桌面、合成液态玻璃卡面，返回本帧要画的玻璃图像（IntPtr.Zero 表示没有）。
    /// 玻璃是重磨砂，背后画面的细节早被抹平，按 GlassRefreshMs 间隔重抓即可；
    /// 展开动画中间那几帧高度变化直接用上一张拉伸（渲染器按卡片矩形缩放画）。
    /// 背景有没有变由 ScreenGlass 的签名比对决定——没变就是一次廉价的 BitBlt + 比对。
    /// </summary>
    private IntPtr UpdateGlass(float scale)
    {
        if (!_glassAllowed || _reveal <= 0.01f || _cardW < 8)
        {
            return IntPtr.Zero;
        }

        _glass ??= new ScreenGlass();

        long now = Environment.TickCount64;
        if (now - _glassAt >= GlassRefreshMs)
        {
            var result = _glass.Compose(_cardX, _cardY, _cardW, _cardH,
                GlassSurface.LensWidth(scale), GlassSurface.BlurRadius(scale, _cardH), 0.42f,
                OsdRenderer.BottomRadius(_cardH), scale);
            if (result != ScreenGlass.Result.Failed)
            {
                _glassAt = now;
                _glassFailedStreak = 0;
            }
            else
            {
                // 失败（锁屏 / UAC / 纯黑壁纸全黑判定）也要推进时间基点并指数退避：
                // 不推进的话动画期间每个渲染帧都会重试一次 BitBlt + 全区域拷贝 + 全黑扫描。
                _glassFailedStreak = Math.Min(_glassFailedStreak + 1, 6);
                _glassAt = now - GlassRefreshMs + (GlassRefreshMs << _glassFailedStreak);
            }
        }

        return _glass.Image;
    }

    private void HideInternal()
    {
        if (_hwnd != IntPtr.Zero && IsVisible)
        {
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SwHide);
        }

        IsVisible = false;
        _cardValid = false;
        _hideCompletion?.TrySetResult();
        _hideCompletion = null;
    }

    /// <summary>
    /// 悬停判定与展开弹簧。
    /// 窗口带 WS_EX_TRANSPARENT（点击穿透），收不到 WM_MOUSEMOVE，所以挂在帧循环里轮询光标；
    /// 进入要停留 120ms（避免扫过屏幕顶部就弹开），离开留 500ms 容错（展开途中手抖一下不会立刻收回）。
    /// </summary>
    private void UpdateHoverAndExpand(float dt)
    {
        if (_enabled && _cardValid)
        {
            bool inside = false;
            if (NativeMethods.GetCursorPos(out var pt))
            {
                inside = pt.X >= _cardX - HoverInflatePx
                    && pt.X <= _cardX + _cardW + HoverInflatePx
                    && pt.Y >= _cardY - HoverInflatePx
                    && pt.Y <= _cardY + _cardH + HoverInflatePx;
            }

            if (inside)
            {
                _hoverSeconds += dt;
                _awaySeconds = 0f;
            }
            else
            {
                _hoverSeconds = 0f;
                _awaySeconds += dt;
            }

            if (_hintSeconds > 0f)
            {
                // 首次出现的自动展开：这段时间不收回去，鼠标上来就顺势继续展开
                _hintSeconds = MathF.Max(0f, _hintSeconds - dt);
                _expandTarget = 1f;
            }
            else
            {
                if (_expandTarget < 1f && _hoverSeconds >= HoverExpandSeconds)
                {
                    _expandTarget = 1f;
                }

                if (_expandTarget > 0f && _awaySeconds >= HoverCollapseSeconds)
                {
                    _expandTarget = 0f;
                }
            }
        }

        // 展开比收起更轻快：展开 300 刚度轻微过冲，收起 420 稍快收干，形态不会「软塌」
        OverlayGdi.SpringStep(ref _expand, ref _expandVelocity, _expandTarget, dt,
            _expandTarget > _expand ? 300f : 420f,
            _expandTarget > _expand ? 22f : 30f);
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
        _glass?.Dispose();
        _glass = null;
        if (_gdiplusToken != 0)
        {
            OverlayGdi.GdiplusShutdown(_gdiplusToken);
        }
    }
}
