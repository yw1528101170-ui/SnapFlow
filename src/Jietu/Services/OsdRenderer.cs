using System.Globalization;

namespace Jietu;

/// <summary>
/// 性能悬浮窗（OSD）渲染器：贴顶横向长条。
/// 每个指标是一格「小标签 + 副值 / 大数值 + 进度条」，靠字号与色彩拉层级；
/// 进度条与数值同级、9dip 高、明显可见，用于一眼判断负载位置。
/// 入场为「锚定屏幕角部缩放 + 淡入」，不横向生长。
/// </summary>
internal static class OsdRenderer
{
    private const float CardHeightDip = 46f;
    private const float PadLeftDip = 16f;
    private const float PadRightDip = 16f;
    private const float CellGapDip = 20f;
    private const float CellWidthDip = 105f;
    private const float ShadowPadDip = 20f;

    private const float LabelTopDip = 8f;
    private const float LabelHeightDip = 11f;
    private const float ValueTopDip = 20f;
    private const float ValueHeightDip = 18f;

    private const float LabelBoxDip = 30f;   // 标签固定宽，副值左边缘因此天然对齐
    private const float LabelGapDip = 4f;
    private const float ValueBoxDip = 52f;
    private const float BarGapDip = 8f;
    private const float BarWidthDip = 52f;
    private const float BarHeightDip = 9f;

    private const float LabelSizeDip = 9f;
    private const float MetaSizeDip = 9f;
    private const float ValueSizeDip = 15f;

    private const uint CardTop = 0xFF12131A;
    private const uint CardBottom = 0xFF06060B;
    private const uint BorderColor = 0x22FFFFFF;
    private const uint TrackColor = 0x1FFFFFFF;
    private const uint LabelPrimary = 0xFAFFFFFF;
    private const uint LabelTertiary = 0xAAFFFFFF;

    /// <summary>已平滑后的显示数值（与目标值之间做指数趋近），可变结构以便原地缓动。</summary>
    internal struct Metrics
    {
        public float Gpu;
        public float Temp;
        public float VramGb;
        public float Cpu;
        public float Ghz;
        public float Ram;
        public float RamUsedGb;
        public float Down;
        public float Up;
        public float Fps;
        public bool FpsMeasured;
        public float RefreshHz;

        public static Metrics From(HardwareMetrics m) => new()
        {
            Gpu = m.GpuUsagePercent,
            Temp = m.GpuTemperature,
            VramGb = (float)(m.GpuMemoryUsedBytes / (1024.0 * 1024.0 * 1024.0)),
            Cpu = m.CpuUsagePercent,
            Ghz = m.CpuFrequencyGhz,
            Ram = m.RamUsagePercent,
            RamUsedGb = (float)(m.RamUsedBytes / (1024.0 * 1024.0 * 1024.0)),
            Down = m.NetDownloadSpeedBytesPerSec,
            Up = m.NetUploadSpeedBytesPerSec,
            Fps = m.FrameRate,
            FpsMeasured = m.FrameRateMeasured,
            RefreshHz = m.RefreshRateHz,
        };
    }

    /// <summary>
    /// 单帧几何：BufferWidth/Height 为分层窗口画布尺寸；CellWidth/CardWidth 由 Layout 量好后带入，
    /// Reveal 为 0..1 的呈现度（>1 时表示弹簧过冲）。
    /// </summary>
    internal readonly record struct Frame(
        int BufferWidth,
        int BufferHeight,
        float Scale,
        bool TopRight,
        float Reveal,
        float CardWidth,
        float CellWidth,
        float EndTop,
        float EndBottom);

    /// <summary>
    /// 一格指标。Percent 为 0..100，仅用于 ≥85% 的告警配色；Bar 为 0..1 的进度条填充比例；
    /// Highlight 为 true 时数值本身也参与告警变色。
    /// </summary>
    private readonly record struct Cell(
        string Label,
        string Meta,
        uint Accent,
        string Value,
        float Percent,
        float Bar,
        bool HasValue,
        bool Warn);

    private static readonly LayeredCanvas Probe = new();

