using System.Runtime.InteropServices;

namespace Jietu;

internal enum AnnotateKind
{
    Pen,
    Arrow,
    Text,
    Mosaic,
    Rectangle,
    Ellipse,
    Number,
    Emoji,
    Highlighter,
    Spotlight,
}

internal sealed class AnnotateStroke
{
    public required AnnotateKind Kind { get; init; }
    public required byte B { get; init; }
    public required byte G { get; init; }
    public required byte R { get; init; }
    public required List<(int X, int Y)> Points { get; init; }
    public string? Text { get; init; }
    public int FontPx { get; init; }
    public int WidthPx { get; init; }
}

internal static class AnnotationRenderer
{
    public static byte[] Render(byte[] source, int width, int height, IReadOnlyList<AnnotateStroke> strokes)
    {
        if (!HasInk(strokes))
        {
            return source;
        }

        var spotlights = strokes.Where(s => s.Kind == AnnotateKind.Spotlight && s.Points.Count >= 2).ToList();
        if (spotlights.Count > 0)
        {
            ApplySpotlights(source, width, height, spotlights);
        }

        foreach (var stroke in strokes)
        {
            switch (stroke.Kind)
            {
                case AnnotateKind.Pen:
                    DrawPolyline(source, width, height, stroke.Points, stroke.B, stroke.G, stroke.R, stroke.WidthPx > 0 ? 0 : 3, stroke.WidthPx);
                    break;
                case AnnotateKind.Arrow:
                    if (stroke.Points.Count >= 2)
                    {
                        DrawArrow(source, width, height, stroke.Points[0], stroke.Points[^1], stroke.B, stroke.G, stroke.R, stroke.WidthPx);
                    }
                    break;
                case AnnotateKind.Mosaic:
                    if (stroke.Text == "full" && stroke.Points.Count >= 2)
                    {
                        Pixelate(source, width, height, Bounds(stroke.Points[0], stroke.Points[^1]), stroke.WidthPx > 0 ? stroke.WidthPx : 16);
                        break;
                    }

                    if (stroke.Points.Count >= 1)
                    {
                        var radius = stroke.WidthPx > 0 ? stroke.WidthPx : 18;
                        foreach (var tile in MosaicBrush(source, width, height, stroke.Points, radius))
                        {
                            FillBlock(source, width, tile);
                        }
                    }
                    break;
                case AnnotateKind.Rectangle:
                    if (stroke.Points.Count >= 2)
                    {
                        DrawRectangle(source, width, height, stroke.Points[0], stroke.Points[^1], stroke.B, stroke.G, stroke.R, stroke.WidthPx);
                    }
                    break;
                case AnnotateKind.Ellipse:
                    if (stroke.Points.Count >= 2)
                    {
                        DrawEllipse(source, width, height, stroke.Points[0], stroke.Points[^1], stroke.B, stroke.G, stroke.R, stroke.WidthPx);
                    }
                    break;
                case AnnotateKind.Number:
                    if (stroke.Points.Count >= 1)
                    {
                        // 与遮罩预览共用同一尺寸公式（见 NumberBadgeMetrics）
                        var badge = NumberBadgeMetrics(stroke.FontPx);
                        FillCircle(source, width, height, stroke.Points[0].X, stroke.Points[0].Y, badge.Radius, stroke.B, stroke.G, stroke.R);
                    }
                    break;
                case AnnotateKind.Highlighter:
                    if (stroke.Points.Count >= 2)
                    {
                        DrawHighlighter(source, width, height, stroke.Points, stroke.B, stroke.G, stroke.R, stroke.WidthPx > 0 ? stroke.WidthPx * 4 : 20);
                    }
                    break;
                case AnnotateKind.Spotlight: break;
            }
        }

        DrawTexts(source, width, height, strokes);
        return source;
    }

    private static bool HasInk(IReadOnlyList<AnnotateStroke> strokes)
    {
        return strokes.Any(stroke =>
            ((stroke.Kind is AnnotateKind.Text or AnnotateKind.Number or AnnotateKind.Emoji) && !string.IsNullOrEmpty(stroke.Text)) ||
            (stroke.Kind == AnnotateKind.Mosaic && stroke.Points.Count >= 1) ||
            stroke.Points.Count >= 2);
    }

