using System.Globalization;

namespace Jietu;

/// <summary>
/// 性能悬浮窗（OSD）渲染器：贴在屏幕顶部正中的一块「缺口形」卡片，
/// 顶边与屏幕物理边缘齐平、下沿两个大圆角，没有左右之分。
/// 四个指标各是一块独立圆角小面板：上排「彩色标签 + 副值」，下排「大数字 + 小号单位」，
/// 面板底部一条细轨当进度条。单位单独走小字号后缀，数值再宽也不会把单位挤掉。
/// 常态是紧凑态：只有四条彩色细轨，鼠标悬停才展开成详细版。
/// 入场为「顶端缩放 + 轻微下落 + 淡入」。
/// </summary>
internal static class OsdRenderer
{
    private const float DetailedHeightDip = 52f;
    private const float CompactHeightDip = 22f;

    private const float PadLeftDip = 14f;
    private const float PadRightDip = 14f;
    private const float CellGapDip = 10f;
    private const float CellWidthDip = 96f;
    private const float ShadowPadDip = 20f;

    private const float BlockTopDip = 4f;
    private const float BlockHeightDip = 44f;
    private const float BlockRadiusDip = 9f;
    private const float BlockPadDip = 10f;

    private const float LabelTopDip = 7f;      // 相对块顶
    private const float LabelHeightDip = 11f;
    private const float ValueTopDip = 17f;     // 相对块顶
    private const float ValueHeightDip = 17f;
    private const float UnitTopDip = 5f;       // 相对数值行：单位底线与数字底线齐平（实测 ink 对齐）
    private const float UnitHeightDip = 10f;
    private const float GaugeTopDip = 34f;     // 相对块顶
    private const float GaugeHeightDip = 5.5f;

    private const float LabelBoxDip = 26f;   // 标签固定宽，副值左边缘因此天然对齐
    private const float LabelGapDip = 4f;
    private const float UnitGapDip = 1.5f;

    private const float LabelSizeDip = 9f;
    private const float MetaSizeDip = 9f;
    private const float ValueSizeDip = 15f;

    private const string PercentUnit = "%";

    // 「冷雾玻璃」材质：接近白的半透明卡面 + 灰蓝细边 + 深色墨字，
    // 数据条一律走霓虹双色横向渐变，靠颜色而不是靠暗底来撑视觉。
    private const uint CardTop = 0xF5FBFDFF;
    private const uint CardBottom = 0xD8DFE9F5;
    private const uint BorderColor = 0x55222A36;
    private const uint TrackColor = 0x14182028;
    private const uint BlockFillTop = 0x0C1A2230;
    private const uint BlockFillBottom = 0x021A2230;
    private const uint BlockBorder = 0x1412181F;
    private const uint LabelPrimary = 0xFF101319;
    private const uint LabelTertiary = 0x8A20252E;
    private const uint UnitColor = 0x99232831;
    private const uint NoDataDot = 0x33000000;
    private const uint LineGloss = 0x59FFFFFF;

    // 卡片上沿的一层薄高光（只有贴屏那条边有，像玻璃被灯照到的棱）
    private const uint RimTop = 0x66FFFFFF;
    private const uint BottomRim = 0x5AFFFFFF;
    private const uint BorderHighlight = 0xE8FFFFFF;

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
    /// 单帧几何：BufferWidth/Height 为分层窗口画布尺寸；CardWidth/Height 由 Layout 量好后带入，
    /// DetailWidth/Height 是「详细版」的完整尺寸（形变时详细内容按它做等比缩放），
    /// Reveal 为 0..1 的呈现度（>1 时表示弹簧过冲），Expand 为紧凑态→详细态的展开度（0..1）。
    /// </summary>
    internal readonly record struct Frame(
        int BufferWidth,
        int BufferHeight,
        float Scale,
        float Reveal,
        float Expand,
        float CardWidth,
        float CardHeight,
        float DetailWidth,
        float DetailHeight,
        float CellWidth);

