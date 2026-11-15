using System.Runtime.InteropServices;

namespace Jietu;

/// <summary>
/// 液态玻璃的实时取景器：抓「OSD 背后那一块桌面」，模糊 + 折射成卡面图像。
/// 玻璃是重度磨砂，背后画面的细节本来就被抹平了，所以只在几何变化或隔一段时间才重算；
/// 展开动画中间那几帧尺寸变化，直接把上一张拉伸上去（渲染器那边按卡片矩形缩放画）。
/// 取景前必须给 OSD 打上 WDA_EXCLUDEFROMCAPTURE，否则会把卡面自己抓进背景里，形成回授。
/// </summary>
internal sealed class ScreenGlass : IDisposable
{
    /// <summary>模糊源先缩小一半再算：重磨砂不差这点细节，成本却掉到 1/4。</summary>
    private const int DownSample = 2;

    private IntPtr _memDc = IntPtr.Zero;
    private IntPtr _dib = IntPtr.Zero;
    private IntPtr _oldBmp = IntPtr.Zero;
    private IntPtr _bits = IntPtr.Zero;
    private int _regionW;
    private int _regionH;

    private readonly LayeredCanvas _canvas = new();
    private byte[] _src = [];
    private byte[] _small = [];
    private byte[] _dst = [];
    private int[] _signature = [];

    public IntPtr Image => _canvas.Image;

    /// <summary>这一次取景的结果。Unchanged 表示背后画面没动，直接沿用上一张，连模糊都不算。</summary>
    public enum Result
    {
        Failed,
        Unchanged,
        Updated,
    }

    /// <summary>抓一次背景并合成玻璃。</summary>
    public Result Compose(int screenX, int screenY, int cardW, int cardH, float lensPx, float blurPx, float tint,
        float radiusPx)
    {
        return Compose(screenX, screenY, cardW, cardH, lensPx, blurPx, tint, radiusPx, 1f);
    }

    /// <summary>
    /// 抓一次背景并合成玻璃；<paramref name="pxScale"/> 是 DPI 缩放（用于折算亮边宽度）。
    /// </summary>
    /// <param name="topRounded">
    /// 顶边要不要圆角：OSD 贴着屏幕上沿，顶角看不见（false）；浮在桌面中间的菜单必须 true，
    /// 否则两个顶角会留下一块方的白角。
    /// </param>
    public Result Compose(int screenX, int screenY, int cardW, int cardH, float lensPx, float blurPx, float tint,
        float radiusPx, float pxScale, bool topRounded = false, float edgeGlow = 1f)
    {
        int margin = (int)MathF.Ceiling(lensPx * 1.7f);

        // 四边余量都要按屏幕边界收缩：卡片贴到任何一条屏幕边上时，越界的部分 BitBlt
        // 抓回来是未定义内容（通常全黑），折射取样会把黑边吃进玻璃边缘。
        // 卡片在抓回来的区域里的位置同步跟着偏移，这样取样坐标不会错位。
        // 卡片本体永远完整落在屏幕内（它就在屏幕上显示着），只有余量会被裁。
        int padLeft = Math.Min(margin, Math.Max(0, screenX));
        int padTop = Math.Min(margin, Math.Max(0, screenY));
        GetVirtualScreenBounds(out var virtualBounds);
        int padRight = Math.Min(margin, Math.Max(0, virtualBounds.Right - screenX - cardW));
        int padBottom = Math.Min(margin, Math.Max(0, virtualBounds.Bottom - screenY - cardH));
        int regionW = cardW + padLeft + padRight;
        int regionH = cardH + padTop + padBottom;
        if (cardW < 8 || cardH < 4 || !EnsureRegion(regionW, regionH))
        {
            return Result.Failed;
        }

        var screen = NativeMethods.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero)
        {
            return Result.Failed;
        }