    private static void DrawPolyline(byte[] pixels, int width, int height, IReadOnlyList<(int X, int Y)> points, byte b, byte g, byte r, int radius, int thickness = 0)
    {
        for (var i = 1; i < points.Count; i++)
        {
            DrawLine(pixels, width, height, points[i - 1], points[i], b, g, r, radius, thickness);
        }
    }

    private static void DrawArrow(byte[] pixels, int width, int height, (int X, int Y) start, (int X, int Y) end, byte b, byte g, byte r, int thickness)
    {
        var angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
        var distance = Math.Sqrt(((end.X - start.X) * (end.X - start.X)) + ((end.Y - start.Y) * (end.Y - start.Y)));
        var length = thickness > 0 ? Math.Max(8, thickness * 5) : 18;
        var back = thickness > 0 ? Math.Min(length * 0.75, distance * 0.35) : Math.Min(12, distance * 0.35);
        var neck = (end.X - (int)Math.Round(Math.Cos(angle) * back), end.Y - (int)Math.Round(Math.Sin(angle) * back));
        DrawLine(pixels, width, height, start, neck, b, g, r, thickness > 0 ? 0 : 2, thickness);
        var spread = Math.PI / 6.5;
        var left = (end.X - (int)Math.Round(Math.Cos(angle - spread) * length), end.Y - (int)Math.Round(Math.Sin(angle - spread) * length));
        var right = (end.X - (int)Math.Round(Math.Cos(angle + spread) * length), end.Y - (int)Math.Round(Math.Sin(angle + spread) * length));
        FillTriangle(pixels, width, height, end, left, right, b, g, r);
    }

    private static void FillTriangle(byte[] pixels, int width, int height, (int X, int Y) a, (int X, int Y) b, (int X, int Y) c, byte blue, byte green, byte red)
    {
        var minY = Math.Clamp(Math.Min(a.Y, Math.Min(b.Y, c.Y)), 0, height - 1);
        var maxY = Math.Clamp(Math.Max(a.Y, Math.Max(b.Y, c.Y)), 0, height - 1);
        Span<int> hits = stackalloc int[6];
        var pixel32 = MemoryMarshal.Cast<byte, uint>(pixels.AsSpan());
        uint color = (uint)(blue | (green << 8) | (red << 16) | (255 << 24));
        for (var y = minY; y <= maxY; y++)
        {
            var count = 0;
            count = EdgeHit(a, b, y, hits, count);
            count = EdgeHit(b, c, y, hits, count);
            count = EdgeHit(c, a, y, hits, count);
            if (count < 2)
            {
                continue;
            }

            var x0 = hits[0];
            var x1 = hits[0];
            for (var i = 1; i < count; i++)
            {
                if (hits[i] < x0) x0 = hits[i];
                if (hits[i] > x1) x1 = hits[i];
            }

            x0 = Math.Clamp(x0, 0, width - 1);
            x1 = Math.Clamp(x1, 0, width - 1);
            if (x1 >= x0)
            {
                pixel32.Slice(y * width + x0, x1 - x0 + 1).Fill(color);
            }
        }
    }

    private static int EdgeHit((int X, int Y) a, (int X, int Y) b, int y, Span<int> hits, int count)
    {
        if (count >= hits.Length || a.Y == b.Y || (y < a.Y && y < b.Y) || (y > a.Y && y > b.Y))
        {
            return count;
        }

        var t = (double)(y - a.Y) / (b.Y - a.Y);
        hits[count] = a.X + (int)Math.Round((b.X - a.X) * t);
        return count + 1;
    }

    private static void DrawLine(byte[] pixels, int width, int height, (int X, int Y) start, (int X, int Y) end, byte b, byte g, byte r, int radius, int thickness = 0)
    {
        var dx = Math.Abs(end.X - start.X);
        var dy = Math.Abs(end.Y - start.Y);
        var sx = start.X < end.X ? 1 : -1;
        var sy = start.Y < end.Y ? 1 : -1;
        var err = dx - dy;
        var x = start.X;
        var y = start.Y;
        while (true)
        {
            if (thickness > 0)
            {
                Stamp(pixels, width, height, x, y, b, g, r, thickness);
            }
            else
            {
                FillDot(pixels, width, height, x, y, b, g, r, radius);
            }
            if (x == end.X && y == end.Y)
            {
                break;
            }

            var e2 = 2 * err;
            if (e2 > -dy)
            {
                err -= dy;
                x += sx;
            }

            if (e2 < dx)
            {
                err += dx;
                y += sy;
            }
        }
    }