    internal static Frame Layout(float scale, bool topRight, in Metrics m)
    {
        float h = CardHeightDip * scale;
        float cellW = MeasureCellWidth(scale);
        // 单元格数量按实际内容计算（去掉帧率格后为 4 格），否则卡片会多留一格的空白。
        var cellCount = BuildCells(m, scale).Length;
        float contentW = (PadLeftDip + PadRightDip) * scale + cellW * cellCount + CellGapDip * Math.Max(0, cellCount - 1) * scale;
        float cardW = EndFootprint(h) + contentW;
        int bufferW = (int)Math.Ceiling(cardW + ShadowPadDip * scale);
        int bufferH = (int)Math.Ceiling(h + ShadowPadDip * scale);
        return new Frame(Math.Max(bufferW, 8), Math.Max(bufferH, 8), scale, topRight, 1f, cardW, cellW,
            EndTopRadius(h), EndBottomRadius(h));
    }

    /// <summary>
    /// 每格宽度 = 数值框 + 间距 + 条；用「最宽可能串」实测兜底，保证数值变化时布局不抖。
    /// 返回设备像素（已含 DPI 缩放）。
    /// </summary>
    private static float MeasureCellWidth(float scale)
    {
        if (MathF.Abs(_measuredScale - scale) < 0.0001f && _measuredWidth > 0f)
        {
            return _measuredWidth;
        }

        float width = CellWidthDip * scale;
        if (Probe.Ensure(8, 8))
        {
            var g = Probe.Graphics;
            var (labelFont, valueFont) = Fonts(scale);
            foreach (var c in WorstCaseCells())
            {
                float need = OverlayGdi.MeasureText(g, c.Value, valueFont) + BarGapDip * scale + BarWidthDip * scale;
                need = MathF.Max(need, OverlayGdi.MeasureText(g, c.Label, labelFont));
                width = MathF.Max(width, MathF.Ceiling(need) + 4f * scale);
            }
        }

        _measuredWidth = width;
        _measuredScale = scale;
        return width;
    }

    private static float _measuredScale = -1f;
    private static float _measuredWidth;

    private static Cell[] WorstCaseCells() =>
    [
        new("GPU", "88° · 24.0G", OverlayGdi.ApplePurple, "100%", 0f, 0f, true, true),
        new("CPU", "5.99GHz", OverlayGdi.AppleGreen, "100%", 0f, 0f, true, true),
        new("RAM", "32.0G", OverlayGdi.AppleOrange, "100%", 0f, 0f, true, true),
        new("NET", "↑ 99.9M", OverlayGdi.AppleTeal, "↓ 99.9M", 0f, 0f, true, false),
    ];

    /// <summary>
    /// 自由端几何：顶部内凹倒角 r1 与底部大圆角 r2 相切（r1 + r2 = h，中间没有直线段），
    /// 边界从屏幕顶边一路顺滑滑到底边，全程无折角；卡片在最顶端最宽，因此完全「融」进屏幕边缘。
    /// </summary>
    private static float EndTopRadius(float h) => h * 0.5f;

    private static float EndBottomRadius(float h) => h * 0.5f;

    /// <summary>端部在水平方向占用的宽度（r1 + r2 = h）。</summary>
    private static float EndFootprint(float h) => EndTopRadius(h) + EndBottomRadius(h);

    /// <summary>内容起点相对卡片左缘的内缩量，避开自由端 S 曲线的扫掠区。</summary>
    private static float ContentInset(float h) => h * 0.86f;