        try
        {
            // 只抓卡片周边一小块，成本与窗口大小无关；CAPTUREBLT 不用，纯 SRCCOPY 最快
            if (!NativeMethods.BitBlt(_memDc, 0, 0, regionW, regionH, screen,
                    screenX - padLeft, screenY - padTop, NativeMethods.SrcCopy))
            {
                return Result.Failed;
            }
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screen);
        }

        Marshal.Copy(_bits, _src, 0, regionW * regionH * 4);

        // 锁屏 / UAC / 受保护内容下 BitBlt 会成功但整块全黑，这时宁可用上一张也别糊一块黑玻璃
        if (IsMostlyBlank(_src, regionW * regionH))
        {
            return Result.Failed;
        }

        // 背后画面没动就别重算：玻璃是重磨砂，一两级色差根本看不出来，
        // 而一次合成要十几毫秒，省下来的是常驻 CPU
        if (!HasChanged(regionW * regionH))
        {
            return Result.Unchanged;
        }

        // 全分辨率合成：半分辨率那条路径（srcScale=2 + 拉伸）虽然省一半时间，
        // 但会把 1px 的镜面亮边插值成 2px 粗白线，正是之前被否掉的那种底边白线。省时间靠下面的变化检测。
        int need = cardW * cardH * 4;
        if (_dst.Length < need)
        {
            _dst = new byte[need];
        }

        var card = new GlassSurface.Card(padLeft, padTop, cardW, cardH, radiusPx, topRounded, edgeGlow);
        GlassSurface.Compose(_src, regionW, regionH, card, lensPx, blurPx, tint, _dst, _small, DownSample,
            pxScale: pxScale);
        GlassSurface.Upload(_dst, cardW, cardH, _canvas);
        return Result.Updated;    }

    /// <summary>按稀疏采样比对背后的画面有没有变。变动超过「肉眼看不出」的门限才重算。</summary>
    private bool HasChanged(int pixels)
    {
        int step = Math.Max(1, pixels / 1024);
        if (_signature.Length == 0)
        {
            _signature = new int[1024];
            for (int i = 0; i < 1024; i++)
            {
                int o = Math.Min(i * step, pixels - 1) * 4;
                _signature[i] = (_src[o] << 16) | (_src[o + 1] << 8) | _src[o + 2];
            }

            return true;
        }

        int changed = 0;
        for (int i = 0; i < 1024; i++)
        {
            int o = Math.Min(i * step, pixels - 1) * 4;
            int now = (_src[o] << 16) | (_src[o + 1] << 8) | _src[o + 2];
            int diff = Math.Abs((now >> 16 & 0xFF) - (_signature[i] >> 16 & 0xFF))
                + Math.Abs((now >> 8 & 0xFF) - (_signature[i] >> 8 & 0xFF))
                + Math.Abs((now & 0xFF) - (_signature[i] & 0xFF));
            if (diff > 14)
            {
                changed++;
            }

            _signature[i] = now;
        }

        // 一成以上的采样点都变了才认为背景真的动了（整块换壁纸/窗口移动会远超这个比例）
        return changed > 100;
    }

    private bool EnsureRegion(int width, int height)
    {
        if (_memDc != IntPtr.Zero && width == _regionW && height == _regionH)
        {
            return true;
        }

        ReleaseRegion();

        var screen = NativeMethods.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            _memDc = NativeMethods.CreateCompatibleDC(screen);
            var bmi = new NativeMethods.BitmapInfo
            {
                Header = new NativeMethods.BitmapInfoHeader
                {
                    BiSize = 40,
                    BiWidth = width,
                    BiHeight = -height,   // Top-down，和 GlassSurface 的扫描线方向一致
                    BiPlanes = 1,
                    BiBitCount = 32,
                    BiCompression = NativeMethods.BiRgb,
                },
            };

            _dib = NativeMethods.CreateDIBSection(screen, ref bmi, 0, out _bits, IntPtr.Zero, 0);
            if (_dib == IntPtr.Zero || _bits == IntPtr.Zero)
            {
                ReleaseRegion();
                return false;
            }

            _oldBmp = NativeMethods.SelectObject(_memDc, _dib);
            _regionW = width;
            _regionH = height;
            _src = new byte[width * height * 4];
            _small = new byte[Math.Max(1, width / DownSample) * Math.Max(1, height / DownSample) * 4];
            return true;
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    /// <summary>整块虚拟桌面（多显示器并起来的范围）。BitBlt 只有越过这个范围才是未定义内容。</summary>
    private static void GetVirtualScreenBounds(out NativeMethods.Rect bounds)
    {
        int x = NativeMethods.GetSystemMetrics(SmXVirtualScreen);
        int y = NativeMethods.GetSystemMetrics(SmYVirtualScreen);
        int w = NativeMethods.GetSystemMetrics(SmCxVirtualScreen);
        int h = NativeMethods.GetSystemMetrics(SmCyVirtualScreen);
        if (w <= 0 || h <= 0)
        {
            // 拿不到虚拟屏就按不收缩口径处理，等同于旧行为
            bounds = new NativeMethods.Rect
            {
                Left = int.MinValue,
                Top = int.MinValue,
                Right = int.MaxValue,
                Bottom = int.MaxValue,
            };
            return;
        }

        bounds = new NativeMethods.Rect { Left = x, Top = y, Right = x + w, Bottom = y + h };
    }

    private static bool IsMostlyBlank(byte[] pixels, int count)
    {
        // 每 64 个像素抽一个采样，整块接近纯黑（BitBlt 在锁屏 / UAC / 受保护内容下会成功
        // 但返回全 0）才判定为受保护画面。阈值压到 2：纯黑壁纸（各通道均值不到 2）是真实
        // 存在的用户配置，不能因为「黑」就被误伤——反正抓到全黑玻璃也不至于崩，只是白板兜底。
        long sum = 0;
        int sampled = 0;
        for (int i = 0; i < count; i += 64)
        {
            int o = i * 4;
            sum += pixels[o] + pixels[o + 1] + pixels[o + 2];
            sampled++;
        }

        return sampled > 0 && sum / (sampled * 3L) < 2;
    }

    private void ReleaseRegion()
    {
        if (_memDc != IntPtr.Zero && _oldBmp != IntPtr.Zero)
        {
            NativeMethods.SelectObject(_memDc, _oldBmp);
        }

        if (_dib != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_dib);
        }

        if (_memDc != IntPtr.Zero)
        {
            NativeMethods.DeleteDC(_memDc);
        }

        _memDc = IntPtr.Zero;
        _dib = IntPtr.Zero;
        _oldBmp = IntPtr.Zero;
        _bits = IntPtr.Zero;
        _regionW = 0;
        _regionH = 0;
    }

    public void Dispose()
    {
        ReleaseRegion();
        _canvas.Dispose();
    }
}