    private static void FillDot(byte[] pixels, int width, int height, int cx, int cy, byte b, byte g, byte r, int radius)
    {
        var top = Math.Max(0, cy - radius);
        var bottom = Math.Min(height - 1, cy + radius);
        var limit = radius * radius;
        var pixel32 = MemoryMarshal.Cast<byte, uint>(pixels.AsSpan());
        uint color = (uint)(b | (g << 8) | (r << 16) | (255 << 24));
        for (var y = top; y <= bottom; y++)
        {
            var dy = y - cy;
            var dy2 = dy * dy;
            if (dy2 > limit)
            {
                continue;
            }

            var dxMax = (int)Math.Sqrt(limit - dy2);
            var x0 = Math.Clamp(cx - dxMax, 0, width - 1);
            var x1 = Math.Clamp(cx + dxMax, 0, width - 1);
            if (x1 >= x0)
            {
                pixel32.Slice(y * width + x0, x1 - x0 + 1).Fill(color);
            }
        }
    }

    private static void Stamp(byte[] pixels, int width, int height, int cx, int cy, byte b, byte g, byte r, int thickness)
    {
        if (thickness <= 1)
        {
            if ((uint)cx >= (uint)width || (uint)cy >= (uint)height)
            {
                return;
            }

            var one = (cy * width + cx) * 4;
            pixels[one] = b;
            pixels[one + 1] = g;
            pixels[one + 2] = r;
            pixels[one + 3] = 255;
            return;
        }

        var half = thickness / 2.0;
        var limit = half * half;
        var minX = Math.Max(0, (int)Math.Floor(cx - half));
        var maxX = Math.Min(width - 1, (int)Math.Ceiling(cx + half) - 1);
        var minY = Math.Max(0, (int)Math.Floor(cy - half));
        var maxY = Math.Min(height - 1, (int)Math.Ceiling(cy + half) - 1);
        for (var y = minY; y <= maxY; y++)
        {
            var dy = y - cy;
            for (var x = minX; x <= maxX; x++)
            {
                var dx = x - cx;
                if ((dx * dx) + (dy * dy) > limit)
                {
                    continue;
                }

                var i = (y * width + x) * 4;
                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
                pixels[i + 3] = 255;
            }
        }
    }

    public readonly record struct MosaicBlock(int X, int Y, int Width, int Height, byte B, byte G, byte R);

    public static List<MosaicBlock> MosaicBlocks(byte[] pixels, int width, int height, (int X, int Y) start, (int X, int Y) end, int block = 16, int sample = 1)
    {
        var blocks = new List<MosaicBlock>();
        var (x0, y0, w, h) = Bounds(start, end);
        x0 = Math.Clamp(x0, 0, width);
        y0 = Math.Clamp(y0, 0, height);
        var x1 = Math.Clamp(x0 + w, 0, width);
        var y1 = Math.Clamp(y0 + h, 0, height);
        for (var y = y0; y < y1; y += block)
        {
            for (var x = x0; x < x1; x += block)
            {
                long sb = 0, sg = 0, sr = 0, count = 0;
                var yb = Math.Min(y + block, y1);
                var xb = Math.Min(x + block, x1);
                var step = Math.Max(1, sample);
                for (var py = y; py < yb; py += step)
                {
                    for (var px = x; px < xb; px += step)
                    {
                        var i = (py * width + px) * 4;
                        sb += pixels[i];
                        sg += pixels[i + 1];
                        sr += pixels[i + 2];
                        count++;
                    }
                }

                if (count == 0)
                {
                    continue;
                }

                blocks.Add(new MosaicBlock(x, y, xb - x, yb - y, (byte)(sb / count), (byte)(sg / count), (byte)(sr / count)));
            }
        }

        return blocks;
    }

    /// <summary>
    /// 单笔马赛克最多生成的瓦片数。上限本身是性能兜底（一次涂抹不能把整屏都重采样），
    /// 但超限必须通过 truncated 告知调用方，不能静默砍掉尾部——否则就是"预览遮住了、导出没遮住"的隐私漏洞。
    /// 4000 块 × 12px 块最坏约 4000×144 ≈ 58 万次像素采样，单帧 1~2ms，60fps 拖拽仍流畅；
    /// 瓦片包围盒位图最坏 4000×144×4B ≈ 2.3MB（LOH），比旧上限多不到 1MB，可接受。
    /// </summary>
    private const int MosaicBrushTileLimit = 4000;

