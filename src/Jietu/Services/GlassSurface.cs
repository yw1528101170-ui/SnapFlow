using System.Runtime.InteropServices;

namespace Jietu;

/// <summary>
/// 液态玻璃的像素引擎：把「玻璃后面的画面」重算成透过这块玻璃看到的样子。
/// 一共三步——大半径模糊（磨砂）、边缘沿法线向外取样（透镜折射，外面的内容被压到边上）、
/// 面层提亮去色 + 方向光亮棱（光从上方来：顶边镜面高光最足，底边只剩一抹并带内阴影，
/// 折射带里带白光色散的彩虹细纹，即 Apple Liquid Glass 的光感）。
/// 输出 PREMULTIPLIED BGRA，边缘的 alpha 由形状距离算出，天然抗锯齿，
/// 所以画的时候不需要裁剪路径（SetClip 不走抗锯齿，会啃出锯齿边）。
/// </summary>
internal static class GlassSurface
{
    /// <summary>
    /// 卡片在背景图里的位置（物理像素）。Radius 为四角圆角。
    /// <paramref name="TopRounded"/> 为 false 时顶边当作与屏幕物理边缘齐平（OSD 就是这样贴在屏幕上沿），
    /// 顶角不做圆角、也不在顶边做折射；浮在桌面中间的卡片必须置 true，否则两个顶角会留下方的白角。
    /// <paramref name="EdgeGlow"/> 是轮廓上镜面高光与内沿亮棱的强度：OSD 用 1 保持「玻璃有厚度」，
    /// 浮在桌面上的面板调到 0.3 左右，才不会像给卡片描了一圈白边。
    /// </summary>
    internal readonly record struct Card(int X, int Y, int W, int H, float Radius, bool TopRounded = false,
        float EdgeGlow = 1f);

    /// <summary>玻璃面层的最低亮度：背后的画面越暗，玻璃自动越乳白，保证墨字读得出。
    /// 172 会在中等偏暗的壁纸上就把整面推成奶白，玻璃态全无；压到 150 仍兜得住深色桌面。</summary>
    private const float MinLuminance = 150f;

    /// <summary>折射带的宽度：从轮廓往内多少像素开始被拉伸。</summary>
    internal static float LensWidth(float scale) => 10f * scale;

    /// <summary>模糊半径：液态玻璃是重度磨砂，但还要看得出背后的明暗，取卡片高度的 1/8。</summary>
    internal static float BlurRadius(float scale, float cardHeight) => MathF.Max(5f * scale, cardHeight / 8f);

