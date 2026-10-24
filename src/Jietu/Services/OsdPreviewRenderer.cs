namespace Jietu;

/// <summary>设置页预览位图：PNG 字节 + 像素尺寸。</summary>
internal readonly record struct OsdPreviewBitmap(byte[] Png, int Width, int Height);

/// <summary>
/// 设置页「仿真预览」的渲染入口：直接调用桌面上真正在用的 <see cref="OsdRenderer"/>，
/// 因此预览与实机显示永远一致（不再手写一套 XAML 假控件）。
/// </summary>
internal static class OsdPreviewRenderer
{
    /// <summary>按 1.0 缩放（= 1dip:1px）渲染时的画布宽度，用于反推预览该缩多少。</summary>
    internal static int NaturalWidth(bool topRight, in OsdRenderer.Metrics m)
        => OsdRenderer.Layout(1f, topRight, m).BufferWidth;

    /// <summary>
    /// 以给定缩放渲染一张 OSD 稳定态位图。scale 为设备像素相对 dip 的倍率，
    /// 传「目标像素宽 / NaturalWidth」即可得到刚好铺满预览区的清晰位图。
    /// </summary>
    internal static OsdPreviewBitmap? Render(float scale, bool topRight, in OsdRenderer.Metrics m)
    {
        if (scale <= 0.05f)
        {
            return null;
        }

        var frame = OsdRenderer.Layout(scale, topRight, m) with { Reveal = 1f };
        using var canvas = new LayeredCanvas();
        if (!canvas.Ensure(frame.BufferWidth, frame.BufferHeight) || canvas.Graphics == IntPtr.Zero)
        {
            return null;
        }

        var g = canvas.Graphics;
        OverlayGdi.Clear(g);
        // cacheChrome: false —— 不复用/不覆盖实时窗口的静态层缓存
        OsdRenderer.Draw(g, frame, m, cacheChrome: false);

        var png = OverlayGdi.EncodePng(canvas.Image);
        return png == null ? null : new OsdPreviewBitmap(png, frame.BufferWidth, frame.BufferHeight);
    }

    /// <summary>没有实时采样时用的示例值，保证预览在没有开启 OSD 时也有内容。</summary>
    internal static OsdRenderer.Metrics Demo =>
        new()
        {
            Gpu = 38f,
            Temp = 56f,
            VramGb = 1.2f,
            Cpu = 22f,
            Ghz = 4.2f,
            Ram = 48f,
            RamUsedGb = 15.4f,
            Down = 1.4f * 1024f * 1024f,
            Up = 0.3f * 1024f * 1024f,
            Fps = 0f,
            FpsMeasured = false,
            RefreshHz = 144f,
        };
}