    public static List<MosaicBlock> MosaicBrush(byte[] pixels, int width, int height, IReadOnlyList<(int X, int Y)> points, int radius, int block = 12)
    {
        return MosaicBrush(pixels, width, height, points, radius, block, out _);
    }

    /// <summary>
    /// 生成马赛克笔刷瓦片。<paramref name="truncated"/> 为 true 表示瓦片数已达
    /// <see cref="MosaicBrushTileLimit"/>，笔迹尾部被丢弃；预览层应据此提示用户分笔涂抹，
    /// 预览与导出共用这一上限，保证两边覆盖范围一致。
    /// </summary>
    public static List<MosaicBlock> MosaicBrush(byte[] pixels, int width, int height, IReadOnlyList<(int X, int Y)> points, int radius, int block, out bool truncated)
    {
        // 局部函数不能捕获 out 参数（CS1628），所以先记在局部变量里，返回前再写回。
        var isTruncated = false;
        truncated = false;
        var blocks = new List<MosaicBlock>();
        if (points.Count == 0 || width <= 0 || height <= 0)
        {
            return blocks;
        }

        radius = Math.Max(8, radius);
        block = Math.Max(4, block);
        var seen = new HashSet<long>();
        void AddBlock(int bx, int by)
        {
            if (isTruncated)
            {
                // 已达上限：后面的瓦片全部丢弃，但仍要把 truncated 标记交给调用方提示。
                return;
            }

            if (bx >= width || by >= height || bx + block <= 0 || by + block <= 0)
            {
                return;
            }

            var key = ((long)bx << 32) | (uint)by;
            if (!seen.Add(key))
            {
                return;
            }

            if (blocks.Count >= MosaicBrushTileLimit)
            {
                isTruncated = true;
                return;
            }

            var x0 = Math.Clamp(bx, 0, width);
            var y0 = Math.Clamp(by, 0, height);
            var x1 = Math.Clamp(bx + block, 0, width);
            var y1 = Math.Clamp(by + block, 0, height);
            long sb = 0, sg = 0, sr = 0, count = 0;
            for (var py = y0; py < y1; py++)
            {
                for (var px = x0; px < x1; px++)
                {
                    var i = (py * width + px) * 4;
                    sb += pixels[i];
                    sg += pixels[i + 1];
                    sr += pixels[i + 2];
                    count++;
                }
            }

            if (count == 0)
            {
                return;
            }

            blocks.Add(new MosaicBlock(x0, y0, x1 - x0, y1 - y0, (byte)(sb / count), (byte)(sg / count), (byte)(sr / count)));
        }

        void Stamp(int cx, int cy)
        {
            var left = cx - radius;
            var top = cy - radius;
            var bx0 = (int)Math.Floor(left / (double)block) * block;
            var by0 = (int)Math.Floor(top / (double)block) * block;
            for (var by = by0; by <= cy + radius; by += block)
            {
                for (var bx = bx0; bx <= cx + radius; bx += block)
                {
                    AddBlock(bx, by);
                }
            }
        }

        Stamp(points[0].X, points[0].Y);
        for (var i = 1; i < points.Count; i++)
        {
            var a = points[i - 1];
            var b = points[i];
            var dist = Math.Abs(b.X - a.X) + Math.Abs(b.Y - a.Y);
            var steps = Math.Max(1, dist / Math.Max(4, radius / 2));
            for (var step = 1; step <= steps; step++)
            {
                var t = step / (double)steps;
                Stamp(a.X + (int)Math.Round((b.X - a.X) * t), a.Y + (int)Math.Round((b.Y - a.Y) * t));
            }
        }

        truncated = isTruncated;
        return blocks;
    }

    /// <summary>
    /// 序号（Number）徽标的统一尺寸（像素）：导出圆半径与字号、遮罩预览的圆直径与字号都从这里取，
    /// 避免两边各写一套公式后漂移。半径 = max(12, FontPx×0.7)、字号 = FontPx（缺省 28），
    /// 因此 100% DPI 下预览（dip）与导出（px）的圆直径、字号完全一致。
    /// </summary>
    public static (int Radius, int FontSize) NumberBadgeMetrics(int fontPx)
    {
        var fontSize = fontPx > 0 ? fontPx : 28;
        return (Math.Max(12, (int)Math.Round(fontSize * 0.7)), fontSize);
    }