    /// <summary>
    /// 合成一块玻璃。<paramref name="src"/> 是背景（BGRA，四边必须比卡片各多出
    /// <see cref="LensWidth"/> 的余量，否则边缘取样会越界），<paramref name="dst"/> 是输出图像。
    /// <paramref name="downsample"/> &gt; 1 时先按这个倍数缩小模糊源，重磨砂看不出差别，
    /// 但模糊成本掉到 1/downsample²；<paramref name="scratch"/> 是缩小时的临时缓冲，可复用。
    /// <paramref name="srcScale"/> &gt; 1 表示「一个输出像素覆盖 srcScale 个背景像素」——
    /// 玻璃本来就要糊，输出按半分辨率算再拉伸回卡片尺寸，主循环成本直接砍到 1/4。
    /// 注意：card 的 W/H/Radius 与 lensPx/blurPx 都按输出像素给，只有 card.X/Y 是源坐标。
    /// </summary>
    internal static void Compose(byte[] src, int sw, int sh, in Card card, float lensPx, float blurPx,
        float tint, byte[] dst, byte[]? scratch = null, int downsample = 1, int srcScale = 1, float pxScale = 1f)
    {
        int cw = card.W;
        int ch = card.H;
        if (cw <= 0 || ch <= 0 || sw <= 0 || sh <= 0)
        {
            return;
        }

        // 1) 磨砂：先把 alpha 抹成 255（BitBlt 抓回来的屏幕像素 alpha 是 0）。
        //    需要缩小时顺手在降采样里完成，省一遍全图遍历
        int ds = Math.Max(1, downsample);
        byte[] blur;
        int bw;
        int bh;
        if (ds == 1)
        {
            // 磨砂必须做在一份副本上：抓回来的屏幕像素后面还要用（同一帧里合成多块玻璃、
            // 或者同一张底图重复合成），原地改会把调用方的图提色、抹平，一次比一次白
            int need = sw * sh * 4;
            if (scratch == null || scratch.Length < need)
            {
                scratch = new byte[need];
            }

            Buffer.BlockCopy(src, 0, scratch, 0, Math.Min(src.Length, need));
            for (int i = 3; i < need; i += 4)
            {
                scratch[i] = 255;
            }

            blur = scratch;
            bw = sw;
            bh = sh;
        }
        else
        {
            bw = Math.Max(1, sw / ds);
            bh = Math.Max(1, sh / ds);
            int need = bw * bh * 4;
            if (scratch == null || scratch.Length < need)
            {
                scratch = new byte[need];
            }

            DownsampleBox(src, sw, sh, scratch, bw, bh, ds);
            blur = scratch;
        }

        // 两遍串联 = 三角核，比单遍方块核柔和得多，也不会有方块感
        int radius = Math.Max(1, (int)MathF.Round(blurPx / ds));
        BoxBlur(blur, bw, bh, radius, horizontal: true);
        BoxBlur(blur, bw, bh, radius, horizontal: false);
        BoxBlur(blur, bw, bh, Math.Max(1, radius / 2), horizontal: true);
        BoxBlur(blur, bw, bh, Math.Max(1, radius / 2), horizontal: false);

        // 面层配色（提白 / 去色 / 自适应提亮）在缩小后的缓冲上做一遍就够：
        // 面积只有 1/ds²，而主循环每像素只负责取样 + 边棱，成本能砍掉一大截
        TintBuffer(blur, bw * bh, tint);

        float lensW = MathF.Max(1f, lensPx);
        float lensShift = lensW * 1.1f;
        float invDs = 1f / ds;
        float scaleToSrc = MathF.Max(1, srcScale);

        // 镜面亮边的宽度按 DPI 折算。浮在桌面上的卡片（菜单）给更足的一道：
        // 它四边的背景明暗差别很大，边太细就会被浅色桌面吃掉一条腿；OSD 贴屏幕顶，弱一点反而自然。
        float specW = (card.TopRounded ? 1.35f : 1.1f) * MathF.Max(pxScale, 0.5f);
        float specPeak = (card.TopRounded ? 1f : 0.92f) * Math.Clamp(card.EdgeGlow, 0f, 1f);

        // 折射带的内边界：落在这块矩形里的像素离轮廓足够远，既没有折射也没有边棱，
        // 直接跳过形状距离计算（占九成像素，是这一层最值得省的地方）
        float innerX0 = lensW;
        float innerX1 = cw - lensW;
        float innerY0 = lensW;
        float innerY1 = ch - lensW;

        for (int y = 0; y < ch; y++)
        {
            float fy = y + 0.5f;
            int rowOut = y * cw * 4;
            // 上边界也要算：顶边参与折射的卡片（菜单）若在这里被当成「深处」，
            // 整条顶边就不会有亮棱和镜面高光，一圈亮边缺一条腿
            bool rowBorder = fy > innerY1 || (card.TopRounded && fy < innerY0);

            for (int x = 0; x < cw; x++)
            {
                float fx = x + 0.5f;
                float dist;
                float nx;
                float ny;

                if (!rowBorder && fx >= innerX0 && fx <= innerX1)
                {
                    dist = lensW;
                    nx = 0f;
                    ny = 0f;
                }
                else
                {
                    Distance(card, fx, fy, out dist, out nx, out ny);
                }

                // 2) 折射：越靠边，取样点越往外推，于是轮廓附近的画面被压扁、聚成一条亮带
                float t = OverlayGdi.Clamp01(1f - dist / lensW);
                float shift = lensShift * t * t;
                SampleBilinear(blur, bw, bh,
                    (card.X + (fx - nx * shift) * scaleToSrc) * invDs,
                    (card.Y + (fy - ny * shift) * scaleToSrc) * invDs,
                    out float r, out float g, out float b);

                // 2.5) 边缘色散：白光进玻璃会被拆开，蓝光折得比红光狠——
                //      折射带内给红、蓝两路各采一次微错位的样本，得到 Apple 液态玻璃
                //      标志性的彩虹细纹。只在靠边的窄带里做，中央大片像素零开销。
                if (t > 0.05f)
                {
                    float k = t * 0.65f;
                    SampleBilinear(blur, bw, bh,
                        (card.X + (fx - nx * shift * 0.86f) * scaleToSrc) * invDs,
                        (card.Y + (fy - ny * shift * 0.86f) * scaleToSrc) * invDs,
                        out float rRed, out _, out _);
                    SampleBilinear(blur, bw, bh,
                        (card.X + (fx - nx * shift * 1.16f) * scaleToSrc) * invDs,
                        (card.Y + (fy - ny * shift * 1.16f) * scaleToSrc) * invDs,
                        out _, out _, out float bBlue);
                    r += (rRed - r) * k;
                    b += (bBlue - b) * k;
                }

                BoostLuminance(ref r, ref g, ref b);

                // 方向光：光从上方来（Apple Liquid Glass 的光感来源）。
                // ny 是指向玻璃内部的法线：顶边 +1（外向正对光）→ 1，底边 -1（背光）→ -1，侧边 0。
                float light = ny;

                // 轮廓效果的整体强度仍由 EdgeGlow 门控
                float relief = Math.Clamp(card.EdgeGlow, 0f, 1f);

                // 3) 斜向光泽：一整块玻璃总有一道从左上扫下来的反光
                float sheen = (1f - (fx / cw * 0.62f + fy / ch * 0.38f));
                if (sheen > 0f)
                {
                    float w2 = sheen * sheen * 0.13f;
                    r += (255f - r) * w2;
                    g += (255f - g) * w2;
                    b += (255f - b) * w2;
                }

                // 4) 内沿收边：贴着轮廓的一道亮棱，玻璃才有厚度；
                //    顶边收得亮、底边收得淡（背光面），玻璃的立体感从这里来
                float rim = OverlayGdi.Clamp01(1f - dist / (lensW * 0.75f));
                if (rim > 0f)
                {
                    float w3 = rim * rim * 0.80f * relief * (0.6f + 0.4f * (light * 0.5f + 0.5f));
                    r += (255f - r) * w3;
                    g += (255f - g) * w3;
                    b += (255f - b) * w3;
                }

                // 4.2) 底边内阴影：背光面不再提白，反而往里压暗——
                //      苹果玻璃「下缘更厚」的体积感就是这道影给的
                if (light < -0.25f && dist < lensW * 0.6f)
                {
                    float s = OverlayGdi.Clamp01(1f - dist / (lensW * 0.6f)) * (-light) * 0.14f * relief;
                    r *= 1f - s;
                    g *= 1f - s;
                    b *= 1f - s;
                }

                // 4.5) 轮廓上那一道镜面高光：e² 是细而锐的主线，+0.3e 补一点柔光晕；
                //      亮度按方向光分配——顶边全亮、侧边七成、底边只剩一抹，
                //      不再是四边等亮的描边（那是「贴了一圈白胶带」，不是一块被光照着的玻璃）。
                //      直接画进像素里（带 alpha 抗锯齿），不靠外面再叠一条描边。
                if (dist < specW)
                {
                    float e = 1f - MathF.Max(dist, 0f) / specW;
                    float w4 = (e * e + 0.30f * e) * (0.3f + 0.7f * (light * 0.5f + 0.5f)) * specPeak;
                    r += (255f - r) * w4;
                    g += (255f - g) * w4;
                    b += (255f - b) * w4;
                }

                // 5) 边缘的 alpha 由距离算出：0 处半透明、1px 内补满，等于 1px 抗锯齿
                float a = OverlayGdi.Clamp01(dist + 0.4f);

                int o = rowOut + x * 4;
                dst[o + 0] = (byte)Math.Clamp(b * a, 0f, 255f);
                dst[o + 1] = (byte)Math.Clamp(g * a, 0f, 255f);
                dst[o + 2] = (byte)Math.Clamp(r * a, 0f, 255f);
                dst[o + 3] = (byte)Math.Clamp(a * 255f, 0f, 255f);
            }
        }
    }