    internal static void Draw(IntPtr g, in Frame frame, in Metrics m, bool cacheChrome = true)
    {
        float scale = frame.Scale;
        float presence = OverlayGdi.Clamp01(frame.Reveal);
        bool topRight = frame.TopRight;
        float h = CardHeightDip * scale;
        float cardW = frame.CardWidth;
        float cardX = topRight ? frame.BufferWidth - cardW : 0f;

        // 内容跟着形体一起进退，末尾快速收淡，避免右侧还能看到半透明的壳体
        float alpha = OverlayGdi.Clamp01(presence * 1.25f);

        // 进出视野：以「横向收进/伸出停靠的那个角」为主，只带一点点向上的分量。
        // 关键约束：胶囊右边始终咬住屏幕边缘，抬升幅度压得很小，
        // 否则右侧会腾出缝、露出胶囊底下的那条底边（看起来像浮起来）。
        float eased = MathF.Min(presence, 1f);
        float travel = 1f - eased;
        float slideX = travel * (cardW * 0.95f);
        float slideY = travel * (h * 0.12f);
        float towardCorner = topRight ? 1f : -1f;
        float scaleFactor = 0.965f + 0.035f * MathF.Min(presence, 1.02f);
        float anchorX = topRight ? frame.BufferWidth : 0f;

        float endTop = frame.EndTop;
        float endBottom = frame.EndBottom;
        bool stable = presence >= 0.999f;
        var saved = IntPtr.Zero;
        if (!stable || MathF.Abs(scaleFactor - 1f) > 0.002f)
        {
            saved = OverlayGdi.SaveGraphics(g);
            OverlayGdi.ScaleAbout(g, anchorX, 0f, scaleFactor);
            if (travel > 0.002f)
            {
                OverlayGdi.Translate(g, slideX * towardCorner, -slideY);
            }
        }

        // 静态层（阴影 / 卡片 / 高光 / 描边）在稳定态按几何缓存，数值滚动时只重绘内容。
        // 设置页预览以 cacheChrome = false 渲染，避免抢占实时窗口的缓存画布。
        string key = string.Create(CultureInfo.InvariantCulture,
            $"{frame.BufferWidth}x{frame.BufferHeight}|{scale:0.###}|{topRight}|{cardW:0.#}");
        if (stable && cacheChrome && Chrome.Matches(key))
        {
            OverlayGdi.GdipDrawImage(g, Chrome.Image, 0f, 0f);
        }
        else if (stable && cacheChrome)
        {
            Chrome.Render(key, frame.BufferWidth, frame.BufferHeight, g2 =>
                DrawChrome(g2, cardX, cardW, h, scale, endTop, endBottom, topRight, 1f));
            if (Chrome.Ready)
            {
                OverlayGdi.GdipDrawImage(g, Chrome.Image, 0f, 0f);
            }
        }
        else
        {
            if (cacheChrome)
            {
                Chrome.Invalidate();
            }

            DrawChrome(g, cardX, cardW, h, scale, endTop, endBottom, topRight, presence);
        }

        DrawContent(g, m, cardX + ContentInset(h) + PadLeftDip * scale, frame.CellWidth, h, alpha, scale);

        if (saved != IntPtr.Zero)
        {
            OverlayGdi.RestoreGraphics(g, saved);
        }
    }

    /// <summary>阴影 + 卡片底色 + 顶部高光 + 自由端描边（几何不变时复用）。</summary>
    private static void DrawChrome(IntPtr g, float cardX, float cardW, float h, float scale,
        float endTop, float endBottom, bool topRight, float alpha)
    {
        if (alpha <= 0.01f)
        {
            return;
        }

        OverlayGdi.DrawSoftShadow(g, alpha, 20f * scale, expand =>
        {
            // 只向屏幕内侧与下方扩散，绝不越过物理屏幕顶边
            float sx = topRight ? cardX - expand : cardX;
            float sw = cardW + expand;
            return BodyPath(sx, 0f, sw, h + expand * 1.2f, endTop, endBottom, topRight, expand);
        });

        var cardPath = BodyPath(cardX, 0f, cardW, h, endTop, endBottom, topRight, 3f);
        OverlayGdi.FillPathVertical(g, cardPath, OverlayGdi.WithAlpha(CardTop, alpha),
            OverlayGdi.WithAlpha(CardBottom, alpha), cardX + cardW * 0.5f, 0f, h);

        OverlayGdi.SetClip(g, cardPath);
        var sheenPath = OverlayGdi.BuildRoundRect(cardX, 0f, MathF.Max(cardW, 1f), h, 0f);
        OverlayGdi.FillPathVertical(g, sheenPath, OverlayGdi.WithAlpha(0x10FFFFFF, alpha),
            OverlayGdi.WithAlpha(0x00FFFFFF, alpha), cardX + cardW * 0.5f, 0f, h * 0.62f);
        OverlayGdi.GdipDeletePath(sheenPath);
        OverlayGdi.ResetClip(g);
        OverlayGdi.GdipDeletePath(cardPath);

        var borderPath = EdgePath(cardX, 0f, cardW, h, endTop, endBottom, topRight);
        OverlayGdi.StrokePath(g, borderPath, OverlayGdi.WithAlpha(BorderColor, alpha * 0.85f), MathF.Max(1f, scale));
        OverlayGdi.GdipDeletePath(borderPath);
    }