    private static void FillBlock(byte[] pixels, int width, MosaicBlock tile)
    {
        var pixel32 = MemoryMarshal.Cast<byte, uint>(pixels.AsSpan());
        uint color = (uint)(tile.B | (tile.G << 8) | (tile.R << 16) | (255 << 24));
        var yEnd = tile.Y + tile.Height;
        for (var py = tile.Y; py < yEnd; py++)
        {
            pixel32.Slice(py * width + tile.X, tile.Width).Fill(color);
        }
    }

    private static void Pixelate(byte[] pixels, int width, int height, (int X, int Y, int W, int H) rect, int block)
    {
        var x0 = Math.Clamp(rect.X, 0, width);
        var y0 = Math.Clamp(rect.Y, 0, height);
        var x1 = Math.Clamp(rect.X + rect.W, 0, width);
        var y1 = Math.Clamp(rect.Y + rect.H, 0, height);
        var pixel32 = MemoryMarshal.Cast<byte, uint>(pixels.AsSpan());
        for (var y = y0; y < y1; y += block)
        {
            for (var x = x0; x < x1; x += block)
            {
                long sb = 0, sg = 0, sr = 0, count = 0;
                var yb = Math.Min(y + block, y1);
                var xb = Math.Min(x + block, x1);
                for (var py = y; py < yb; py++)
                {
                    var row = py * width;
                    for (var px = x; px < xb; px++)
                    {
                        var i = (row + px) * 4;
                        sb += pixels[i];
                        sg += pixels[i + 1];
                        sr += pixels[i + 2];
                        count++;
                    }
                }

                if (count == 0)
                {
                    continue;
                }

                var ab = (byte)(sb / count);
                var ag = (byte)(sg / count);
                var ar = (byte)(sr / count);
                uint color = (uint)(ab | (ag << 8) | (ar << 16) | (255 << 24));
                var bw = xb - x;
                for (var py = y; py < yb; py++)
                {
                    pixel32.Slice(py * width + x, bw).Fill(color);
                }
            }
        }
    }

    private static void DrawRectangle(byte[] pixels, int width, int height, (int X, int Y) a, (int X, int Y) b, byte blue, byte green, byte red, int thickness)
    {
        var (x, y, w, h) = Bounds(a, b);
        var radius = thickness > 0 ? 0 : 2;
        DrawLine(pixels, width, height, (x, y), (x + w, y), blue, green, red, radius, thickness);
        DrawLine(pixels, width, height, (x + w, y), (x + w, y + h), blue, green, red, radius, thickness);
        DrawLine(pixels, width, height, (x + w, y + h), (x, y + h), blue, green, red, radius, thickness);
        DrawLine(pixels, width, height, (x, y + h), (x, y), blue, green, red, radius, thickness);
    }

    private static void DrawEllipse(byte[] pixels, int width, int height, (int X, int Y) a, (int X, int Y) b, byte blue, byte green, byte red, int thickness)
    {
        var (x, y, w, h) = Bounds(a, b);
        var rx = Math.Max(1, w / 2.0);
        var ry = Math.Max(1, h / 2.0);
        var cx = x + rx;
        var cy = y + ry;
        var radius = thickness > 0 ? 0 : 2;
        var previous = ((int)cx, (int)(cy - ry));
        for (var step = 1; step <= 72; step++)
        {
            var angle = step / 72.0 * Math.PI * 2;
            var next = ((int)Math.Round(cx + Math.Cos(angle) * rx), (int)Math.Round(cy + Math.Sin(angle) * ry));
            DrawLine(pixels, width, height, previous, next, blue, green, red, radius, thickness);
            previous = next;
        }
    }