    /// <summary>
    /// 形状的有向距离（内部为正）+ 指向内部的法线。
    /// 顶边与屏幕物理边缘齐平，所以只有左右两侧和下沿（含两个圆角）参与折射。
    /// </summary>
    private static void Distance(in Card card, float x, float y, out float dist, out float nx, out float ny)
    {
        float w = card.W;
        float h = card.H;
        float rr = MathF.Min(card.Radius, MathF.Min(w * 0.5f, h));

        // 下沿两个圆角：落在圆角方框里就按圆算，法线取径向
        if (y > h - rr && (x < rr || x > w - rr))
        {
            float cx = x < rr ? rr : w - rr;
            float cy = h - rr;
            float dx = x - cx;
            float dy = y - cy;
            float len = MathF.Sqrt(dx * dx + dy * dy);
            if (len < 0.001f)
            {
                len = 0.001f;
            }

            dist = rr - len;
            nx = -dx / len;
            ny = -dy / len;
            return;
        }

        // 上沿两个圆角同上——只有浮在桌面里的卡片才需要（OSD 贴屏幕顶，顶角本来就看不见）
        if (card.TopRounded && y < rr && (x < rr || x > w - rr))
        {
            float cx = x < rr ? rr : w - rr;
            float cy = rr;
            float dx = x - cx;
            float dy = y - cy;
            float len = MathF.Sqrt(dx * dx + dy * dy);
            if (len < 0.001f)
            {
                len = 0.001f;
            }

            dist = rr - len;
            nx = -dx / len;
            ny = -dy / len;
            return;
        }

        float dLeft = x;
        float dRight = w - x;
        float dBottom = h - y;
        float dTop = card.TopRounded ? y : float.MaxValue;

        if (dLeft <= dRight && dLeft <= dBottom && dLeft <= dTop)
        {
            dist = dLeft;
            nx = 1f;
            ny = 0f;
        }
        else if (dRight <= dBottom && dRight <= dTop)
        {
            dist = dRight;
            nx = -1f;
            ny = 0f;
        }
        else if (dTop <= dBottom)
        {
            dist = dTop;
            nx = 0f;
            ny = 1f;
        }
        else
        {
            dist = dBottom;
            nx = 0f;
            ny = -1f;
        }
    }