    private static void DrawContent(IntPtr g, in Metrics m, float startX, float cellWidth, float h, float alpha, float scale)
    {
        var (labelFont, valueFont) = Fonts(scale);
        var cells = BuildCells(m, scale);

        float gap = CellGapDip * scale;
        float labelTop = LabelTopDip * scale;
        float labelH = LabelHeightDip * scale;
        float labelBox = LabelBoxDip * scale;
        float labelGap = LabelGapDip * scale;
        float valueTop = ValueTopDip * scale;
        float valueH = ValueHeightDip * scale;
        float valueBox = ValueBoxDip * scale;
        float barGap = BarGapDip * scale;
        float barW = BarWidthDip * scale;
        float barH = BarHeightDip * scale;
        float barY = valueTop + (valueH - barH) * 0.5f;

        float x = startX;
        foreach (var cell in cells)
        {
            // 第 1 行：彩色标签（固定宽，副值因此天然左对齐成网格） + 副值
            OverlayGdi.DrawText(g, cell.Label, x, labelTop, labelBox, labelH, labelFont, OverlayGdi.AlignNear,
                OverlayGdi.WithAlpha(OverlayGdi.Lighten(cell.Accent, 0.12f), alpha * 0.95f));
            if (cell.Meta.Length > 0)
            {
                OverlayGdi.DrawText(g, cell.Meta, x + labelBox + labelGap, labelTop, cellWidth - labelBox - labelGap,
                    labelH, labelFont, OverlayGdi.AlignNear, OverlayGdi.WithAlpha(LabelTertiary, alpha));
            }

            // 第 2 行：大数值 + 进度条
            uint valueColor = cell.Warn && cell.Percent >= 85f
                ? OverlayGdi.Mix(LabelPrimary, OverlayGdi.AppleRed, OverlayGdi.Clamp01((cell.Percent - 85f) / 8f))
                : LabelPrimary;
            OverlayGdi.DrawText(g, cell.Value, x, valueTop, valueBox, valueH, valueFont, OverlayGdi.AlignNear,
                OverlayGdi.WithAlpha(valueColor, alpha));

            DrawBar(g, x + valueBox + barGap, barY, barW, barH, cell.HasValue ? cell.Bar : -1f, cell.Accent, alpha, scale, cell.Warn);

            x += cellWidth + gap;
        }

        _ = h;
    }

    /// <summary>9dip 圆头进度条：轨道 12% 白，填充为 accent 渐变；≥85% 转红；pct &lt; 0 表示无数据，只画轨道。</summary>
    private static void DrawBar(IntPtr g, float x, float y, float w, float h, float pct, uint accent, float alpha,
        float scale, bool warn)
    {
        float radius = h * 0.5f;
        var track = OverlayGdi.BuildRoundRect(x, y, w, h, radius);
        OverlayGdi.FillPath(g, track, OverlayGdi.WithAlpha(TrackColor, alpha));
        OverlayGdi.GdipDeletePath(track);

        if (pct < 0f)
        {
            // 无比例数据（例如帧率读不到合成器计数时）：轨道上画一排点，明确「不是 0%，是没有」
            float dot = h * 0.5f;
            float step = dot * 2.3f;
            for (float dx = 3f; dx + dot <= w - 2f; dx += step)
            {
                var dotPath = OverlayGdi.BuildRoundRect(x + dx, y + (h - dot) * 0.5f, dot, dot, dot * 0.5f);
                OverlayGdi.FillPath(g, dotPath, OverlayGdi.WithAlpha(0x3DFFFFFF, alpha));
                OverlayGdi.GdipDeletePath(dotPath);
            }

            return;
        }

        float value = OverlayGdi.Clamp01(pct);
        float fillW = MathF.Max(h, value * w);
        uint fillTop = OverlayGdi.Lighten(accent, 0.34f);
        uint fillBottom = accent;
        if (warn && value * 100f >= 85f)
        {
            float hot = OverlayGdi.Clamp01((value * 100f - 85f) / 10f);
            fillTop = OverlayGdi.Mix(fillTop, OverlayGdi.Lighten(OverlayGdi.AppleRed, 0.25f), hot);
            fillBottom = OverlayGdi.Mix(fillBottom, OverlayGdi.AppleRed, hot);
        }

        var fill = OverlayGdi.BuildRoundRect(x, y, fillW, h, radius);
        OverlayGdi.FillPathVertical(g, fill, OverlayGdi.WithAlpha(fillTop, alpha), OverlayGdi.WithAlpha(fillBottom, alpha),
            x, y, y + h);
        OverlayGdi.GdipDeletePath(fill);
    }

