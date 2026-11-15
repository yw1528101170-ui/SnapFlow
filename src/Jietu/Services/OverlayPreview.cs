namespace Jietu;

/// <summary>
/// 离屏预览：把 OSD / Toast 的真实渲染结果合成到设计板与 2x 细节图上并导出 PNG，
/// 便于在无交互环境里核对视觉细节（仅开发/自检用，不参与常驻逻辑）。
/// </summary>
internal static class OverlayPreview
{
    private static readonly OsdRenderer.Metrics NormalMetrics = new()
    {
        Gpu = 37f,
        Temp = 62f,
        VramGb = 5.8f,
        Cpu = 23f,
        Ghz = 4.65f,
        Ram = 68f,
        RamUsedGb = 21.7f,
        Down = 12.4f * 1024f * 1024f,
        Up = 1.8f * 1024f * 1024f,
        Fps = 144f,
        FpsMeasured = true,
        RefreshHz = 144f,
    };

    private static readonly OsdRenderer.Metrics HotMetrics = new()
    {
        Gpu = 96f,
        Temp = 83f,
        VramGb = 10.2f,
        Cpu = 91f,
        Ghz = 5.12f,
        Ram = 88f,
        RamUsedGb = 28.4f,
        Down = 42.6f * 1024f * 1024f,
        Up = 6.3f * 1024f * 1024f,
        Fps = 61f,
        FpsMeasured = true,
        RefreshHz = 144f,
    };

    public static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var input = OverlayGdi.GdiplusStartupInput.Default;
        if (OverlayGdi.GdiplusStartup(out var token, ref input, IntPtr.Zero) != 0)
        {
            return 1;
        }

