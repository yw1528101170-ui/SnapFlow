namespace Jietu;

internal static class FrameCompose
{
    public static void Blit(byte[] dest, int destWidth, int destHeight, byte[] src, int srcWidth, int srcHeight, int x, int y)
    {
        if (destWidth <= 0 || destHeight <= 0 || srcWidth <= 0 || srcHeight <= 0)
        {
            return;
        }

        var sourceX = 0;
        var targetX = x;
        var width = srcWidth;
        if (targetX < 0)
        {
            sourceX = -targetX;
            width -= sourceX;
            targetX = 0;
        }

        if (targetX + width > destWidth)
        {
            width = destWidth - targetX;
        }

        if (width <= 0)
        {
            return;
        }

        var minRow = Math.Max(0, -y);
        var maxRow = Math.Min(srcHeight, destHeight - y);
        if (minRow >= maxRow)
        {
            return;
        }

        if (sourceX == 0 && targetX == 0 && width == destWidth && width == srcWidth && minRow == 0 && maxRow == srcHeight)
        {
            Buffer.BlockCopy(src, 0, dest, y * destWidth * 4, srcWidth * srcHeight * 4);
            return;
        }

        var rowBytes = width * 4;
        var srcStride = srcWidth * 4;
        var destStride = destWidth * 4;
        var srcOffset = (minRow * srcWidth + sourceX) * 4;
        var destOffset = ((y + minRow) * destWidth + targetX) * 4;

        for (var row = minRow; row < maxRow; row++)
        {
            Buffer.BlockCopy(src, srcOffset, dest, destOffset, rowBytes);
            srcOffset += srcStride;
            destOffset += destStride;
        }
    }
}