    private static (IntPtr Label, IntPtr Value) Fonts(float scale)
        => (OverlayGdi.NumberFont(LabelSizeDip * scale),
            OverlayGdi.NumberFont(ValueSizeDip * scale));

    /// <summary>
    /// BuildCells 字符串的显示精度键：每个分量与对应插值字符串的实际输出精度一一对应
    /// （与 <c>OsdWindow.RenderKey</c> 的分档思路一致），键不变则字符串不变。
    /// </summary>
    private readonly record struct CellKey(
        int Gpu,
        int Temp,
        bool HasTemp,
        int Vram,
        int Cpu,
        int Ghz,
        bool HasGhz,
        int Ram,
        int RamUsed,
        long DownKey,
        long UpKey);

    private readonly record struct CellTexts(
        string GpuMeta,
        string CpuMeta,
        string RamMeta,
        string NetMeta,
        string GpuValue,
        string CpuValue,
        string RamValue,
        string NetValue);

    private static readonly object CellTextGate = new();
    private static CellKey _cellKey;
    private static CellTexts _cellTexts;
    private static bool _cellTextsValid;

    private static Cell[] BuildCells(in Metrics m, float scale)
    {
        // 字符串按显示精度缓存：指数趋近的中间帧（四舍五入后数字相同）不再每帧重新分配插值字符串。
        // 进度条/告警仍用未取整的原始值，平滑度不受影响。Layout 与 DrawContent 每帧各调一次，都命中同一份缓存。
        var key = new CellKey(
            RoundKey(m.Gpu),
            RoundKey(m.Temp),
            m.Temp >= 0f,
            (int)MathF.Round(m.VramGb * 10f),
            RoundKey(m.Cpu),
            (int)MathF.Round(m.Ghz * 100f),
            m.Ghz > 0f,
            RoundKey(m.Ram),
            (int)MathF.Round(m.RamUsedGb * 10f),
            SpeedTextKey(m.Down),
            SpeedTextKey(m.Up));

        CellTexts texts;
        lock (CellTextGate)
        {
            if (!_cellTextsValid || !key.Equals(_cellKey))
            {
                _cellTexts = BuildCellTexts(m, key.Vram, key.Ghz, key.RamUsed, key.HasTemp, key.HasGhz);
                _cellKey = key;
                _cellTextsValid = true;
            }

            texts = _cellTexts;
        }

        // 用户要求去掉帧率格（真实 FPS 需要注入或管理员 ETW，已按需求移除），OSD 只保留吞吐/占用类指标。
        _ = scale;
        return
        [
            new Cell("GPU", texts.GpuMeta, OverlayGdi.ApplePurple, texts.GpuValue, m.Gpu, m.Gpu / 100f, true, true),
            new Cell("CPU", texts.CpuMeta, OverlayGdi.AppleGreen, texts.CpuValue, m.Cpu, m.Cpu / 100f, true, true),
            new Cell("RAM", texts.RamMeta, OverlayGdi.AppleOrange, texts.RamValue, m.Ram, m.Ram / 100f, true, true),
            new Cell("NET", texts.NetMeta, OverlayGdi.AppleTeal, texts.NetValue, 0f,
                // 无百分比语义：以 100MB/s 为满量程显示上下行总吞吐
                Math.Clamp((m.Down + m.Up) / (100f * 1024f * 1024f), 0f, 1f), true, false),
        ];
    }