    private static (int X, int Y, int W, int H) Bounds((int X, int Y) a, (int X, int Y) b)
    {
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        return (x, y, Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
    }

    private static void FillCircle(byte[] pixels, int width, int height, int cx, int cy, int radius, byte b, byte g, byte r)
    {
        var top = Math.Max(0, cy - radius);
        var bottom = Math.Min(height - 1, cy + radius);
        var limit = radius * radius;
        var pixel32 = MemoryMarshal.Cast<byte, uint>(pixels.AsSpan());
        uint color = (uint)(b | (g << 8) | (r << 16) | (255 << 24));
        for (var y = top; y <= bottom; y++)
        {
            var dy = y - cy;
            var dy2 = dy * dy;
            if (dy2 > limit)
            {
                continue;
            }

            var dxMax = (int)Math.Sqrt(limit - dy2);
            var x0 = Math.Clamp(cx - dxMax, 0, width - 1);
            var x1 = Math.Clamp(cx + dxMax, 0, width - 1);
            if (x1 >= x0)
            {
                pixel32.Slice(y * width + x0, x1 - x0 + 1).Fill(color);
            }
        }
    }

    private static void DrawTexts(byte[] pixels, int width, int height, IReadOnlyList<AnnotateStroke> strokes)
    {
        var texts = strokes.Where(stroke => (stroke.Kind is AnnotateKind.Text or AnnotateKind.Number or AnnotateKind.Emoji) && !string.IsNullOrEmpty(stroke.Text)).ToList();
        if (texts.Count == 0)
        {
            return;
        }

        var info = new NativeMethods.BitmapInfo
        {
            Header = new NativeMethods.BitmapInfoHeader
            {
                BiSize = 40,
                BiWidth = width,
                BiHeight = -height,
                BiPlanes = 1,
                BiBitCount = 32,
                BiCompression = NativeMethods.BiRgb,
            },
        };
        var screen = NativeMethods.GetDC(IntPtr.Zero);
        var dib = NativeMethods.CreateDIBSection(screen, ref info, 0, out var bits, IntPtr.Zero, 0);
        var dc = NativeMethods.CreateCompatibleDC(screen);
        NativeMethods.ReleaseDC(IntPtr.Zero, screen);
        if (dib == IntPtr.Zero || dc == IntPtr.Zero || bits == IntPtr.Zero)
        {
            if (dib != IntPtr.Zero) NativeMethods.DeleteObject(dib);
            if (dc != IntPtr.Zero) NativeMethods.DeleteDC(dc);
            return;
        }

        Marshal.Copy(pixels, 0, bits, pixels.Length);
        var previous = NativeMethods.SelectObject(dc, dib);
        try
        {
            SetBkMode(dc, 1);
            foreach (var stroke in texts)
            {
                var isEmoji = stroke.Kind == AnnotateKind.Emoji;
                // 序号字号与遮罩预览共用 NumberBadgeMetrics；其它文字沿用 FontPx（缺省 28）。
                var textPx = stroke.Kind == AnnotateKind.Number
                    ? NumberBadgeMetrics(stroke.FontPx).FontSize
                    : stroke.FontPx > 0 ? stroke.FontPx : 28;
                var fontPx = isEmoji ? -32 : -textPx;
                var fontName = isEmoji ? "Segoe UI Emoji" : "Microsoft YaHei";
                var font = CreateFont(fontPx, 0, 0, 0, stroke.Kind == AnnotateKind.Number ? 700 : 400, 0, 0, 0, 1, 0, 0, 5, 0, fontName);
                var oldFont = NativeMethods.SelectObject(dc, font);
                try
                {
                    var text = stroke.Text ?? "";
                    var x = stroke.Points[0].X;
                    var y = stroke.Points[0].Y;
                    var color = (uint)(stroke.R | (stroke.G << 8) | (stroke.B << 16));
                    if (stroke.Kind == AnnotateKind.Number && GetTextExtentPoint32(dc, text, text.Length, out var extent))
                    {
                        x -= extent.Cx / 2;
                        y -= extent.Cy / 2;
                        var light = (stroke.R * 3) + (stroke.G * 6) + stroke.B > 1600;
                        color = light ? 0x00111111u : 0x00FFFFFFu;
                    }

                    SetTextColor(dc, color);
                    TextOut(dc, x, y, text, text.Length);
                }
                finally
                {
                    NativeMethods.SelectObject(dc, oldFont);
                    NativeMethods.DeleteObject(font);
                }
            }

            Marshal.Copy(bits, pixels, 0, pixels.Length);
        }
        finally
        {
            NativeMethods.SelectObject(dc, previous);
            NativeMethods.DeleteObject(dib);
            NativeMethods.DeleteDC(dc);
        }
    }

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(IntPtr hdc, uint color);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool TextOut(IntPtr hdc, int x, int y, string text, int count);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetTextExtentPoint32(IntPtr hdc, string text, int count, out TextExtent size);

    [StructLayout(LayoutKind.Sequential)]
    private struct TextExtent
    {
        public int Cx;
        public int Cy;
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);

    private static void ApplySpotlights(byte[] pixels, int width, int height, IReadOnlyList<AnnotateStroke> spotlights)
    {
        var boxes = new List<(int Left, int Top, int Right, int Bottom)>(spotlights.Count);
        foreach (var s in spotlights)
        {
            var p0 = s.Points[0];
            var p1 = s.Points[^1];
            boxes.Add((
                Math.Clamp(Math.Min(p0.X, p1.X), 0, width - 1),
                Math.Clamp(Math.Min(p0.Y, p1.Y), 0, height - 1),
                Math.Clamp(Math.Max(p0.X, p1.X), 0, width - 1),
                Math.Clamp(Math.Max(p0.Y, p1.Y), 0, height - 1)
            ));
        }

        for (var y = 0; y < height; y++)
        {
            var row = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var inside = false;
                for (var i = 0; i < boxes.Count; i++)
                {
                    var b = boxes[i];
                    if (x >= b.Left && x <= b.Right && y >= b.Top && y <= b.Bottom)
                    {
                        inside = true;
                        break;
                    }
                }

                if (!inside)
                {
                    var idx = row + (x * 4);
                    pixels[idx] = (byte)((pixels[idx] * 40) / 100);
                    pixels[idx + 1] = (byte)((pixels[idx + 1] * 40) / 100);
                    pixels[idx + 2] = (byte)((pixels[idx + 2] * 40) / 100);
                }
            }
        }

        foreach (var b in boxes)
        {
            DrawRectangle(pixels, width, height, (b.Left, b.Top), (b.Right, b.Bottom), 255, 255, 255, 2);
        }
    }

