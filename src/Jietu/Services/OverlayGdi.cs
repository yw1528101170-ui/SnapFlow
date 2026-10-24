using System.Runtime.InteropServices;

namespace Jietu;

/// <summary>
/// 浮层自绘基础设施：统一 GDI+ 声明、Apple 风格绘制原语、分层窗口画布缓存与弹簧缓动。
/// OSD 与 Toast 共用这一层，避免两份重复的 P/Invoke 与逐帧创建 GDI+ 资源。
/// </summary>
internal static class OverlayGdi
{
    #region 常量

    internal const int PixelFormat32bppPARGB = 0x000E200B;
    internal const int SmoothingModeAntiAlias = 4;
    internal const int TextRenderingHintAntiAliasGridFit = 4;
    internal const int UnitPixel = 2;
    internal const int StringFormatNoWrap = 0x00001000;
    internal const int CombineModeIntersect = 1;
    internal const int WrapModeTile = 0;
    internal const int AlignNear = 0;
    internal const int AlignCenter = 1;
    internal const int AlignFar = 2;
    internal const int LineNear = 0;
    internal const int LineCenter = 1;
    internal const int MatrixOrderAppend = 1;
    internal const int LineCapRound = 2;
    internal const int LineJoinRound = 2;

    // Apple 系统色（暗色模式）
    internal const uint ApplePurple = 0xFFBF5AF2;
    internal const uint AppleGreen = 0xFF30D158;
    internal const uint AppleOrange = 0xFFFF9F0A;
    internal const uint AppleTeal = 0xFF5AC8FA;
    internal const uint AppleBlue = 0xFF0A84FF;
    internal const uint AppleYellow = 0xFFFFD60A;
    internal const uint AppleRed = 0xFFFF453A;
    internal const uint AppleMint = 0xFF66D4CF;

    #endregion

    #region 颜色与缓动

    /// <summary>按比例调整 alpha（0..1），保留原有 RGB。</summary>
    internal static uint WithAlpha(uint argb, float k)
    {
        float a = (argb >> 24) & 0xFF;
        uint na = (uint)Math.Clamp((int)MathF.Round(a * Math.Clamp(k, 0f, 1f)), 0, 255);
        return (argb & 0x00FFFFFFu) | (na << 24);
    }