    private static CellTexts BuildCellTexts(in Metrics m, int vram, int ghz, int ramUsed, bool hasTemp, bool hasGhz)
    {
        string gpuMeta = (hasTemp, vram > 0) switch
        {
            (true, true) => $"{m.Temp:0}° · {vram / 10f:0.0}G",
            (true, false) => $"{m.Temp:0}°",
            (false, true) => $"{vram / 10f:0.0}G",
            _ => string.Empty,
        };

        return new CellTexts(
            GpuMeta: gpuMeta,
            CpuMeta: hasGhz ? $"{ghz / 100f:0.00}GHz" : string.Empty,
            RamMeta: ramUsed > 0 ? $"{ramUsed / 10f:0.0}G" : string.Empty,
            NetMeta: $"↑ {FormatSpeed(m.Up)}",
            GpuValue: $"{m.Gpu:0}%",
            CpuValue: $"{m.Cpu:0}%",
            RamValue: $"{m.Ram:0}%",
            NetValue: $"↓ {FormatSpeed(m.Down)}");
    }

    /// <summary>与 "0"/"0.0" 数字格式的四舍五入（AwayFromZero）一致，保证键与输出字符串严格同步。</summary>
    private static int RoundKey(float value)
        => (int)MathF.Round(value, MidpointRounding.AwayFromZero);

    /// <summary>速率按 FormatSpeed 的输出精度分档；档内变化不改变任何一个字。</summary>
    private static long SpeedTextKey(float bytesPerSec)
    {
        double value = Math.Max(0d, bytesPerSec);
        if (value < 1024d)
        {
            return (long)Math.Round(value, MidpointRounding.AwayFromZero);
        }

        if (value < 1024d * 1024d)
        {
            return 1_000_000 + (long)Math.Round(value / 1024d, MidpointRounding.AwayFromZero);
        }

        if (value < 1024d * 1024d * 1024d)
        {
            return 2_000_000 + (long)Math.Round(value / (1024d * 1024d) * 10d, MidpointRounding.AwayFromZero);
        }

        return 3_000_000 + (long)Math.Round(value / (1024d * 1024d * 1024d) * 10d, MidpointRounding.AwayFromZero);
    }

    #region 自由端几何

    /// <summary>
    /// 贴顶卡片轮廓：顶边与屏幕物理边缘齐平并一直延伸到自由端最外侧，
    /// 自由端由「内凹倒角 r1」与「底部圆角 r2」两段相切的圆弧组成（r1 + r2 = h），
    /// 中间不存在竖直直墙，因此既没有折角也不会像悬浮胶囊。贴屏一侧保持直角并向外微延展。
    /// </summary>
    private static IntPtr BodyPath(float x, float y, float w, float h, float endTop, float endBottom, bool topRight, float bleed)
    {
        // 顶部向屏幕外延展 bleed，圆弧半径按比例同步，保证底部仍精确落在 y + h
        float top = y - bleed;
        float height = h + bleed;
        float ratio = height / MathF.Max(h, 1f);
        float r1 = endTop * ratio;
        float r2 = endBottom * ratio;
        float yc2 = top + height - r2;

        OverlayGdi.GdipCreatePath(0, out var path);
        if (topRight)
        {
            OverlayGdi.GdipAddPathLine(path, x + w + bleed, top, x, top);
            if (r1 > 0.5f)
            {
                OverlayGdi.GdipAddPathArc(path, x - r1, top, r1 * 2f, r1 * 2f, 270f, 90f);
            }
            else
            {
                OverlayGdi.GdipAddPathLine(path, x, top, x, top + 0.01f);
            }

            if (r1 + r2 < height - 0.5f)
            {
                OverlayGdi.GdipAddPathLine(path, x + r1, top + r1, x + r1, yc2);
            }

            OverlayGdi.GdipAddPathArc(path, x + r1, yc2 - r2, r2 * 2f, r2 * 2f, 180f, -90f);
            OverlayGdi.GdipAddPathLine(path, x + r1 + r2, top + height, x + w + bleed, top + height);
            OverlayGdi.GdipAddPathLine(path, x + w + bleed, top + height, x + w + bleed, top);
        }
        else
        {
            OverlayGdi.GdipAddPathLine(path, x - bleed, top, x + w, top);
            if (r1 > 0.5f)
            {
                OverlayGdi.GdipAddPathArc(path, x + w - r1, top, r1 * 2f, r1 * 2f, 270f, -90f);
            }
            else
            {
                OverlayGdi.GdipAddPathLine(path, x + w, top, x + w, top + 0.01f);
            }

            if (r1 + r2 < height - 0.5f)
            {
                OverlayGdi.GdipAddPathLine(path, x + w - r1, top + r1, x + w - r1, yc2);
            }

            OverlayGdi.GdipAddPathArc(path, x + w - r1 - r2 * 2f, yc2 - r2, r2 * 2f, r2 * 2f, 0f, 90f);
            OverlayGdi.GdipAddPathLine(path, x + w - r1 - r2, top + height, x - bleed, top + height);
            OverlayGdi.GdipAddPathLine(path, x - bleed, top + height, x - bleed, top);
        }

        OverlayGdi.GdipClosePathFigure(path);
        return path;
    }