    private static void DrawHighlighter(byte[] pixels, int width, int height, IReadOnlyList<(int X, int Y)> points, byte b, byte g, byte r, int diameter)
    {
        var radius = Math.Max(2, diameter / 2);
        var visited = new byte[width * height];
        for (var i = 1; i < points.Count; i++)
        {
            DrawHighlighterLine(pixels, width, height, points[i - 1], points[i], b, g, r, radius, visited);
        }
    }

    private static void DrawHighlighterLine(byte[] pixels, int width, int height, (int X, int Y) start, (int X, int Y) end, byte b, byte g, byte r, int radius, byte[] visited)
    {
        var dx = Math.Abs(end.X - start.X);
        var dy = Math.Abs(end.Y - start.Y);
        var sx = start.X < end.X ? 1 : -1;
        var sy = start.Y < end.Y ? 1 : -1;
        var err = dx - dy;
        var x = start.X;
        var y = start.Y;
        while (true)
        {
            StampHighlighter(pixels, width, height, x, y, b, g, r, radius, visited);
            if (x == end.X && y == end.Y)
            {
                break;
            }

            var e2 = 2 * err;
            if (e2 > -dy)
            {
                err -= dy;
                x += sx;
            }
            if (e2 < dx)
            {
                err += dx;
                y += sy;
            }
        }
    }

    private static void StampHighlighter(byte[] pixels, int width, int height, int cx, int cy, byte b, byte g, byte r, int radius, byte[] visited)
    {
        var top = Math.Max(0, cy - radius);
        var bottom = Math.Min(height - 1, cy + radius);
        var r2 = radius * radius;
        const int alpha = 110;
        const int invAlpha = 255 - alpha;

        for (var y = top; y <= bottom; y++)
        {
            var dy = y - cy;
            var dy2 = dy * dy;
            if (dy2 > r2)
            {
                continue;
            }

            var dxMax = (int)Math.Sqrt(r2 - dy2);
            var x0 = Math.Clamp(cx - dxMax, 0, width - 1);
            var x1 = Math.Clamp(cx + dxMax, 0, width - 1);
            var rowStart = y * width;
            var pixelOffset = rowStart * 4;

            for (var x = x0; x <= x1; x++)
            {
                if (visited[rowStart + x] == 0)
                {
                    visited[rowStart + x] = 1;
                    var idx = pixelOffset + (x * 4);
                    pixels[idx] = (byte)((pixels[idx] * invAlpha + b * alpha) / 255);
                    pixels[idx + 1] = (byte)((pixels[idx + 1] * invAlpha + g * alpha) / 255);
                    pixels[idx + 2] = (byte)((pixels[idx + 2] * invAlpha + r * alpha) / 255);
                }
            }
        }
    }
}