    /// <summary>盒式降采样：把一个 ds×ds 块平均成 1 个像素，同时补满 alpha。</summary>
    private static void DownsampleBox(byte[] src, int sw, int sh, byte[] dst, int dw, int dh, int ds)
    {
        for (int y = 0; y < dh; y++)
        {
            int sy0 = y * ds;
            int rowOut = y * dw * 4;
            for (int x = 0; x < dw; x++)
            {
                int sx0 = x * ds;
                int sr = 0;
                int sg = 0;
                int sb = 0;
                int n = 0;
                for (int j = 0; j < ds; j++)
                {
                    int sy = Math.Min(sy0 + j, sh - 1);
                    int row = sy * sw * 4;
                    for (int i = 0; i < ds; i++)
                    {
                        int sx = Math.Min(sx0 + i, sw - 1);
                        int o = row + sx * 4;
                        sb += src[o];
                        sg += src[o + 1];
                        sr += src[o + 2];
                        n++;
                    }
                }

                int d = rowOut + x * 4;
                dst[d] = (byte)(sb / n);
                dst[d + 1] = (byte)(sg / n);
                dst[d + 2] = (byte)(sr / n);
                dst[d + 3] = 255;
            }
        }
    }

    private static void SampleBilinear(byte[] src, int sw, int sh, float x, float y,        out float r, out float g, out float b)
    {
        x = Math.Clamp(x, 0f, sw - 1.001f);
        y = Math.Clamp(y, 0f, sh - 1.001f);

        int x0 = (int)x;
        int y0 = (int)y;
        int x1 = Math.Min(x0 + 1, sw - 1);
        int y1 = Math.Min(y0 + 1, sh - 1);
        float tx = x - x0;
        float ty = y - y0;

        int i00 = (y0 * sw + x0) * 4;
        int i10 = (y0 * sw + x1) * 4;
        int i01 = (y1 * sw + x0) * 4;
        int i11 = (y1 * sw + x1) * 4;

        r = Mix(src[i00 + 2], src[i10 + 2], src[i01 + 2], src[i11 + 2], tx, ty);
        g = Mix(src[i00 + 1], src[i10 + 1], src[i01 + 1], src[i11 + 1], tx, ty);
        b = Mix(src[i00 + 0], src[i10 + 0], src[i01 + 0], src[i11 + 0], tx, ty);
    }

    private static float Mix(byte a, byte b, byte c, byte d, float tx, float ty)
    {
        float top = a + (b - a) * tx;
        float bottom = c + (d - c) * tx;
        return top + (bottom - top) * ty;
    }