    /// <summary>只描自由端曲线与底边，顶边与贴屏侧不画线（保持与屏幕无缝融合）。</summary>
    private static IntPtr EdgePath(float x, float y, float w, float h, float endTop, float endBottom, bool topRight)
    {
        float r1 = endTop;
        float r2 = endBottom;
        float yc2 = y + h - r2;

        OverlayGdi.GdipCreatePath(0, out var path);
        if (topRight)
        {
            OverlayGdi.GdipAddPathArc(path, x - r1, y, r1 * 2f, r1 * 2f, 270f, 90f);
            if (r1 + r2 < h - 0.5f)
            {
                OverlayGdi.GdipAddPathLine(path, x + r1, y + r1, x + r1, yc2);
            }

            OverlayGdi.GdipAddPathArc(path, x + r1, yc2 - r2, r2 * 2f, r2 * 2f, 180f, -90f);
            OverlayGdi.GdipAddPathLine(path, x + r1 + r2, y + h, x + w, y + h);
        }
        else
        {
            // 底边 → 底部圆角（顺时针 90°）→ 竖边 → 顶部内凹倒角（180°→270°）
            OverlayGdi.GdipAddPathLine(path, x, y + h, x + w - r1 - r2, y + h);
            OverlayGdi.GdipAddPathArc(path, x + w - r1 - r2 * 2f, yc2 - r2, r2 * 2f, r2 * 2f, 90f, -90f);
            if (r1 + r2 < h - 0.5f)
            {
                OverlayGdi.GdipAddPathLine(path, x + w - r1, yc2, x + w - r1, y + r1);
            }

            OverlayGdi.GdipAddPathArc(path, x + w - r1, y, r1 * 2f, r1 * 2f, 180f, 90f);
        }

        return path;
    }

    /// <summary>几何缓存：几何不变时直接把上帧的静态层整块合成回来。</summary>
    private sealed class ChromeLayer
    {
        private readonly LayeredCanvas _canvas = new();
        private string _key = string.Empty;

        public bool Ready { get; private set; }

        public IntPtr Image => _canvas.Image;

        public bool Matches(string key) => Ready && string.Equals(_key, key, StringComparison.Ordinal);

        public void Invalidate() => Ready = false;

        public void Render(string key, int width, int height, Action<IntPtr> draw)
        {
            Ready = false;
            if (!_canvas.Ensure(width, height) || _canvas.Graphics == IntPtr.Zero)
            {
                return;
            }

            draw(_canvas.Graphics);
            _key = key;
            Ready = true;
        }
    }

    private static readonly ChromeLayer Chrome = new();

    #endregion

    private static string FormatSpeed(float bytesPerSec)
    {
        double value = Math.Max(0d, bytesPerSec);
        if (value < 1024d)
        {
            return $"{value:0}B";
        }

        if (value < 1024d * 1024d)
        {
            return (value / 1024d).ToString("0", CultureInfo.InvariantCulture) + "K";
        }

        if (value < 1024d * 1024d * 1024d)
        {
            return (value / (1024d * 1024d)).ToString("0.0", CultureInfo.InvariantCulture) + "M";
        }

        return (value / (1024d * 1024d * 1024d)).ToString("0.0", CultureInfo.InvariantCulture) + "G";
    }
}