    /// <summary>
    /// 一块指标面板。Percent 为 0..100，仅用于 ≥85% 的告警配色；Bar 为 0..1 的进度条填充比例；
    /// Warn 为 true 时数值本身也参与告警变色。Number 走大字号，Unit 走小字号后缀。
    /// </summary>
    private readonly record struct Cell(
        string Label,
        string Meta,
        uint Accent,
        string Number,
        string Unit,
        float Percent,
        float Bar,
        bool HasValue,
        bool Warn);

    private static readonly LayeredCanvas Probe = new();

    /// <summary>
    /// Probe 画布与实测宽缓存的访问闸：OSD 渲染线程和设置页预览（UI 线程）都会进
    /// <see cref="MeasureCellWidth"/>，GDI+ 对象跨线程共用同一 DC 会出状态错乱。
    /// </summary>
    private static readonly object ProbeGate = new();

    internal static Frame Layout(float scale, float expand, in Metrics m)
    {
        expand = Math.Clamp(expand, 0f, 1f);

        float pad = ShadowPadDip * scale;
        float detailH = DetailedHeightDip * scale;
        float cellW = MeasureCellWidth(scale);
        var cellCount = BuildCells(m, scale).Length;
        float detailW = (PadLeftDip + PadRightDip) * scale + cellW * cellCount
            + CellGapDip * Math.Max(0, cellCount - 1) * scale;

        // 宽度恒定（就是详细版的整排宽度），只有高度在紧凑↔详细之间伸缩：
        // 展开时绝不左右变长，视觉上是从顶部往下「拉下来」一块
        float cardW = detailW;
        float cardH = CompactHeightDip * scale + (detailH - CompactHeightDip * scale) * expand;

        // 阴影只朝屏幕内侧与下方扩散，绝不越过物理屏幕顶边；左右两边对称留
        int bufferW = (int)Math.Ceiling(cardW + pad * 2f);
        int bufferH = (int)Math.Ceiling(cardH + pad);
        return new Frame(Math.Max(bufferW, 8), Math.Max(bufferH, 8), scale, 1f, expand,
            cardW, cardH, detailW, detailH, cellW);
    }

    /// <summary>卡片在画布里的左上角：两侧留白对称，卡片水平居中。</summary>
    private static float CardX(in Frame frame, float pad) => pad;

    /// <summary>卡片在画布内的左缘与宽度（宿主用它做定位与悬停命中）。</summary>
    internal static (float X, float Width) CardHorizontal(in Frame frame)
        => (CardX(frame, ShadowPadDip * frame.Scale), frame.CardWidth);

    /// <summary>
    /// 每块宽度 = 块内左右留白 + 上排（标签 + 副值）与下排（数字 + 单位）里更宽的那个；
    /// 用「最宽可能串」实测兜底，保证数值变化时布局不抖、单位也不会被裁掉。
    /// 返回设备像素（已含 DPI 缩放）。
    /// </summary>
    private static float MeasureCellWidth(float scale)
    {
        lock (ProbeGate)
        {
            if (MathF.Abs(_measuredScale - scale) < 0.0001f && _measuredWidth > 0f)
            {
                return _measuredWidth;
            }

            float width = CellWidthDip * scale;
            if (Probe.Ensure(8, 8))
            {
                var g = Probe.Graphics;
                var (smallFont, valueFont) = Fonts(scale);
                foreach (var c in WorstCaseCells())
                {
                    float metaRow = LabelBoxDip * scale + LabelGapDip * scale + OverlayGdi.MeasureText(g, c.Meta, smallFont);
                    float valueRow = OverlayGdi.MeasureText(g, c.Number, valueFont)
                        + UnitGapDip * scale + OverlayGdi.MeasureText(g, c.Unit, smallFont);
                    float need = MathF.Max(metaRow, valueRow) + BlockPadDip * 2f * scale;
                    width = MathF.Max(width, MathF.Ceiling(need) + 2f * scale);
                }
            }

            _measuredWidth = width;
            _measuredScale = scale;
            return width;
        }
    }