    /// <summary>滑动窗口盒式模糊，逐通道、O(像素)。scratch 复用一份线程内缓冲，一次合成要调四次，不能每次都 new。</summary>
    private static void BoxBlur(byte[] buf, int w, int h, int radius, bool horizontal)
    {
        int len = w * h * 4;
        var scratch = BlurScratch(len);
        int window = radius * 2 + 1;

        if (horizontal)
        {
            for (int y = 0; y < h; y++)
            {
                int row = y * w * 4;
                for (int c = 0; c < 3; c++)
                {
                    int sum = 0;
                    for (int i = -radius; i <= radius; i++)
                    {
                        int sx = Math.Clamp(i, 0, w - 1);
                        sum += buf[row + sx * 4 + c];
                    }

                    for (int x = 0; x < w; x++)
                    {
                        scratch[row + x * 4 + c] = (byte)(sum / window);
                        int addX = Math.Clamp(x + radius + 1, 0, w - 1);
                        int subX = Math.Clamp(x - radius, 0, w - 1);
                        sum += buf[row + addX * 4 + c] - buf[row + subX * 4 + c];
                    }
                }
            }
        }
        else
        {
            for (int x = 0; x < w; x++)
            {
                for (int c = 0; c < 3; c++)
                {
                    int sum = 0;
                    for (int i = -radius; i <= radius; i++)
                    {
                        int sy = Math.Clamp(i, 0, h - 1);
                        sum += buf[(sy * w + x) * 4 + c];
                    }

                    for (int y = 0; y < h; y++)
                    {
                        scratch[(y * w + x) * 4 + c] = (byte)(sum / window);
                        int addY = Math.Clamp(y + radius + 1, 0, h - 1);
                        int subY = Math.Clamp(y - radius, 0, h - 1);
                        sum += buf[(addY * w + x) * 4 + c] - buf[(subY * w + x) * 4 + c];
                    }
                }
            }
        }

        for (int y = 0; y < h; y++)
        {
            Buffer.BlockCopy(scratch, y * w * 4, buf, y * w * 4, w * 4);
        }
    }

    /// <summary>
    /// 面层配色：提白 tint 与轻微去色。这两步是线性的，放在缩小后的模糊缓冲上做没问题；
    /// 自适应提亮是非线性的（越暗提得越多），必须逐像素做，否则半分辨率路径
    /// 会「先提亮再插值」，比全分辨率路径亮出一大截。
    /// </summary>
    private static void TintBuffer(byte[] buf, int count, float tint)
    {
        for (int i = 0; i < count; i++)
        {
            int o = i * 4;
            float b = buf[o];
            float g = buf[o + 1];
            float r = buf[o + 2];

            float lum = (r + g + b) * (1f / 3f);
            r = r * (1f - tint) + 255f * tint;
            g = g * (1f - tint) + 255f * tint;
            b = b * (1f - tint) + 255f * tint;
            r = r * 0.92f + lum * 0.08f;
            g = g * 0.92f + lum * 0.08f;
            b = b * 0.92f + lum * 0.08f;

            buf[o] = (byte)Math.Clamp(b, 0f, 255f);
            buf[o + 1] = (byte)Math.Clamp(g, 0f, 255f);
            buf[o + 2] = (byte)Math.Clamp(r, 0f, 255f);
        }
    }

    /// <summary>自适应提亮：背后越暗，玻璃越乳白，墨字才读得出来。逐输出像素应用，两条路径才一致。</summary>
    private static void BoostLuminance(ref float r, ref float g, ref float b)
    {
        float lum = (r + g + b) * (1f / 3f);
        if (lum < MinLuminance)
        {
            float need = Math.Clamp((MinLuminance - lum) / MathF.Max(255f - lum, 1f), 0f, 1f) * 0.92f;
            r += (255f - r) * need;
            g += (255f - g) * need;
            b += (255f - b) * need;
        }
    }

    [ThreadStatic]
    private static byte[]? _blurScratch;

    /// <summary>BoxBlur 的工作缓冲，按线程复用（合成只在各自渲染线程上跑）。</summary>
    private static byte[] BlurScratch(int minLength)
    {
        if (_blurScratch is null || _blurScratch.Length < minLength)
        {
            _blurScratch = new byte[minLength];
        }

        return _blurScratch;
    }

    /// <summary>把一块 BGRA 像素搬进分层画布的位图（供后续当 GDI+ 图像绘制）。</summary>
    internal static void Upload(byte[] pixels, int width, int height, LayeredCanvas canvas)
    {
        if (canvas.Ensure(width, height))
        {
            Marshal.Copy(pixels, 0, canvas.Bits, width * height * 4);
        }
    }
}

