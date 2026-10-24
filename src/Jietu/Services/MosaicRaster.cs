using System.Runtime.InteropServices;

namespace Jietu;

/// <summary>
/// 马赛克瓦片的光栅化：把 <see cref="AnnotationRenderer.MosaicBlock"/> 列表写进一块 BGRA 缓冲。
/// 与 XAML 无关，便于在自测里做像素级断言；遮罩层只负责把缓冲塞进 WriteableBitmap。
/// </summary>
internal static class MosaicRaster
{
    /// <summary>所有瓦片的并集包围盒（像素）。没有瓦片时返回全 0。</summary>
    public static (int X, int Y, int Width, int Height) Bounds(IReadOnlyList<AnnotationRenderer.MosaicBlock> tiles)
    {
        if (tiles.Count == 0)
        {
            return default;
        }

        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = int.MinValue;
        var maxY = int.MinValue;
        foreach (var tile in tiles)
        {
            if (tile.Width <= 0 || tile.Height <= 0)
            {
                continue;
            }

            minX = Math.Min(minX, tile.X);
            minY = Math.Min(minY, tile.Y);
            maxX = Math.Max(maxX, tile.X + tile.Width);
            maxY = Math.Max(maxY, tile.Y + tile.Height);
        }

        return minX >= maxX || minY >= maxY ? default : (minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>
    /// 把瓦片画进 <paramref name="pixels"/>（BGRA，尺寸为 width×height，原点对应 (originX, originY)）。
    /// <paramref name="drawn"/> 非空时做增量：已画过的瓦片（按左上角去重）直接跳过，返回本次真正上色的块数。
    /// 越界部分自动裁掉，不会写到缓冲之外。
    /// </summary>
    public static int Paint(
        byte[] pixels,
        int width,
        int height,
        int originX,
        int originY,
        IReadOnlyList<AnnotationRenderer.MosaicBlock> tiles,
        HashSet<long>? drawn = null)
    {
        if (pixels.Length < width * height * 4 || width <= 0 || height <= 0)
        {
            return 0;
        }

        var buffer = MemoryMarshal.Cast<byte, uint>(pixels.AsSpan(0, width * height * 4));
        var painted = 0;
        foreach (var tile in tiles)
        {
            if (tile.Width <= 0 || tile.Height <= 0)
            {
                continue;
            }

            if (drawn != null && !drawn.Add(Key(tile.X, tile.Y)))
            {
                continue;
            }

            var left = Math.Max(tile.X, originX);
            var top = Math.Max(tile.Y, originY);
            var right = Math.Min(tile.X + tile.Width, originX + width);
            var bottom = Math.Min(tile.Y + tile.Height, originY + height);
            if (left >= right || top >= bottom)
            {
                continue;
            }

            var color = (uint)(tile.B | (tile.G << 8) | (tile.R << 16) | (255u << 24));
            for (var y = top; y < bottom; y++)
            {
                var row = ((y - originY) * width) + (left - originX);
                buffer.Slice(row, right - left).Fill(color);
            }

            painted++;
        }

        return painted;
    }

    /// <summary>瓦片唯一键：同一格左上角只画一次。</summary>
    public static long Key(int x, int y) => ((long)x << 32) | (uint)y;
}