    private static float _measuredScale = -1f;
    private static float _measuredWidth;

    private static Cell[] WorstCaseCells() =>
    [
        // 副值按真实上限取最宽写法：三位数温度、24G 显存、128G 内存都要留得下（否则副值行会被框宽裁掉）
        new("GPU", "100° · 24.0G", OverlayGdi.ApplePurple, "100", PercentUnit, 0f, 0f, true, true),
        new("CPU", "5.99GHz", OverlayGdi.AppleGreen, "100", PercentUnit, 0f, 0f, true, true),
        new("RAM", "128.0G", OverlayGdi.AppleOrange, "100", PercentUnit, 0f, 0f, true, true),
        // 速率最宽的那一档：4 位 K 值（「↓ 1023」+「K/s」），M 档的两位整数也不超过它
        new("NET", "↑ 99.9M/s", OverlayGdi.AppleTeal, "↓ 1023", "K/s", 0f, 0f, true, false),
    ];

    /// <summary>
    /// 画一帧。<paramref name="glassImage"/> 是本帧的液态玻璃卡面（GpImage，尺寸等于卡片像素
    /// 尺寸，由调用方抓屏/合成后传入；IntPtr.Zero 表示没有玻璃，卡面走色块填充）。
    /// 玻璃图按参数传递而不是静态字段：OSD 渲染线程和设置页预览会各自调 Draw，
    /// 静态可变状态在两边并发时会把错误的玻璃画进对方的帧。
    /// </summary>
    internal static void Draw(IntPtr g, in Frame frame, in Metrics m, IntPtr glassImage = default,
        bool cacheChrome = true)
    {
        float scale = frame.Scale;
        float presence = OverlayGdi.Clamp01(frame.Reveal);
        float h = frame.CardHeight;
        float cardW = frame.CardWidth;
        float pad = ShadowPadDip * scale;
        float cardX = CardX(frame, pad);
        float expand = Math.Clamp(frame.Expand, 0f, 1f);

        // 内容跟着形体一起进退，末尾快速收淡，避免右侧还能看到半透明的壳体
        float alpha = OverlayGdi.Clamp01(presence * 1.25f);

        // 进出视野：卡片贴屏，只做一点点向下落位 + 淡入淡出；不做任何缩放，宽度全程不变
        float eased = MathF.Min(presence, 1f);
        float travel = 1f - eased;
        float slideY = travel * (h * 0.16f);

        bool stable = presence >= 0.999f;
        var saved = IntPtr.Zero;
        if (travel > 0.002f)
        {
            saved = OverlayGdi.SaveGraphics(g);
            OverlayGdi.Translate(g, 0f, -slideY);
        }

        // 静态层（阴影 / 卡片 / 描边）在稳定态按几何缓存，数值滚动时只重绘内容。
        // 液态玻璃的卡面每帧都来自「玻璃背后的画面」，缓存会把它冻住，所以这时一律直画。
        string key = string.Create(CultureInfo.InvariantCulture,
            $"{frame.BufferWidth}x{frame.BufferHeight}|{scale:0.###}|{cardW:0.#}|{h:0.#}");
        if (stable && cacheChrome && glassImage == IntPtr.Zero && Chrome.Matches(key))
        {
            OverlayGdi.GdipDrawImage(g, Chrome.Image, 0f, 0f);
        }
        else if (stable && cacheChrome && glassImage == IntPtr.Zero)
        {
            // 仅无玻璃时才走缓存：玻璃模式下卡面每帧都来自「玻璃背后的画面」，
            // 缓存会把背景冻住（且这里不带 glassImage 会画成纯色卡面）。
            Chrome.Render(key, frame.BufferWidth, frame.BufferHeight, g2 =>
                DrawChrome(g2, cardX, cardW, h, scale, 1f));
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

            DrawChrome(g, cardX, cardW, h, scale, presence, glassImage);
        }

        // 展开度太低时只剩底部那一排细轨；越接近详细态，文字逐渐淡入。
        // 内容整块以「卡底」为锚（dy），卡片长高时内容跟着下移，横向一动不动。
        float textAlpha = OverlayGdi.Clamp01((expand - 0.15f) / 0.6f);
        float dy = h - frame.DetailHeight;
        bool clipped = expand < 0.999f;

        if (clipped)
        {
            // 卡片轮廓只用来做裁剪，SetClip 会拷贝区域，用完立刻释放路径
            var clipPath = NotchBodyPath(cardX, 0f, cardW, h, NotchRadius(h), 3f);
            OverlayGdi.SetClip(g, clipPath);
            OverlayGdi.GdipDeletePath(clipPath);
        }

        DrawContent(g, m, cardX + PadLeftDip * scale, frame.CellWidth, dy,
            alpha * textAlpha, alpha, scale);

        if (clipped)
        {
            OverlayGdi.ResetClip(g);
        }

        if (saved != IntPtr.Zero)
        {
            OverlayGdi.RestoreGraphics(g, saved);
        }
    }

