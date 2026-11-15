namespace Jietu.Tests;

/// <summary>
/// 液态玻璃的像素引擎。锁的是「算错了也看不出来、只会悄悄糊一片」的行为：
/// 形状距离算错会让边缘 alpha 满到轮廓外（一块硬边的塑料片）；
/// 降采样那条路径若和原尺寸路径差太多，展开动画中间的玻璃就会跳一下；
/// 背景越暗自适应提亮越强，否则深色壁纸下墨字直接读不出。
/// </summary>
public sealed class GlassSurfaceTests
{
    private static byte[] SolidBackdrop(int width, int height, byte b, byte g, byte r)
    {
        var buffer = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            buffer[i * 4 + 0] = b;
            buffer[i * 4 + 1] = g;
            buffer[i * 4 + 2] = r;
            buffer[i * 4 + 3] = 255;
        }

        return buffer;
    }

    private static byte AlphaAt(byte[] pixels, int width, int x, int y)
        => pixels[(y * width + x) * 4 + 3];

    [Fact]
    public void Compose_FillsInteriorAndFadesAtSilhouette()
    {
        const int cardW = 120;
        const int cardH = 40;
        int margin = 12;
        int sw = cardW + margin * 2;
        int sh = cardH + margin * 2;

        var src = SolidBackdrop(sw, sh, 90, 110, 130);
        var dst = new byte[cardW * cardH * 4];
        var card = new GlassSurface.Card(margin, margin, cardW, cardH, 12f);

        GlassSurface.Compose(src, sw, sh, card, 8f, 6f, 0.4f, dst);

        // 正中一定要是不透明的玻璃
        Assert.Equal(255, AlphaAt(dst, cardW, cardW / 2, cardH / 2));

        // 下角圆弧之外必须是透明的（否则轮廓外会糊出一块灰）
        Assert.Equal(0, AlphaAt(dst, cardW, 1, cardH - 1));

        // 左边缘 1px 之内是半透明过渡，不能一刀切
        var edge = AlphaAt(dst, cardW, 0, cardH / 2);
        Assert.True(edge < 255, $"边缘应带抗锯齿过渡，实际 alpha={edge}");
    }

    [Fact]
    public void Compose_DarkBackdropGetsMilkyButKeepsHue()
    {
        const int cardW = 100;
        const int cardH = 36;
        int margin = 10;
        int sw = cardW + margin * 2;
        int sh = cardH + margin * 2;

        var dark = SolidBackdrop(sw, sh, 8, 10, 14);
        var bright = SolidBackdrop(sw, sh, 200, 210, 220);
        var card = new GlassSurface.Card(margin, margin, cardW, cardH, 10f);

        var darkOut = new byte[cardW * cardH * 4];
        var brightOut = new byte[cardW * cardH * 4];
        GlassSurface.Compose(dark, sw, sh, card, 8f, 6f, 0.4f, darkOut);
        GlassSurface.Compose(bright, sw, sh, card, 8f, 6f, 0.4f, brightOut);

        int o = ((cardH / 2) * cardW + cardW / 2) * 4;
        float darkLum = (darkOut[o] + darkOut[o + 1] + darkOut[o + 2]) / 3f;
        float brightLum = (brightOut[o] + brightOut[o + 1] + brightOut[o + 2]) / 3f;

        // 深色背景下玻璃必须自己提亮，否则墨字读不出；但不能亮到和亮背景一样
        Assert.True(darkLum > 120f, $"深色背景下的玻璃亮度只有 {darkLum:0}");
        Assert.True(darkLum < brightLum);
    }

    [Fact]
    public void Compose_TopRoundedClearsTopCorners()
    {
        // 浮在桌面中间的卡片（托盘菜单）：四个角都得是圆的，
        // 顶角若不按圆角算，轮廓外的 alpha 会满成一整块方的白角。
        const int cardW = 120;
        const int cardH = 80;
        int margin = 16;
        int sw = cardW + margin * 2;
        int sh = cardH + margin * 2;

        var src = SolidBackdrop(sw, sh, 90, 110, 130);
        var card = new GlassSurface.Card(margin, margin, cardW, cardH, 14f, TopRounded: true);
        var rounded = new byte[cardW * cardH * 4];
        GlassSurface.Compose(src, sw, sh, card, 10f, 8f, 0.4f, rounded);

        var flat = new GlassSurface.Card(margin, margin, cardW, cardH, 14f);
        var squareTop = new byte[cardW * cardH * 4];
        GlassSurface.Compose(src, sw, sh, flat, 10f, 8f, 0.4f, squareTop);

        // 顶角外侧那一点：圆角版必须透明，贴屏幕边的版本仍是实心
        Assert.True(AlphaAt(rounded, cardW, 0, 0) < 20,
            $"顶角外侧 alpha 应为 0，实际 {AlphaAt(rounded, cardW, 0, 0)}");
        Assert.True(AlphaAt(squareTop, cardW, 0, 0) > 180,
            $"顶边齐平的卡片不该在这里透明，实际 {AlphaAt(squareTop, cardW, 0, 0)}");

        // 圆角内侧、以及四边中段都还是实心
        Assert.Equal(255, AlphaAt(rounded, cardW, cardW / 2, cardH / 2));
        Assert.True(AlphaAt(rounded, cardW, 6, 6) > 200);
        Assert.True(AlphaAt(rounded, cardW, cardW / 2, 0) > 200);
    }

    [Fact]
    public void Compose_DownsampledPathStaysCloseToExactPath()
    {
        const int cardW = 96;
        const int cardH = 32;
        // 留足边距：玻璃的磨砂半径是 6~8px，贴着测试画布边缘会把边界钳制算进结果，
        // 那是测试布置的问题，不是降采样路径的问题
        int margin = 24;
        int sw = cardW + margin * 2;
        int sh = cardH + margin * 2;

        // 带结构而不是纯色的背景，才能看出降采样有没有把明暗抹平；
        // 频率取真实壁纸的量级（大块明暗），而不是贴着磨砂半径的高频条纹
        var src = new byte[sw * sh * 4];
        for (int y = 0; y < sh; y++)
        {
            for (int x = 0; x < sw; x++)
            {
                int i = (y * sw + x) * 4;
                double v = 120 + 90 * Math.Sin(x * 0.03) * Math.Cos(y * 0.05);
                src[i + 0] = (byte)Math.Clamp(v, 0, 255);
                src[i + 1] = (byte)Math.Clamp(v * 0.9, 0, 255);
                src[i + 2] = (byte)Math.Clamp(v * 0.8, 0, 255);
                src[i + 3] = 255;
            }
        }

        var card = new GlassSurface.Card(margin, margin, cardW, cardH, 10f);
        var exact = new byte[cardW * cardH * 4];
        var fast = new byte[cardW * cardH * 4];

        GlassSurface.Compose(src, sw, sh, card, 8f, 6f, 0.4f, exact);
        GlassSurface.Compose(src, sw, sh, card, 8f, 6f, 0.4f, fast,
            new byte[(sw / 2) * (sh / 2) * 4], downsample: 2);

        int samples = 0;
        float total = 0f;
        for (int y = 4; y < cardH - 4; y += 3)
        {
            for (int x = 4; x < cardW - 4; x += 3)
            {
                int o = (y * cardW + x) * 4;
                total += MathF.Abs(exact[o + 1] - fast[o + 1]);
                samples++;
            }
        }
        float mean = total / samples;
        Assert.True(mean < 6f, $"降采样路径与原尺寸路径平均差 {mean:0.0}，超过 6 会看出差别");
        Assert.Equal(255, AlphaAt(fast, cardW, cardW / 2, cardH / 2));
    }
}




