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
            RenderShapes(Path.Combine(directory, "snapflow-overlay-shapes.png"));
            RenderBoard(Path.Combine(directory, "snapflow-overlay-preview.png"));
            RenderDetail(Path.Combine(directory, "snapflow-overlay-detail.png"));
            RenderMotion(Path.Combine(directory, "snapflow-overlay-motion.png"));
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

    /// <summary>形状对比：4 种自由端端部几何，1:1 渲染，用于挑选最"融进屏幕边"的一种。</summary>
    private static void RenderShapes(string path)
    {
        const int width = 700;
        const int height = 340;
        using var canvas = new LayeredCanvas();
        if (!canvas.Ensure(width, height))
        {
            return;
        }

        var g = canvas.Graphics;
        OverlayGdi.Clear(g);
        FillVertical(g, 0f, 0f, width, height, 0xFFAECDDD, 0xFF93B6C9);

        var osd = OsdRenderer.Layout(1f, topRight: true, NormalMetrics);
        (float Top, float Bottom, string Caption)[] variants =
        [
            (34f, 34f, "1  对称 S"),
            (22f, 24f, "2  小倒角+直边"),
            (30f, 38f, "3  偏心 S"),
            (0f, 34f, "4  直角+圆角"),
        ];

        var labelFont = OverlayGdi.TextFont(12f, bold: true);
        float y = 8f;
        foreach (var (top, bottom, caption) in variants)
        {
            OverlayGdi.DrawText(g, caption, 8f, y + 24f, 84f, 20f, labelFont, OverlayGdi.AlignNear, 0xFF1B2A33);

            var frame = osd with { EndTop = top, EndBottom = bottom, Reveal = 1f };
            float cardX = frame.BufferWidth - frame.CardWidth;
            var saved = OverlayGdi.SaveGraphics(g);
            OverlayGdi.Translate(g, 96f - cardX, y);
            OsdRenderer.Draw(g, frame, NormalMetrics);
            OverlayGdi.RestoreGraphics(g, saved);
            y += 82f;
        }

        SavePng(canvas, path);
    }

    /// <summary>设计板：两种停靠位、正常/高压状态、暗底与浅底下的胶囊。</summary>
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

        var osd = OsdRenderer.Layout(1f, topRight: true, NormalMetrics);
        DrawOsd(g, osd, NormalMetrics, width - osd.BufferWidth, 0f);

        var osdLeft = OsdRenderer.Layout(1f, topRight: false, HotMetrics);
        DrawOsd(g, osdLeft, HotMetrics, 0f, 88f);

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

        var osd = OsdRenderer.Layout(1f, topRight: true, NormalMetrics);
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
            OverlayGdi.GdipDrawImage(g, frameCanvas.Image, width - osd.BufferWidth - 10f, rowTop);

            OverlayGdi.DrawText(g, $"reveal {reveals[i]:0.00}", 14f, rowTop + 34f, 110f, 16f, labelFont,
                OverlayGdi.AlignNear, 0xF21A2330);
        }

        SavePng(canvas, path);
    }

    /// <summary>2x 细节图：核对 200% DPI 下的字形清晰度、岛体曲率与浮层阴影。</summary>
    private static void RenderDetail(string path)
    {
        const int width = 940;
        const int height = 486;
        const float scale = 2f;

        using var canvas = new LayeredCanvas();
        if (!canvas.Ensure(width, height))
        {
            return;
        }

        var g = canvas.Graphics;
        OverlayGdi.Clear(g);
        // 上半段浅色底，便于看清岛体的凹弧/圆角轮廓；下半段深色底检验胶囊对比度
        FillVertical(g, 0f, 0f, width, 172f, 0xFFEEF0F4, 0xFFD8DBE1);
        FillVertical(g, 0f, 172f, width, height - 172f, 0xFF151D27, 0xFF06080B);

        // 2x 下岛体约 1600px 宽，这里只取自由端一段，看清倒角与圆角
        var osd = OsdRenderer.Layout(scale, topRight: false, NormalMetrics);
        DrawOsd(g, osd, NormalMetrics, 36f, 18f);

        DrawToast(g, "截图已复制到剪贴板", ToastKind.Success, busy: false, right: 900f, bottom: 340f, scale: scale);
        DrawToast(g, "正在识别文字…", ToastKind.Info, busy: true, right: 900f, bottom: 470f, spin: 52f, scale: scale);

        SavePng(canvas, path);
    }

    private static void DrawOsd(IntPtr g, OsdRenderer.Frame frame, OsdRenderer.Metrics metrics, float x, float y)
    {
        var saved = OverlayGdi.SaveGraphics(g);
        OverlayGdi.Translate(g, x, y);
        OsdRenderer.Draw(g, frame, metrics);
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