    /// <summary>卡片轮廓：一整块底部胶囊圆的缺口形，顶边与屏幕物理边缘齐平。</summary>
    private static IntPtr CardPath(float cardX, float cardW, float h)
        => NotchBodyPath(cardX, 0f, cardW, h, NotchRadius(h), 3f);

    /// <summary>阴影 + 卡片底色 + 描边（几何不变时复用）。<paramref name="glassImage"/> 为本帧玻璃卡面，可空。</summary>
    private static void DrawChrome(IntPtr g, float cardX, float cardW, float h, float scale, float alpha,
        IntPtr glassImage = default)
    {
        if (alpha <= 0.01f)
        {
            return;
        }

        // 液态玻璃模式：卡面不是「半透明色块」，而是「玻璃背后的画面被模糊 + 折射后的结果」。
        // 这块图像由调用方（实时窗口抓屏 / 预览页合成）做好传进来，尺寸就是卡片本身的像素尺寸，
        // 边缘 alpha 已带抗锯齿，所以这里直接 1:1 贴上去，不用裁剪路径。
        bool glass = glassImage != IntPtr.Zero;
        if (glass)
        {
            // 按当前卡片矩形缩放画：抓屏合成的玻璃可能是上一帧的高度，展开动画中间直接拉伸过渡
            OverlayGdi.GdipDrawImageRect(g, glassImage, cardX, 0f, cardW, h);
        }
        else
        {
            // 不画投影：亮色卡面配重阴影会在四个角外留一圈灰块，用户明确不要。
            // 轮廓和壁纸的分离交给描边那一层细边。
            var cardPath = CardPath(cardX, cardW, h);
            OverlayGdi.FillPathVertical(g, cardPath, OverlayGdi.WithAlpha(CardTop, alpha),
                OverlayGdi.WithAlpha(CardBottom, alpha), cardX + cardW * 0.5f, 0f, h);
            OverlayGdi.GdipDeletePath(cardPath);
        }

        // 1. 顶部镜面反射：贴着屏幕顶边的高光带，往下 40% 高度就隐没。
        //    卡片顶边是直的，这一条用直角矩形即可，不需要裁剪。
        //    玻璃模式下不加：玻璃引擎自己已经算过顶部反光，再叠一层就白成塑料
        if (!glass)
        {
            float rimH = MathF.Min(h * 0.4f, 17f * scale);
            var rimRect = OverlayGdi.BuildRoundRect(cardX, 0f, cardW, rimH, 0f);
            OverlayGdi.FillPathVertical(g, rimRect, OverlayGdi.WithAlpha(RimTop, alpha), 0x00FFFFFF,
                cardX + cardW * 0.5f, 0f, rimH);
            OverlayGdi.GdipDeletePath(rimRect);
        }

        // 2. 描边：只画一道细线。之前是「灰蓝宽边 + 白亮内沿」两层叠在同一条路径上，
        //    抗锯齿下会叠出 灰-亮-白-灰 四道条纹，缩到 1x 就是两条平行的边。
        //    玻璃模式不描边：轮廓已经是像素级镜面亮边，再叠一条会把暗色描到轮廓外（浅底上读作第二条线）
        if (!glass)
        {
            var rr = NotchRadius(h);
            var borderPath = NotchEdgePath(cardX, 0f, cardW, h, rr);
            OverlayGdi.StrokePath(g, borderPath, OverlayGdi.WithAlpha(BorderColor, alpha),
                MathF.Max(1.3f, 1.3f * scale));
            OverlayGdi.GdipDeletePath(borderPath);
        }
    }