    /// <summary>线性混合两种颜色（含 alpha），t=0 取 from。</summary>
    internal static uint Mix(uint from, uint to, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        uint a = LerpChannel(from >> 24, to >> 24, t);
        uint r = LerpChannel(from >> 16, to >> 16, t);
        uint g = LerpChannel(from >> 8, to >> 8, t);
        uint b = LerpChannel(from, to, t);
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    private static uint LerpChannel(uint from, uint to, float t)
    {
        float a = from & 0xFF;
        float b = to & 0xFF;
        return (uint)Math.Clamp((int)MathF.Round(a + (b - a) * t), 0, 255);
    }

    /// <summary>平滑到白色的比例，用于高光、提亮。</summary>
    internal static uint Lighten(uint argb, float t) => Mix(argb, 0xFFFFFFFFu, t);

    internal static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

    /// <summary>Smoothstep，让数值过渡的起止更柔和。</summary>
    internal static float Smooth01(float t)
    {
        t = Clamp01(t);
        return t * t * (3f - 2f * t);
    }

    #endregion

    #region 弹簧与缓动阻尼

    /// <summary>
    /// 半隐式欧拉积分的阻尼弹簧：stiffness 越大越快，damping 越小回弹越明显。
    /// ζ = damping / (2 * sqrt(stiffness))，0.6~0.8 接近 Apple 的「快出慢停 + 轻微回弹」。
    /// 内部按固定子步长积分，保证不同帧率下手感一致。
    /// </summary>
    internal static void SpringStep(ref float value, ref float velocity, float target, float dt,
        float stiffness, float damping)
    {
        if (dt <= 0f)
        {
            return;
        }

        int steps = Math.Clamp((int)MathF.Ceiling(dt * 240f), 1, 24);
        float h = dt / steps;
        for (int i = 0; i < steps; i++)
        {
            float accel = stiffness * (target - value) - damping * velocity;
            velocity += accel * h;
            value += velocity * h;
        }
    }

    /// <summary>指数趋近（无回弹），用于数值滚动。返回 true 表示仍在移动。</summary>
    internal static bool Approach(ref float value, float target, float dt, float speed)
    {
        float diff = target - value;
        float epsilon = MathF.Max(0.4f, MathF.Abs(target) * 0.0025f);
        if (MathF.Abs(diff) <= epsilon)
        {
            value = target;
            return false;
        }

        value += diff * (1f - MathF.Exp(-dt * speed));
        return true;
    }

    #endregion

    #region 路径

    internal static IntPtr BuildRoundRect(float x, float y, float w, float h, float r)
    {
        GdipCreatePath(0, out var path);
        float d = MathF.Min(r * 2f, MathF.Min(w, h));
        if (d <= 0f)
        {
            GdipAddPathLine(path, x, y, x + w, y);
            GdipAddPathLine(path, x + w, y, x + w, y + h);
            GdipAddPathLine(path, x + w, y + h, x, y + h);
            GdipAddPathLine(path, x, y + h, x, y);
            GdipClosePathFigure(path);
            return path;
        }

        GdipAddPathArc(path, x + w - d, y, d, d, 270, 90);
        GdipAddPathArc(path, x + w - d, y + h - d, d, d, 0, 90);
        GdipAddPathArc(path, x, y + h - d, d, d, 90, 90);
        GdipAddPathArc(path, x, y, d, d, 180, 90);
        GdipClosePathFigure(path);
        return path;
    }

    /// <summary>
    /// 超椭圆（squircle）路径：用三次贝塞尔替代纯圆弧，得到 Apple 图标那种「连续曲率」转角，
    /// 比普通圆角在视觉上更饱满、更少转折感。
    /// </summary>
    internal static IntPtr BuildSquircle(float x, float y, float w, float h, float r)
    {
        float maxR = MathF.Min(w, h) * 0.5f;
        r = Math.Clamp(r, 0f, maxR);
        float k = r * 0.52f; // 控制点偏移；0.5523 为纯圆弧近似，0.52 让曲率更连续

        GdipCreatePath(0, out var path);
        GdipAddPathLine(path, x + r, y, x + w - r, y);
        GdipAddPathBezier(path, x + w - r, y, x + w - r + k, y, x + w, y + r - k, x + w, y + r);
        GdipAddPathLine(path, x + w, y + r, x + w, y + h - r);
        GdipAddPathBezier(path, x + w, y + h - r, x + w, y + h - r + k, x + w - r + k, y + h, x + w - r, y + h);
        GdipAddPathLine(path, x + w - r, y + h, x + r, y + h);
        GdipAddPathBezier(path, x + r, y + h, x + r - k, y + h, x, y + h - r + k, x, y + h - r);
        GdipAddPathLine(path, x, y + h - r, x, y + r);
        GdipAddPathBezier(path, x, y + r, x, y + r - k, x + r - k, y, x + r, y);
        GdipClosePathFigure(path);
        return path;
    }

    internal static IntPtr BuildEllipse(float cx, float cy, float r)
        => BuildEllipse(cx, cy, r, r);

    internal static IntPtr BuildEllipse(float cx, float cy, float rx, float ry)
    {
        GdipCreatePath(0, out var path);
        GdipAddPathEllipse(path, cx - rx, cy - ry, rx * 2f, ry * 2f);
        GdipClosePathFigure(path);
        return path;
    }

    #endregion

    #region 填充与描边

    internal static void FillPath(IntPtr g, IntPtr path, uint argb)
    {
        if (argb >> 24 == 0)
        {
            return;
        }

        if (GdipCreateSolidFill(argb, out var brush) == 0)
        {
            GdipFillPath(g, brush, path);
            GdipDeleteBrush(brush);
        }
    }

    /// <summary>垂直线性渐变填充（顶部 from → 底部 to），Apple 材质的高光/渐隐都靠它。</summary>
    internal static void FillPathVertical(IntPtr g, IntPtr path, uint from, uint to, float xRef, float yTop, float yBottom)
    {
        if (yBottom <= yTop)
        {
            FillPath(g, path, from);
            return;
        }

        var p1 = new PointF(xRef, yTop);
        var p2 = new PointF(xRef, yBottom);
        if (GdipCreateLineBrush(ref p1, ref p2, from, to, WrapModeTile, out var brush) == 0)
        {
            GdipFillPath(g, brush, path);
            GdipDeleteBrush(brush);
        }
    }

    internal static void StrokePath(IntPtr g, IntPtr path, uint argb, float width, bool round = false)
    {
        if (argb >> 24 == 0)
        {
            return;
        }

        if (GdipCreatePen1(argb, width, UnitPixel, out var pen) == 0)
        {
            if (round)
            {
                GdipSetPenLineCap197819(pen, LineCapRound, LineCapRound, 0);
                GdipSetPenLineJoin(pen, LineJoinRound);
            }

            GdipDrawPath(g, pen, path);
            GdipDeletePen(pen);
        }
    }

    internal static void DrawLine(IntPtr g, float x1, float y1, float x2, float y2, uint argb, float width, bool round = true)
    {
        if (GdipCreatePen1(argb, width, UnitPixel, out var pen) == 0)
        {
            if (round)
            {
                GdipSetPenLineCap197819(pen, LineCapRound, LineCapRound, 0);
            }

            GdipDrawLine(g, pen, x1, y1, x2, y2);
            GdipDeletePen(pen);
        }
    }

    /// <summary>折线（圆头圆角连接），用于勾选、感叹号等图标笔画。</summary>
    internal static void DrawPolyline(IntPtr g, uint argb, float width, ReadOnlySpan<(float X, float Y)> points)
    {
        if (points.Length < 2)
        {
            return;
        }

        GdipCreatePath(0, out var path);
        GdipAddPathLine(path, points[0].X, points[0].Y, points[1].X, points[1].Y);
        for (int i = 2; i < points.Length; i++)
        {
            GdipAddPathLine(path, points[i - 1].X, points[i - 1].Y, points[i].X, points[i].Y);
        }

        StrokePath(g, path, argb, width, round: true);
        GdipDeletePath(path);
    }

    internal static void DrawArc(IntPtr g, float cx, float cy, float radius, float startDeg, float sweepDeg,
        uint argb, float width, bool round = true)
    {
        if (GdipCreatePen1(argb, width, UnitPixel, out var pen) == 0)
        {
            if (round)
            {
                GdipSetPenLineCap197819(pen, LineCapRound, LineCapRound, 0);
            }

            GdipDrawArc(g, pen, cx - radius, cy - radius, radius * 2f, radius * 2f, startDeg, sweepDeg);
            GdipDeletePen(pen);
        }
    }

    /// <summary>
    /// 分层的柔和投影：多层同心路径叠加 + 平方衰减，最外层 alpha 收敛到 0，
    /// 因此边界不会出现可见硬边（比固定层数的"台阶"更接近真实高斯模糊）。
    /// </summary>
    internal static void DrawSoftShadow(IntPtr g, float strength, float blur, Func<float, IntPtr> pathFactory)
    {
        const int layers = 16;
        for (int i = layers; i >= 1; i--)
        {
            float t = i / (float)layers;
            uint alpha = (uint)Math.Clamp((int)MathF.Round(7.5f * MathF.Pow(1f - t, 1.5f) * strength), 0, 255);
            if (alpha == 0)
            {
                continue;
            }

            var path = pathFactory(blur * t);
            FillPath(g, path, alpha << 24);
            GdipDeletePath(path);
        }
    }

    #endregion

    #region 文字

    internal static void DrawText(IntPtr g, string text, float x, float y, float w, float h, IntPtr font,
        int align, uint argb)
    {
        if (string.IsNullOrEmpty(text) || font == IntPtr.Zero || argb >> 24 == 0)
        {
            return;
        }

        var rect = new RectF(x, y, w, h);
        if (GdipCreateSolidFill(argb, out var brush) == 0)
        {
            GdipDrawString(g, text, text.Length, font, ref rect, FormatFor(align), brush);
            GdipDeleteBrush(brush);
        }
    }

    internal static float MeasureText(IntPtr g, string text, IntPtr font)
    {
        if (string.IsNullOrEmpty(text) || font == IntPtr.Zero)
        {
            return 0f;
        }

        var rect = new RectF(0f, 0f, 4000f, 400f);
        if (GdipMeasureString(g, text, text.Length, font, ref rect, FormatFor(AlignNear), out var bbox, out _, out _) == 0)
        {
            return bbox.Width;
        }

        return 0f;
    }

    private static readonly IntPtr[] FormatCache = new IntPtr[3];

    private static IntPtr FormatFor(int align)
    {
        int index = align == AlignCenter ? 1 : (align == AlignFar ? 2 : 0);
        if (FormatCache[index] != IntPtr.Zero)
        {
            return FormatCache[index];
        }

        if (GdipCreateStringFormat(StringFormatNoWrap, 0, out var format) != 0)
        {
            return IntPtr.Zero;
        }

        GdipSetStringFormatAlign(format, index switch { 1 => AlignCenter, 2 => AlignFar, _ => AlignNear });
        GdipSetStringFormatLineAlign(format, LineCenter);
        FormatCache[index] = format;
        return format;
    }

    #endregion

    #region 画布状态

    internal static void Clear(IntPtr g) => GdipGraphicsClear(g, 0);

    internal static void SetQuality(IntPtr g)
    {
        GdipSetSmoothingMode(g, SmoothingModeAntiAlias);
        GdipSetTextRenderingHint(g, TextRenderingHintAntiAliasGridFit);
        GdipSetPixelOffsetMode(g, 4 /* PixelOffsetModeHighQuality */);
        GdipSetInterpolationMode(g, 7 /* InterpolationModeHighQualityBicubic */);
    }

    internal static void SetClip(IntPtr g, IntPtr path) => GdipSetClipPath(g, path, CombineModeIntersect);

    internal static void ResetClip(IntPtr g) => GdipResetClip(g);

    internal static IntPtr SaveGraphics(IntPtr g) => GdipSaveGraphics(g, out int state) == 0 ? new IntPtr(state) : IntPtr.Zero;

    internal static void RestoreGraphics(IntPtr g, IntPtr state)
    {
        if (state != IntPtr.Zero)
        {
            GdipRestoreGraphics(g, (int)state.ToInt64());
        }
    }

    /// <summary>平移当前世界变换（用于把离屏浮层摆到画布指定位置）。</summary>
    internal static void Translate(IntPtr g, float dx, float dy)
        => GdipTranslateWorldTransform(g, dx, dy, MatrixOrderAppend);

    /// <summary>以 (cx, cy) 为中心等比缩放当前世界变换。</summary>
    internal static void ScaleAbout(IntPtr g, float cx, float cy, float scale)
    {
        GdipTranslateWorldTransform(g, cx, cy, MatrixOrderAppend);
        GdipScaleWorldTransform(g, scale, scale, MatrixOrderAppend);
        GdipTranslateWorldTransform(g, -cx, -cy, MatrixOrderAppend);
    }

    /// <summary>以 (cx, cy) 为锚点做非等比缩放（分离 X/Y，用于「从屏幕边缘滑出」的竖向压缩）。</summary>
    internal static void ScaleAbout(IntPtr g, float cx, float cy, float scaleX, float scaleY)
    {
        GdipTranslateWorldTransform(g, cx, cy, MatrixOrderAppend);
        GdipScaleWorldTransform(g, scaleX, scaleY, MatrixOrderAppend);
        GdipTranslateWorldTransform(g, -cx, -cy, MatrixOrderAppend);
    }

    #endregion

    #region 字体

    private static readonly Dictionary<string, IntPtr> FamilyCache = [];
    private static readonly Dictionary<string, IntPtr> FontCache = [];

    internal static IntPtr ResolveFamily(string name)
    {
        if (FamilyCache.TryGetValue(name, out var cached))
        {
            return cached;
        }

        var family = IntPtr.Zero;
        if (GdipCreateFontFamilyFromName(name, IntPtr.Zero, out var created) != 0)
        {
            created = IntPtr.Zero;
        }

        if (created != IntPtr.Zero)
        {
            family = created;
        }

        FamilyCache[name] = family;
        return family;
    }

    /// <summary>按「像素高度」创建字体，带字体族回退；结果按字号缓存，避免逐帧重建。</summary>
    internal static IntPtr Font(string family, float px, bool bold)
    {
        string key = $"{family}|{px:0.##}|{(bold ? 1 : 0)}";
        if (FontCache.TryGetValue(key, out var cached) && cached != IntPtr.Zero)
        {
            return cached;
        }

        IntPtr font = IntPtr.Zero;
        var handle = ResolveFamily(family);
        if (handle != IntPtr.Zero && GdipCreateFont(handle, px, bold ? 1 : 0, UnitPixel, out var created) == 0)
        {
            font = created;
        }

        FontCache[key] = font;
        return font;
    }

    /// <summary>数字优先用 Segoe UI Variable Display，中文回退到微软雅黑。</summary>
    internal static IntPtr NumberFont(float px, bool bold = true)
    {
        var font = Font("Segoe UI Variable Display", px, bold);
        if (font == IntPtr.Zero)
        {
            font = Font("Segoe UI", px, bold);
        }

        return font;
    }

    internal static IntPtr TextFont(float px, bool bold = false)
    {
        var font = Font("Microsoft YaHei UI", px, bold);
        if (font == IntPtr.Zero)
        {
            font = Font("Segoe UI", px, bold);
        }

        return font;
    }

    #endregion

    #region GDI+ P/Invoke

    [DllImport("gdiplus.dll")]
    internal static extern int GdiplusStartup(out nuint token, ref GdiplusStartupInput input, IntPtr output);

    [DllImport("gdiplus.dll")]
    internal static extern int GdiplusShutdown(nuint token);

    [StructLayout(LayoutKind.Sequential)]
    internal struct GdiplusStartupInput
    {
        public uint GdiplusVersion;
        public IntPtr DebugEventCallback;
        public bool SuppressBackgroundThread;
        public bool SuppressExternalCodecs;

        public static GdiplusStartupInput Default => new() { GdiplusVersion = 1 };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PointF
    {
        public float X;
        public float Y;

        public PointF(float x, float y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RectF
    {
        public float X;
        public float Y;
        public float Width;
        public float Height;

        public RectF(float x, float y, float width, float height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }
    }

    [DllImport("gdiplus.dll")]
    internal static extern int GdipCreateBitmapFromScan0(int width, int height, int stride, int format, IntPtr scan0, out IntPtr bitmap);

    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
    internal static extern int GdipSaveImageToFile(IntPtr image, string filename, ref Guid clsidEncoder, IntPtr callback);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipSaveImageToStream(IntPtr image, IntPtr stream, ref Guid clsidEncoder, IntPtr callback);

    [DllImport("ole32.dll")]
    internal static extern int CreateStreamOnHGlobal(IntPtr hGlobal, bool deleteOnRelease, out IntPtr stream);

    [DllImport("ole32.dll")]
    internal static extern int GetHGlobalFromStream(IntPtr stream, out IntPtr hGlobal);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    internal static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    internal static extern UIntPtr GlobalSize(IntPtr hMem);

    /// <summary>把 GDI+ 位图编码为 PNG 字节（内存流，用于设置页的实时预览）。</summary>
    internal static byte[]? EncodePng(IntPtr image)
    {
        if (image == IntPtr.Zero)
        {
            return null;
        }

        // PNG 编码器固定 CLSID（{557CF406-1A04-11D3-9A73-0000F81EF32E}）
        var encoder = new Guid("557CF406-1A04-11D3-9A73-0000F81EF32E");
        if (CreateStreamOnHGlobal(IntPtr.Zero, true, out var stream) != 0 || stream == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (GdipSaveImageToStream(image, stream, ref encoder, IntPtr.Zero) != 0
                || GetHGlobalFromStream(stream, out var hGlobal) != 0
                || hGlobal == IntPtr.Zero)
            {
                return null;
            }

            int size = (int)GlobalSize(hGlobal).ToUInt64();
            var buffer = new byte[size];
            IntPtr ptr = GlobalLock(hGlobal);
            if (ptr == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                Marshal.Copy(ptr, buffer, 0, size);
            }
            finally
            {
                GlobalUnlock(hGlobal);
            }

            return buffer;
        }
        finally
        {
            Marshal.Release(stream);
        }
    }

    [DllImport("gdiplus.dll")]
    internal static extern int GdipDisposeImage(IntPtr image);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipGetImageGraphicsContext(IntPtr image, out IntPtr graphics);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipDeleteGraphics(IntPtr graphics);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipGraphicsClear(IntPtr graphics, uint color);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipSetSmoothingMode(IntPtr graphics, int smoothingMode);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipSetTextRenderingHint(IntPtr graphics, int textRenderingHint);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipSetPixelOffsetMode(IntPtr graphics, int pixelOffsetMode);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipSetInterpolationMode(IntPtr graphics, int interpolationMode);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipSetClipPath(IntPtr graphics, IntPtr path, int combineMode);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipResetClip(IntPtr graphics);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipSaveGraphics(IntPtr graphics, out int state);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipRestoreGraphics(IntPtr graphics, int state);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipTranslateWorldTransform(IntPtr graphics, float dx, float dy, int order);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipScaleWorldTransform(IntPtr graphics, float sx, float sy, int order);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipCreateSolidFill(uint argb, out IntPtr brush);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipCreateLineBrush(ref PointF point1, ref PointF point2, uint color1, uint color2, int wrapMode, out IntPtr brush);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipDeleteBrush(IntPtr brush);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipCreatePen1(uint argb, float width, int unit, out IntPtr pen);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipDeletePen(IntPtr pen);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipSetPenLineCap197819(IntPtr pen, int startCap, int endCap, int dashCap);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipSetPenLineJoin(IntPtr pen, int lineJoin);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipDrawLine(IntPtr graphics, IntPtr pen, float x1, float y1, float x2, float y2);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipDrawArc(IntPtr graphics, IntPtr pen, float x, float y, float width, float height, float startAngle, float sweepAngle);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipCreatePath(int brushMode, out IntPtr path);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipDeletePath(IntPtr path);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipAddPathLine(IntPtr path, float x1, float y1, float x2, float y2);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipAddPathBezier(IntPtr path, float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipAddPathArc(IntPtr path, float x, float y, float width, float height, float startAngle, float sweepAngle);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipAddPathRectangle(IntPtr path, float x, float y, float width, float height);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipDrawImage(IntPtr graphics, IntPtr image, float x, float y);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipCreateTexture2I(IntPtr image, int wrapMode, float x, float y, float width, float height, out IntPtr texture);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipAddPathEllipse(IntPtr path, float x, float y, float width, float height);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipClosePathFigure(IntPtr path);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipFillPath(IntPtr graphics, IntPtr brush, IntPtr path);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipDrawPath(IntPtr graphics, IntPtr pen, IntPtr path);

    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
    internal static extern int GdipCreateFontFamilyFromName(string name, IntPtr fontCollection, out IntPtr fontFamily);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipCreateFont(IntPtr fontFamily, float emSize, int style, int unit, out IntPtr font);

    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
    internal static extern int GdipCreateStringFormat(int formatAttributes, int language, out IntPtr format);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipSetStringFormatAlign(IntPtr format, int align);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipSetStringFormatLineAlign(IntPtr format, int lineAlign);

    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
    internal static extern int GdipMeasureString(IntPtr graphics, string text, int length, IntPtr font, ref RectF layoutRect, IntPtr stringFormat, out RectF boundingBox, out int codepointsFitted, out int linesFilled);

    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
    internal static extern int GdipDrawString(IntPtr graphics, string text, int length, IntPtr font, ref RectF layoutRect, IntPtr stringFormat, IntPtr brush);

    #endregion
}

/// <summary>
/// 分层窗口画布：缓存 32 位 DIB 与 GDI+ 位图/Graphics，尺寸不变时零重建，
/// 通过 UpdateLayeredWindow 做硬件 Premultiplied Alpha 混合。
/// </summary>
internal sealed class LayeredCanvas : IDisposable
{
    private IntPtr _memDc;
    private IntPtr _dib;
    private IntPtr _oldBmp;
    private IntPtr _bitmap;
    private IntPtr _graphics;
    private IntPtr _bits;
    private int _width;
    private int _height;

    public IntPtr Graphics => _graphics;
    public IntPtr Bits => _bits;
    public IntPtr Image => _bitmap;
    public int Width => _width;
    public int Height => _height;

    public bool Ensure(int width, int height)
    {
        if (width < 1 || height < 1)
        {
            return false;
        }

        if (_graphics != IntPtr.Zero && width == _width && height == _height)
        {
            OverlayGdi.Clear(_graphics);
            return true;
        }

        Release();

        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            _memDc = NativeMethods.CreateCompatibleDC(screenDc);
            var bmi = new NativeMethods.BitmapInfo
            {
                Header = new NativeMethods.BitmapInfoHeader
                {
                    BiSize = 40,
                    BiWidth = width,
                    BiHeight = -height, // Top-down，与 GDI+ 扫描线一致
                    BiPlanes = 1,
                    BiBitCount = 32,
                    BiCompression = NativeMethods.BiRgb,
                },
            };

            _dib = NativeMethods.CreateDIBSection(screenDc, ref bmi, 0, out _bits, IntPtr.Zero, 0);
            if (_dib == IntPtr.Zero || _bits == IntPtr.Zero)
            {
                Release();
                return false;
            }

            _oldBmp = NativeMethods.SelectObject(_memDc, _dib);
            if (OverlayGdi.GdipCreateBitmapFromScan0(width, height, width * 4, OverlayGdi.PixelFormat32bppPARGB, _bits, out _bitmap) != 0
                || _bitmap == IntPtr.Zero)
            {
                Release();
                return false;
            }

            if (OverlayGdi.GdipGetImageGraphicsContext(_bitmap, out _graphics) != 0 || _graphics == IntPtr.Zero)
            {
                Release();
                return false;
            }

            OverlayGdi.SetQuality(_graphics);
            _width = width;
            _height = height;
            OverlayGdi.Clear(_graphics);
            return true;
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public bool Present(IntPtr hwnd, int x, int y)
    {
        if (_graphics == IntPtr.Zero || _memDc == IntPtr.Zero)
        {
            return false;
        }

        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var ptSrc = new NativeMethods.Point { X = 0, Y = 0 };
            var ptDst = new NativeMethods.Point { X = x, Y = y };
            var size = new NativeMethods.Size { Cx = _width, Cy = _height };
            var blend = new NativeMethods.BlendFunction
            {
                BlendOp = NativeMethods.AcSrcOver,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = NativeMethods.AcSrcAlpha,
            };

            return NativeMethods.UpdateLayeredWindow(hwnd, screenDc, ref ptDst, ref size, _memDc, ref ptSrc, 0, ref blend, NativeMethods.UlwAlpha);
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void Release()
    {
        if (_graphics != IntPtr.Zero)
        {
            OverlayGdi.GdipDeleteGraphics(_graphics);
            _graphics = IntPtr.Zero;
        }

        if (_bitmap != IntPtr.Zero)
        {
            OverlayGdi.GdipDisposeImage(_bitmap);
            _bitmap = IntPtr.Zero;
        }

        if (_oldBmp != IntPtr.Zero && _memDc != IntPtr.Zero)
        {
            NativeMethods.SelectObject(_memDc, _oldBmp);
            _oldBmp = IntPtr.Zero;
        }

        if (_dib != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_dib);
            _dib = IntPtr.Zero;
        }

        if (_memDc != IntPtr.Zero)
        {
            NativeMethods.DeleteDC(_memDc);
            _memDc = IntPtr.Zero;
        }

        _bits = IntPtr.Zero;
        _width = 0;
        _height = 0;
    }

    public void Dispose() => Release();
}