        try
        {
            RenderStates(Path.Combine(directory, "snapflow-overlay-states.png"));
            RenderBoard(Path.Combine(directory, "snapflow-overlay-preview.png"));
            RenderDetail(Path.Combine(directory, "snapflow-overlay-detail.png"));
            RenderMotion(Path.Combine(directory, "snapflow-overlay-motion.png"));
            RenderLiquid(Path.Combine(directory, "snapflow-overlay-liquid.png"));
            RenderTrayMenu(Path.Combine(directory, "snapflow-tray-menu.png"));
            Console.WriteLine("preview ok");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            OverlayGdi.GdiplusShutdown(token);
        }
    }

    /// <summary>形态对照：紧凑态 → 展开中 → 详细态，1:1 渲染。</summary>
    private static void RenderStates(string path)
    {
        var rows = new (float Expand, string Caption)[]
        {
            (0f, "紧凑（常态）"),
            (0.35f, "展开 35%"),
            (0.7f, "展开 70%"),
            (1f, "详细（悬停）"),
        };

        const int width = 720;
        const int rowHeight = 92;
        int height = rowHeight * rows.Length + 16;

        using var canvas = new LayeredCanvas();
        if (!canvas.Ensure(width, height))
        {
            return;
        }

        var g = canvas.Graphics;
        OverlayGdi.Clear(g);
        FillVertical(g, 0f, 0f, width, height, 0xFFAECDDD, 0xFF93B6C9);

        var labelFont = OverlayGdi.TextFont(12f, bold: true);
        for (int i = 0; i < rows.Length; i++)
        {
            var (expand, caption) = rows[i];
            float rowTop = 8f + i * rowHeight;
            OverlayGdi.DrawText(g, caption, 8f, rowTop + 4f, 220f, 18f, labelFont, OverlayGdi.AlignNear, 0xFF1B2A33);

            var frame = OsdRenderer.Layout(1f, expand, NormalMetrics) with { Reveal = 1f };
            DrawOsd(g, frame, NormalMetrics, (width - frame.BufferWidth) * 0.5f, rowTop + 26f);
        }

        SavePng(canvas, path);
    }

    /// <summary>设计板：顶部居中的紧凑态 / 详细态 / 高压态，暗底与浅底下的对照。</summary>
    private static void RenderBoard(string path)
    {
        const int width = 1280;
        const int height = 440;
        using var canvas = new LayeredCanvas();
        if (!canvas.Ensure(width, height))
        {
            return;
        }

        var g = canvas.Graphics;
        OverlayGdi.Clear(g);
        FillVertical(g, 0f, 0f, width, height, 0xFF161E28, 0xFF05070A);
        FillVertical(g, 0f, 0f, width, 190f, 0x145AC8FA, 0x00000000);
        var topBar = OverlayGdi.BuildRoundRect(0f, 0f, width, 24f, 0f);
        OverlayGdi.FillPath(g, topBar, 0xFF0C1015);
        OverlayGdi.GdipDeletePath(topBar);

        // 贴屏幕顶边正中：详细态（悬停时）
        var osd = OsdRenderer.Layout(1f, 1f, NormalMetrics) with { Reveal = 1f };
        DrawOsd(g, osd, NormalMetrics, (width - osd.BufferWidth) * 0.5f, 0f);

        // 常态：一小条紧凑态
        var osdCompact = OsdRenderer.Layout(1f, 0f, NormalMetrics) with { Reveal = 1f };
        DrawOsd(g, osdCompact, NormalMetrics, (width - osdCompact.BufferWidth) * 0.5f, 88f);

        // 高压态：数值转红、细轨变红，验证告警配色在展开态下的可读性
        var osdHot = OsdRenderer.Layout(1f, 1f, HotMetrics) with { Reveal = 1f };
        DrawOsd(g, osdHot, HotMetrics, (width - osdHot.BufferWidth) * 0.5f, 152f);

        // 浅色底带：验证深色玻璃胶囊在明亮背景下的可读性
        FillVertical(g, 0f, 268f, width, height - 268f, 0xFFF3F4F7, 0xFFDCE0E6);

        DrawToast(g, "截图已复制到剪贴板", ToastKind.Success, busy: false, right: 1246f, bottom: 232f);
        DrawToast(g, "写入剪贴板失败，请重试", ToastKind.Warning, busy: false, right: 1246f, bottom: 336f);
        DrawToast(g, "正在识别文字…", ToastKind.Info, busy: true, right: 1246f, bottom: 400f, spin: 46f);
        DrawToast(g, "已复制颜色：#2D6EFA", ToastKind.Success, busy: false, right: 620f, bottom: 336f);

        SavePng(canvas, path);
    }

    /// <summary>进出视野的分镜：每帧单独渲染到自己的画布再贴上来，避免世界变换互相污染。</summary>
    private static void RenderMotion(string path)
    {
        const int width = 860;
        const int rowHeight = 96;
        const int rows = 5;
        const int height = rowHeight * rows + 12;
        using var canvas = new LayeredCanvas();
        if (!canvas.Ensure(width, height))
        {
            return;
        }

        var g = canvas.Graphics;
        OverlayGdi.Clear(g);
        // 壁纸感渐变：卡片是纯黑，深色底会让轮廓糊掉，浅到中灰最能看清形体压缩
        FillVertical(g, 0f, 0f, width, height, 0xFFB9C4D6, 0xFF5D6A80);

        var osd = OsdRenderer.Layout(1f, 0f, NormalMetrics) with { Reveal = 1f };
        float[] reveals = [1f, 0.8f, 0.55f, 0.3f, 0.1f];
        var labelFont = OverlayGdi.TextFont(11f, bold: true);

        for (int i = 0; i < reveals.Length; i++)
        {
            float rowTop = 6f + i * rowHeight;
            using var frameCanvas = new LayeredCanvas();
            if (!frameCanvas.Ensure(osd.BufferWidth, osd.BufferHeight))
            {
                continue;
            }

            OverlayGdi.Clear(frameCanvas.Graphics);
            OsdRenderer.Draw(frameCanvas.Graphics, osd with { Reveal = reveals[i] }, NormalMetrics, cacheChrome: false);
            // 居中形态的入场是纵向落位，分镜里也让它从顶边中心长出来
            OverlayGdi.GdipDrawImage(g, frameCanvas.Image, (width - osd.BufferWidth) * 0.5f, rowTop);

            OverlayGdi.DrawText(g, $"reveal {reveals[i]:0.00}", 14f, rowTop + 34f, 110f, 16f, labelFont,
                OverlayGdi.AlignNear, 0xF21A2330);
        }

        SavePng(canvas, path);
    }

    /// <summary>2x 细节图：核对 200% DPI 下的字形清晰度、缺口形曲率与浮层阴影。</summary>
    private static void RenderDetail(string path)
    {
        const int width = 1040;
        const int height = 486;
        const float scale = 2f;

        using var canvas = new LayeredCanvas();
        if (!canvas.Ensure(width, height))
        {
            return;
        }

        var g = canvas.Graphics;
        OverlayGdi.Clear(g);
        // 上半段浅色底，便于看清缺口形的直角顶边与底部圆角；下半段深色底检验卡片对比度
        FillVertical(g, 0f, 0f, width, 172f, 0xFFEEF0F4, 0xFFD8DBE1);
        FillVertical(g, 0f, 172f, width, height - 172f, 0xFF151D27, 0xFF06080B);

        // 2x 下的展开态：核对字形清晰度、缺口形圆角与阴影
        var osd = OsdRenderer.Layout(scale, 1f, NormalMetrics) with { Reveal = 1f };
        DrawOsd(g, osd, NormalMetrics, (width - osd.BufferWidth) * 0.5f, 18f);

        DrawToast(g, "截图已复制到剪贴板", ToastKind.Success, busy: false, right: 1000f, bottom: 340f, scale: scale);
        DrawToast(g, "正在识别文字…", ToastKind.Info, busy: true, right: 1000f, bottom: 470f, spin: 52f, scale: scale);

        SavePng(canvas, path);
    }

    /// <summary>
    /// 液态玻璃：同一块玻璃摆到三种壁纸前（明亮 / 花色夜景 / 近黑），
    /// 卡片是当场从背后那一层像素算出来的——模糊范围、边缘折射、乳白程度全部跟着背景变。
    /// </summary>
    private static void RenderLiquid(string path)
    {
        const int width = 1180;
        const int bandHeight = 218;
        const int bands = 3;
        const int height = bandHeight * bands;
        const float scale = 2f;

        using var canvas = new LayeredCanvas();
        if (!canvas.Ensure(width, height))
        {
            return;
        }

        var wall = new byte[width * height * 4];
        for (int py = 0; py < height; py++)
        {
            int variant = Math.Clamp(py / bandHeight, 0, bands - 1);
            for (int px = 0; px < width; px++)
            {
                float v = (py - variant * bandHeight) / (float)bandHeight;
                WallPixel(variant, px / (float)width, v, out byte wr, out byte wg, out byte wb);
                int i = (py * width + px) * 4;
                wall[i + 0] = wb;
                wall[i + 1] = wg;
                wall[i + 2] = wr;
                wall[i + 3] = 255;
            }
        }

        System.Runtime.InteropServices.Marshal.Copy(wall, 0, canvas.Bits, wall.Length);

        var g = canvas.Graphics;
        var labelFont = OverlayGdi.TextFont(12f, bold: true);
        string[] captions = ["明亮壁纸", "花色夜景", "近黑壁纸"];

        var frame = OsdRenderer.Layout(scale, 1f, NormalMetrics) with { Reveal = 1f };
        float x = (width - frame.BufferWidth) * 0.5f;

        for (int i = 0; i < bands; i++)
        {
            float y = i * bandHeight + 42f;
            OverlayGdi.DrawText(g, captions[i], 16f, y + 6f, 200f, 18f, labelFont, OverlayGdi.AlignNear,
                i == 0 ? 0xFF23303C : 0xFFEDEDED);
            DrawGlassOsd(g, frame, NormalMetrics, x, y, scale, width, bandHeight, i);
        }

        SavePng(canvas, path);
    }

    /// <summary>合成壁纸的解析函数：明亮 / 花色夜景 / 近黑三种，都带横向亮带和竖向细纹，
    /// 用来检验模糊与边缘折射是不是真的发生。</summary>
    private static void WallPixel(int variant, float u, float v, out byte r, out byte g, out byte b)
    {
        float cr;
        float cg;
        float cb;

        if (variant == 0)
        {
            // 明亮：米白 → 浅蓝，右上一团强日光，下面压一条冷影
            cr = 236f - 34f * v;
            cg = 238f - 30f * v;
            cb = 243f - 22f * v;
            float dx = (u - 0.70f) * 1.5f;
            float dy = (v - 0.22f) * 2.4f;
            float sun = MathF.Exp(-(dx * dx + dy * dy) * 5f);
            cr += 24f * sun;
            cg += 18f * sun;
            cb += 6f * sun;
            cr -= 40f * Math.Clamp((v - 0.62f) * 3f, 0f, 1f);
            cg -= 46f * Math.Clamp((v - 0.62f) * 3f, 0f, 1f);
            cb -= 30f * Math.Clamp((v - 0.62f) * 3f, 0f, 1f);
        }
        else if (variant == 1)
        {
            // 花色夜景：深海蓝底 + 暖光 + 冷光
            float t = Math.Clamp(v * 1.15f, 0f, 1f);
            cr = 30f * (1f - t) + 10f * t;
            cg = 44f * (1f - t) + 14f * t;
            cb = 70f * (1f - t) + 26f * t;
            float dx = (u - 0.72f) * 1.7f;
            float dy = (v - 0.16f) * 2.6f;
            float warm = MathF.Exp(-(dx * dx + dy * dy) * 5.5f);
            cr += 215f * warm;
            cg += 128f * warm;
            cb += 44f * warm;
            dx = (u - 0.16f) * 1.8f;
            dy = (v - 0.78f) * 2.9f;
            float cool = MathF.Exp(-(dx * dx + dy * dy) * 6.5f);
            cr += 26f * cool;
            cg += 158f * cool;
            cb += 190f * cool;
        }
        else
        {
            // 近黑：几乎纯暗，只有一束很窄的高光斜穿，考验乳白自动提亮
            float t = Math.Clamp(v, 0f, 1f);
            cr = 16f * (1f - t) + 5f * t;
            cg = 18f * (1f - t) + 6f * t;
            cb = 24f * (1f - t) + 9f * t;
            float dx = (u - 0.42f);
            float dy = (v - 0.5f) * 3.4f;
            float gl = MathF.Exp(-(dx * dx + dy * dy) * 9f);
            cr += 60f * gl;
            cg += 74f * gl;
            cb += 92f * gl;
        }

        float band = MathF.Exp(-MathF.Pow((v - 0.34f) * 18f, 2f));
        cr += 26f * band;
        cg += 25f * band;
        cb += 22f * band;

        float stripe = 0.5f + 0.5f * MathF.Sin(u * 42f + 0.6f);
        cr += 9f * stripe;
        cg += 9f * stripe;
        cb += 8f * stripe;

        r = (byte)Math.Clamp(cr, 0f, 255f);
        g = (byte)Math.Clamp(cg, 0f, 255f);
        b = (byte)Math.Clamp(cb, 0f, 255f);
    }

    /// <summary>把玻璃背景算成卡片图像、喂给渲染器，再照常画 OSD。</summary>
    private static void DrawGlassOsd(IntPtr g, OsdRenderer.Frame frame, OsdRenderer.Metrics metrics,
        float x, float y, float scale, int wallW, int bandH, int variant)
    {
        float pad = OsdRenderer.CardHorizontal(frame).X;
        int cardW = (int)MathF.Round(frame.CardWidth);
        int cardH = (int)MathF.Round(frame.CardHeight);
        float cardXpx = x + pad;

        // 背景要比卡片四周各多出一段，边缘折射才有东西可取
        int margin = (int)MathF.Ceiling(GlassSurface.LensWidth(scale) * 1.6f);
        int bw = cardW + margin * 2;
        int bh = cardH + margin * 2;
        var backdrop = new byte[bw * bh * 4];
        int bandTop = variant * bandH;
        for (int j = 0; j < bh; j++)
        {
            int sy = (int)y - margin + j;
            float v = (sy - bandTop) / (float)bandH;
            for (int i = 0; i < bw; i++)
            {
                int sx = (int)cardXpx - margin + i;
                WallPixel(variant, Math.Clamp(sx, 0, wallW - 1) / (float)wallW, Math.Clamp(v, 0f, 1f),
                    out byte wr, out byte wg, out byte wb);
                int o = (j * bw + i) * 4;
                backdrop[o + 0] = wb;
                backdrop[o + 1] = wg;
                backdrop[o + 2] = wr;
                backdrop[o + 3] = 255;
            }
        }

        // 和实时窗口一致：全分辨率合成（半分辨率那条路径会把 1px 的镜面亮边拉成 2px 粗线，
        // 且「自适应提亮」在缩放缓冲上做和逐像素做结果不同，预览就会和实机不是一个亮度）
        var card = new GlassSurface.Card(margin, margin, cardW, cardH, OsdRenderer.BottomRadius(cardH));
        var glass = new byte[cardW * cardH * 4];
        GlassSurface.Compose(backdrop, bw, bh, card, GlassSurface.LensWidth(scale),
            GlassSurface.BlurRadius(scale, cardH), 0.42f, glass,
            pxScale: scale);

        using var glassCanvas = new LayeredCanvas();
        GlassSurface.Upload(glass, cardW, cardH, glassCanvas);

        DrawOsd(g, frame, metrics, x, y, glassCanvas.Image);
    }

    private static void DrawOsd(IntPtr g, OsdRenderer.Frame frame, OsdRenderer.Metrics metrics, float x, float y,
        IntPtr glassImage = default)
    {
        var saved = OverlayGdi.SaveGraphics(g);
        OverlayGdi.Translate(g, x, y);
        OsdRenderer.Draw(g, frame, metrics, glassImage);
        OverlayGdi.RestoreGraphics(g, saved);
    }

    private static void DrawToast(IntPtr g, string message, ToastKind kind, bool busy, float right, float bottom,
        float spin = 0f, float presence = 1f, float scale = 1f)
    {
        float contentWidth = ToastRenderer.MeasureWidth(g, scale, message);
        var (w, h) = ToastRenderer.BufferSize(scale, contentWidth * 1.06f);
        var state = new ToastRenderer.State(message, kind, busy, contentWidth, presence, spin);

        var saved = OverlayGdi.SaveGraphics(g);
        OverlayGdi.Translate(g, right - w, bottom - h);
        ToastRenderer.Draw(g, scale, state, w, h);
        OverlayGdi.RestoreGraphics(g, saved);
    }

    private static void FillVertical(IntPtr g, float x, float y, float w, float h, uint top, uint bottom)
    {
        var path = OverlayGdi.BuildRoundRect(x, y, w, h, 0f);
        OverlayGdi.FillPathVertical(g, path, top, bottom, x + w * 0.5f, y, y + h);
        OverlayGdi.GdipDeletePath(path);
    }

    /// <summary>
    /// 托盘菜单：用和运行时一模一样的版式与玻璃引擎，铺在一块合成桌面上渲染出来。
    /// 菜单本身在运行时是不参与抓屏的（否则会把自己的卡面糊进背景），所以只能这样看它长什么样。
    /// </summary>
    private static void RenderTrayMenu(string path)
    {
        const float scale = 1.5f;
        const int margin = 44;

        var ctx = new TrayMenuContext(
            Paused: false,
            RunAtStartup: true,
            RegionHotkey: "Ctrl+Shift+A",
            HistoryHotkey: "Ctrl+Shift+V",
            PinCount: 2,
            HasGhostPins: false,
            ShowOsd: true,
            OsdDisplay: "Primary",
            VaultHotkey: "Ctrl+Shift+L",
            VaultUnlocked: true);

        var rows = TrayMenuModel.Build(ctx);
        var layout = TrayMenuRenderer.Measure(rows, scale);
        int width = layout.Width + margin * 2;
        int height = layout.Height + margin * 2;

        using var canvas = new LayeredCanvas();
        if (!canvas.Ensure(width, height))
        {
            return;
        }

        var g = canvas.Graphics;
        OverlayGdi.Clear(g);
        TrayMenuRenderer.MeasureChips(layout, g);

        // 合成桌面：冷色渐变 + 两块柔光，让磨砂与边缘折射看得出来
        var backdrop = BuildBackdrop(width, height);
        var pinned = System.Runtime.InteropServices.GCHandle.Alloc(backdrop,
            System.Runtime.InteropServices.GCHandleType.Pinned);
        IntPtr backdropImage = IntPtr.Zero;
        try
        {
            if (OverlayGdi.GdipCreateBitmapFromScan0(width, height, width * 4, OverlayGdi.PixelFormat32bppPARGB,
                    pinned.AddrOfPinnedObject(), out backdropImage) == 0 && backdropImage != IntPtr.Zero)
            {
                OverlayGdi.GdipDrawImage(g, backdropImage, 0f, 0f);
            }
        }
        finally
        {
            pinned.Free();
        }

        // 托盘菜单已不带玻璃底（只保留 OSD 一处自研玻璃），预览与实机一致：直接白板卡面
        int hoverRow = rows.FindIndex(r => r.Label == "剪贴板历史");
        OverlayGdi.Translate(g, margin, margin);
        TrayMenuRenderer.Paint(g, layout, IntPtr.Zero, hoverRow, -1, -1);
        OverlayGdi.Translate(g, -margin, -margin);

        if (backdropImage != IntPtr.Zero)
        {
            OverlayGdi.GdipDisposeImage(backdropImage);
        }

        SavePng(canvas, path);
    }

    /// <summary>合成一块「桌面」：冷色渐变 + 柔光 + 一条斜向亮带 + 角部暗块，用作玻璃背景。</summary>
    private static byte[] BuildBackdrop(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var t = y / (float)Math.Max(1, height - 1);
            var r = (byte)(214 + (168 - 214) * t);
            var gch = (byte)(224 + (188 - 224) * t);
            var b = (byte)(238 + (206 - 238) * t);
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var warm = MathF.Max(0f, 1f - MathF.Sqrt((x - width * 0.78f) * (x - width * 0.78f) +
                                                          (y - height * 0.22f) * (y - height * 0.22f)) / (width * 0.55f));
                var cool = MathF.Max(0f, 1f - MathF.Sqrt((x - width * 0.2f) * (x - width * 0.2f) +
                                                          (y - height * 0.8f) * (y - height * 0.8f)) / (width * 0.5f));

                // 斜向亮带：玻璃的边缘折射与磨砂能在这里看出层次
                var diagonal = x * 0.8f + y * 0.6f;
                var band = MathF.Max(0f, 1f - MathF.Abs(diagonal - (width * 0.7f + height * 0.35f)) / (width * 0.18f));
                band = OverlayGdi.Smooth01(band) * 34f;

                // 右下角压暗，让卡面右下边缘的亮棱有对比
                var corner = MathF.Max(0f, MathF.Min((x - width * 0.55f) / (width * 0.45f),
                    (y - height * 0.55f) / (height * 0.45f)));

                var rr = r + warm * 26f + band - corner * 46f;
                var gg = gch + warm * 6f + cool * 5f + band * 0.9f - corner * 40f;
                var bb = b - warm * 22f + cool * 16f + band * 0.8f - corner * 30f;
                pixels[i] = (byte)Math.Clamp((int)rr, 0, 255);
                pixels[i + 1] = (byte)Math.Clamp((int)gg, 0, 255);
                pixels[i + 2] = (byte)Math.Clamp((int)bb, 0, 255);
                pixels[i + 3] = 255;
            }
        }

        return pixels;
    }

    private static void SavePng(LayeredCanvas canvas, string path)
    {
        // PNG 编码器固定 CLSID（{557CF406-1A04-11D3-9A73-0000F81EF32E}）
        var encoder = new Guid("557CF406-1A04-11D3-9A73-0000F81EF32E");
        if (canvas.Image != IntPtr.Zero)
        {
            OverlayGdi.GdipSaveImageToFile(canvas.Image, path, ref encoder, IntPtr.Zero);
        }
    }
}