    /// <summary>
    /// 四块面板的内容。dy 是「以卡底为锚」的纵向位移：紧凑态时 dy 为负，
    /// 上面的标签与数字被推出卡外（由卡片轮廓裁剪掉），只剩最底下那排细轨可见。
    /// textAlpha 只作用在文字上，细轨永远按 barAlpha 画，所以紧凑态就是详细版削到只剩底排轨。
    /// </summary>
    private static void DrawContent(IntPtr g, in Metrics m, float startX, float cellWidth, float dy,
        float textAlpha, float barAlpha, float scale)
    {
        var (smallFont, valueFont) = Fonts(scale);
        var cells = BuildCells(m, scale);

        float gap = CellGapDip * scale;
        float blockTop = dy + BlockTopDip * scale;
        float blockH = BlockHeightDip * scale;
        float blockPad = BlockPadDip * scale;
        float innerW = MathF.Max(cellWidth - blockPad * 2f, 1f);

        float labelTop = blockTop + LabelTopDip * scale;
        float labelH = LabelHeightDip * scale;
        float labelBox = LabelBoxDip * scale;
        float labelGap = LabelGapDip * scale;
        float valueTop = blockTop + ValueTopDip * scale;
        float valueH = ValueHeightDip * scale;
        float unitGap = UnitGapDip * scale;
        float unitTop = valueTop + UnitTopDip * scale;
        float gaugeTop = blockTop + GaugeTopDip * scale;

        float x = startX;
        foreach (var cell in cells)
        {
            // 每格一块独立的圆角面板：9% 白底 + 1px 描边，靠底色分组，不再用裸排 + 分隔线
            // 面板底与文字用同一个 alpha：文字淡入时底块不会先「空着」出现
            DrawBlock(g, x, blockTop, cellWidth, blockH, textAlpha, scale);

            // 上排：彩色标签（固定宽，副值因此天然左对齐成网格） + 副值
            // 浅底上标签不能直接上亮色，往墨色压 30% 才够清楚
            OverlayGdi.DrawText(g, cell.Label, x + blockPad, labelTop, labelBox, labelH, smallFont, OverlayGdi.AlignNear,
                OverlayGdi.WithAlpha(OverlayGdi.Mix(cell.Accent, LabelPrimary, 0.28f), textAlpha * 0.95f));
            if (cell.Meta.Length > 0)
            {
                OverlayGdi.DrawText(g, cell.Meta, x + blockPad + labelBox + labelGap, labelTop,
                    MathF.Max(innerW - labelBox - labelGap, 1f), labelH, smallFont, OverlayGdi.AlignNear,
                    OverlayGdi.WithAlpha(LabelTertiary, textAlpha));
            }

            // 下排：大数字 + 小号单位（单位紧贴实测数字宽，永远不会被框宽裁掉）
            uint valueColor = cell.Warn && cell.Percent >= 85f
                ? OverlayGdi.Mix(LabelPrimary, OverlayGdi.AppleRed, OverlayGdi.Clamp01((cell.Percent - 85f) / 8f))
                : LabelPrimary;
            OverlayGdi.DrawText(g, cell.Number, x + blockPad, valueTop, innerW, valueH, valueFont, OverlayGdi.AlignNear,
                OverlayGdi.WithAlpha(valueColor, textAlpha));
            float unitX = x + blockPad + OverlayGdi.MeasureText(g, cell.Number, valueFont) + unitGap;
            OverlayGdi.DrawText(g, cell.Unit, unitX, unitTop, MathF.Max(innerW - (unitX - x - blockPad), 1f),
                UnitHeightDip * scale, smallFont, OverlayGdi.AlignNear, OverlayGdi.WithAlpha(UnitColor, textAlpha));

            // 底部细轨：既是分隔也是负载位置；位置以卡底为锚，卡片长高时跟着下移、横向不动
            DrawBar(g, x + blockPad, gaugeTop, innerW, GaugeHeightDip * scale, cell.HasValue ? cell.Bar : -1f,
                cell.Accent, barAlpha, scale, cell.Warn);

            x += cellWidth + gap;
        }
    }

