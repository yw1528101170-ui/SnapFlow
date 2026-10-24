using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Jietu;

/// <summary>
/// 帧率采集。
/// Windows 没有「免注入读取游戏渲染帧率」的公开接口（PresentMon 走 ETW 需要管理员与内核会话），
/// 因此这里用合成器的呈现计数（<c>DwmGetCompositionTimingInfo</c> 的 <c>cFramesDisplayed</c>
/// ——已显示的唯一帧数）做每秒差分，得到「屏幕实际刷新出来的帧数」。
/// 注意 DWM 文档：cFramesDisplayed 要在第二次调用后才有效，构造函数里的首次调用正是为此预热。
/// 部分环境（远程会话、受限令牌）该接口不可用，此时降级为显示器的刷新率，
/// 并在界面上如实标注为 HZ 而不是 FPS。
/// </summary>
internal sealed class FrameRateMeter
{
    private readonly bool _compositionAvailable;
    private ulong _lastPresented;
    private long _lastTimestamp;
    private float _lastFps;

    public FrameRateMeter()
    {
        RefreshHz = QueryRefreshHz();
        _compositionAvailable = TryReadPresented(out _lastPresented);
        _lastTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>显示器刷新率（主屏），永远可用。</summary>
    public int RefreshHz { get; }

    /// <summary>是否能读到合成器帧计数（决定 UI 显示 FPS 还是 HZ）。</summary>
    public bool IsMeasured => _compositionAvailable;

    /// <summary>取一次采样：返回自上次调用以来的平均帧率。</summary>
    public float Sample()
    {
        if (!_compositionAvailable)
        {
            return RefreshHz;
        }

        long now = Stopwatch.GetTimestamp();
        double seconds = (now - _lastTimestamp) / (double)Stopwatch.Frequency;
        _lastTimestamp = now;

        if (!TryReadPresented(out var presented))
        {
            return _lastFps;
        }

        ulong delta = presented >= _lastPresented ? presented - _lastPresented : 0;
        _lastPresented = presented;

        if (seconds < 0.2 || seconds > 5.0)
        {
            return _lastFps;
        }

        // 合成器每秒可能多呈现 1~2 帧，这里做一次轻微钳制，避免出现 61/62 这类抖值
        _lastFps = (float)Math.Clamp(delta / seconds, 0.0, RefreshHz * 2.0);
        return _lastFps;
    }

    private static bool TryReadPresented(out ulong presented)
    {
        presented = 0;
        var info = new TimingInfo { CbSize = (uint)Marshal.SizeOf<TimingInfo>() };
        if (DwmGetCompositionTimingInfo(IntPtr.Zero, ref info) != 0)
        {
            return false;
        }

        presented = info.CFramesDisplayed;
        return presented > 0;
    }

    private static int QueryRefreshHz()
    {
        var dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero)
        {
            return 60;
        }

        try
        {
            int hz = GetDeviceCaps(dc, VRefresh);
            return hz is >= 24 and <= 500 ? hz : 60;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
    }

    #region Win32

    private const int VRefresh = 116;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Ratio
    {
        public uint Numerator;
        public uint Denominator;
    }

    /// <summary>
    /// DWM_TIMING_INFO（dwmapi.h）逐字段还原，UNSIGNED_RATIO={UINT32 分子,UINT32 分母}（8 字节），
    /// QPC_TIME/DWM_FRAME_COUNT 均为 ULONGLONG。默认 Pack(8) 下自然对齐即真实布局，共 <b>320 字节</b>，
    /// cbSize 必须填 320，否则 DwmGetCompositionTimingInfo 返回 E_INVALIDARG。
    /// （旧定义只有 240 字节且从 rateRefresh 起错位，cbSize 校验恒失败，导致 FPS 永远降级为 HZ。）
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct TimingInfo
    {
        public uint CbSize;                  // @0
        public Ratio RateRefresh;            // @4（UNSIGNED_RATIO 对齐 4，紧随 cbSize；qpcRefreshPeriod 起补齐 8 字节对齐 @16）
        public ulong QpcRefreshPeriod;       // @16
        public Ratio RateCompose;            // @24
        public ulong QpcVBlank;              // @32
        public ulong CRefresh;               // @40
        public uint CDxRefresh;              // @48
        public ulong QpcCompose;             // @56
        public ulong CFrame;                 // @64
        public uint CDxPresent;              // @72
        public ulong CRefreshFrame;          // @80
        public ulong CFrameSubmitted;        // @88
        public uint CDxPresentSubmitted;     // @96
        public ulong CFrameConfirmed;        // @104
        public uint CDxPresentConfirmed;     // @112
        public ulong CRefreshConfirmed;      // @120
        public uint CDxRefreshConfirmed;     // @128
        public ulong CFramesLate;            // @136
        public uint CFramesOutstanding;      // @144
        public ulong CFrameDisplayed;        // @152
        public ulong QpcFrameDisplayed;      // @160
        public ulong CRefreshFrameDisplayed; // @168
        public ulong CFrameComplete;         // @176
        public ulong QpcFrameComplete;       // @184
        public ulong CFramePending;          // @192
        public ulong QpcFramePending;        // @200
        public ulong CFramesDisplayed;       // @208 ← 已显示的唯一帧数（FPS 差分来源）
        public ulong CFramesComplete;        // @216
        public ulong CFramesPending;         // @224
        public ulong CFramesAvailable;       // @232
        public ulong CFramesDropped;         // @240
        public ulong CFramesMissed;          // @248
        public ulong CRefreshNextDisplayed;  // @256
        public ulong CRefreshNextPresented;  // @264
        public ulong CRefreshesDisplayed;    // @272
        public ulong CRefreshesPresented;    // @280
        public ulong CRefreshStarted;        // @288
        public ulong CPixelsReceived;        // @296
        public ulong CPixelsDrawn;           // @304
        public ulong CBuffersEmpty;          // @312
    }                                        // 共 320 字节

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, ref TimingInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr dc, int index);

    #endregion
}