    /// <summary>一格面板的底：圆角矩形 + 自带的顶部高光 + 细描边，让四格在长条里各自成块。</summary>
    private static void DrawBlock(IntPtr g, float x, float y, float w, float h, float alpha, float scale)
    {
        var path = OverlayGdi.BuildRoundRect(x, y, w, h, BlockRadiusDip * scale);
        OverlayGdi.FillPathVertical(g, path, OverlayGdi.WithAlpha(BlockFillTop, alpha),
            OverlayGdi.WithAlpha(BlockFillBottom, alpha), x, y, y + h);
        OverlayGdi.StrokePath(g, path, OverlayGdi.WithAlpha(BlockBorder, alpha * 0.9f), MathF.Max(1f, scale));
        OverlayGdi.GdipDeletePath(path);
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
                OverlayGdi.FillPath(g, dotPath, OverlayGdi.WithAlpha(NoDataDot, alpha));
                OverlayGdi.GdipDeletePath(dotPath);
            }

            return;
        }

        float value = OverlayGdi.Clamp01(pct);
        float fillW = MathF.Max(h, value * w);
        var (from, to) = NeonPair(accent);
        if (warn && value * 100f >= 85f)
        {
            // 过载：双色渐变整体往红压，压得越多越红
            float hot = OverlayGdi.Clamp01((value * 100f - 85f) / 10f);
            from = OverlayGdi.Mix(from, OverlayGdi.AppleRed, hot);
            to = OverlayGdi.Mix(to, 0xFFFF2D55, hot);
        }

        // 横向双色渐变：从左侧冷色滑到右侧暖色，一根条里就有霓虹的走向
        var fill = OverlayGdi.BuildRoundRect(x, y, fillW, h, radius);
        OverlayGdi.FillPathLinear(g, fill, OverlayGdi.WithAlpha(from, alpha), OverlayGdi.WithAlpha(to, alpha),
            x, y + h * 0.5f, x + fillW, y + h * 0.5f);

        // 果冻感：条子上端压一条白色反光，只留在填充范围内。
        // 不调 ResetClip：它会把裁剪恢复为无限区域，清掉外层卡片的缺口形裁剪；
        // 下面的 RestoreGraphics 会连同裁剪一起还原到 SetClip 之前的状态。
        var saved = OverlayGdi.SaveGraphics(g);
        OverlayGdi.SetClip(g, fill);
        OverlayGdi.DrawLine(g, x, y + h * 0.32f, x + fillW, y + h * 0.32f,
            OverlayGdi.WithAlpha(LineGloss, alpha), MathF.Max(1f, h * 0.28f));
        if (saved != IntPtr.Zero)
        {
            OverlayGdi.RestoreGraphics(g, saved);
        }

        OverlayGdi.GdipDeletePath(fill);
    }

    /// <summary>每块数据条的霓虹双色：左端是冷一点的同族色，右端是它的邻色。</summary>
    private static (uint From, uint To) NeonPair(uint accent) => accent switch
    {
        OverlayGdi.ApplePurple => (0xFF8B5CFF, 0xFFFF6FD8),
        OverlayGdi.AppleGreen => (0xFF16D96B, 0xFF7EF0A8),
        OverlayGdi.AppleOrange => (0xFFFFA51F, 0xFFFF5E7A),
        OverlayGdi.AppleTeal => (0xFF23C8F5, 0xFF7C7BFF),
        OverlayGdi.AppleBlue => (0xFF2F7BFF, 0xFF46D8F5),
        OverlayGdi.AppleRed => (0xFFFF4B3A, 0xFFFF2D8F),
        _ => (OverlayGdi.Lighten(accent, 0.18f), accent),
    };

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
        string GpuNumber,
        string CpuNumber,
        string RamNumber,
        string NetNumber,
        string NetUnit);

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
            new Cell("GPU", texts.GpuMeta, OverlayGdi.ApplePurple, texts.GpuNumber, PercentUnit, m.Gpu, m.Gpu / 100f, true, true),
            new Cell("CPU", texts.CpuMeta, OverlayGdi.AppleGreen, texts.CpuNumber, PercentUnit, m.Cpu, m.Cpu / 100f, true, true),
            new Cell("RAM", texts.RamMeta, OverlayGdi.AppleOrange, texts.RamNumber, PercentUnit, m.Ram, m.Ram / 100f, true, true),
            new Cell("NET", texts.NetMeta, OverlayGdi.AppleTeal, texts.NetNumber, texts.NetUnit, 0f,
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

        var down = FormatSpeed(m.Down);
        var up = FormatSpeed(m.Up);
        return new CellTexts(
            GpuMeta: gpuMeta,
            CpuMeta: hasGhz ? $"{ghz / 100f:0.00}GHz" : string.Empty,
            RamMeta: ramUsed > 0 ? $"{ramUsed / 10f:0.0}G" : string.Empty,
            NetMeta: $"↑ {up.Number}{up.Unit}",
            GpuNumber: $"{m.Gpu:0}",
            CpuNumber: $"{m.Cpu:0}",
            RamNumber: $"{m.Ram:0}",
            NetNumber: $"↓ {down.Number}",
            NetUnit: down.Unit);
    }

    /// <summary>与 "0"/"0.0" 数字格式的四舍五入（AwayFromZero）一致，保证键与输出字符串严格同步。</summary>
    private static int RoundKey(float value)
        => (int)MathF.Round(value, MidpointRounding.AwayFromZero);

    /// <summary>速率按 FormatSpeed 的输出精度分档；档内变化不改变任何一个字。OsdWindow 的 RenderKey 也用它，保证「键变 ⇒ 字必变」。</summary>
    internal static long SpeedTextKey(float bytesPerSec)
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

    #region 缺口形轮廓

    /// <summary>底部圆角：紧凑态正好半高（小药丸），详细态放开到 26dip 更接近整颗胶囊的下沿。</summary>
    private static float NotchRadius(float h) => MathF.Min(h * 0.5f, 26f);

    /// <summary>下沿圆角（设备像素）。玻璃引擎要按同一个形状算边缘折射。</summary>
    internal static float BottomRadius(float heightPx) => NotchRadius(heightPx);

    /// <summary>
    /// 卡片轮廓：顶边与屏幕物理边缘齐平（顶角是直角，不留缝），下沿是两个大圆角 + 一段平直底边，
    /// 像从屏幕顶边长出来的一块缺口。顶部向外延展 bleed，避免边缘抗锯齿露缝。
    /// </summary>
    private static IntPtr NotchBodyPath(float x, float y, float w, float h, float r, float bleed)
    {
        float top = y - bleed;
        float bottom = y + h;
        float rr = MathF.Min(r, MathF.Min(w * 0.5f, h));

        OverlayGdi.GdipCreatePath(0, out var path);
        OverlayGdi.GdipAddPathLine(path, x, top, x + w, top);
        OverlayGdi.GdipAddPathLine(path, x + w, top, x + w, bottom - rr);
        OverlayGdi.GdipAddPathArc(path, x + w - rr * 2f, bottom - rr * 2f, rr * 2f, rr * 2f, 0f, 90f);
        OverlayGdi.GdipAddPathLine(path, x + w - rr, bottom, x + rr, bottom);
        OverlayGdi.GdipAddPathArc(path, x, bottom - rr * 2f, rr * 2f, rr * 2f, 90f, 90f);
        OverlayGdi.GdipAddPathLine(path, x, bottom - rr, x, top);
        OverlayGdi.GdipClosePathFigure(path);
        return path;
    }

    /// <summary>只描左右两条竖边与底部圆角，顶边不画线（保持与屏幕无缝融合）。</summary>
    private static IntPtr NotchEdgePath(float x, float y, float w, float h, float r)
    {
        float rr = MathF.Min(r, MathF.Min(w * 0.5f, h));

        OverlayGdi.GdipCreatePath(0, out var path);
        OverlayGdi.GdipAddPathLine(path, x, y, x, y + h - rr);
        OverlayGdi.GdipAddPathArc(path, x, y + h - rr * 2f, rr * 2f, rr * 2f, 180f, -90f);
        OverlayGdi.GdipAddPathLine(path, x + rr, y + h, x + w - rr, y + h);
        OverlayGdi.GdipAddPathArc(path, x + w - rr * 2f, y + h - rr * 2f, rr * 2f, rr * 2f, 90f, -90f);
        OverlayGdi.GdipAddPathLine(path, x + w, y + h - rr, x + w, y);
        return path;
    }

    /// <summary>只描底部一段（两个圆角 + 底边），用来画底缘那道透光亮边，不碰左右两侧。</summary>
    private static IntPtr NotchBottomPath(float x, float y, float w, float h, float r)
    {
        float rr = MathF.Min(r, MathF.Min(w * 0.5f, h));
        float bottom = y + h;

        OverlayGdi.GdipCreatePath(0, out var path);
        OverlayGdi.GdipAddPathArc(path, x, bottom - rr * 2f, rr * 2f, rr * 2f, 180f, -90f);
        OverlayGdi.GdipAddPathLine(path, x + rr, bottom, x + w - rr, bottom);
        OverlayGdi.GdipAddPathArc(path, x + w - rr * 2f, bottom - rr * 2f, rr * 2f, rr * 2f, 90f, -90f);
        return path;
    }

    #endregion

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

    /// <summary>
    /// 速率拆成「数值 + 单位」两段：数值走大字号，单位走小字号后缀。
    /// 单位始终是完整的「B/s」写法（而不是只写 K/M 的量级字母），
    /// 且两段分开绘制，单位不会被数值框宽度裁掉。
    /// </summary>
    private static (string Number, string Unit) FormatSpeed(float bytesPerSec)
    {
        double value = Math.Max(0d, bytesPerSec);
        if (value < 1024d)
        {
            return (value.ToString("0", CultureInfo.InvariantCulture), "B/s");
        }

        if (value < 1024d * 1024d)
        {
            return ((value / 1024d).ToString("0", CultureInfo.InvariantCulture), "K/s");
        }

        if (value < 1024d * 1024d * 1024d)
        {
            return ((value / (1024d * 1024d)).ToString("0.0", CultureInfo.InvariantCulture), "M/s");
        }

        return ((value / (1024d * 1024d * 1024d)).ToString("0.0", CultureInfo.InvariantCulture), "G/s");
    }
}
