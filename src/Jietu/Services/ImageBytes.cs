using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Jietu;

public readonly record struct PixelRect(int X, int Y, int Width, int Height);

internal sealed class PickedShot
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Pixels { get; init; }
    public bool Pin { get; init; }
    public bool ExtractText { get; init; }
    public bool ExtractCode { get; init; }
    public bool LongCapture { get; init; }
    public bool Translate { get; init; }
    public bool Share { get; init; }
    public bool AiVision { get; init; }
    public int ScreenX { get; init; }
    public int ScreenY { get; init; }
}

internal static class ImageBytes
{
    public static byte[] ReadBgra(SoftwareBitmap source)
    {
        var converted = EnsureBgra(source);
        try
        {
            return ReadBgraCore(converted);
        }
        finally
        {
            if (!ReferenceEquals(converted, source))
            {
                converted.Dispose();
            }
        }
    }

    private static byte[] ReadBgraCore(SoftwareBitmap converted)
    {
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var pixels = new byte[width * height * 4];
        using var buffer = converted.LockBuffer(BitmapBufferAccessMode.Read);
        var plane = buffer.GetPlaneDescription(0);
        using var reference = buffer.CreateReference();
        var access = reference.As<IMemoryBufferByteAccess>();
        access.GetBuffer(out var pointer, out _);
        if (plane.Stride == width * 4)
        {
            Marshal.Copy(pointer, pixels, 0, pixels.Length);
        }
        else
        {
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(pointer + y * plane.Stride, pixels, y * width * 4, width * 4);
            }
        }

        ForceOpaque(pixels);
        return pixels;
    }

    public static SoftwareBitmap FromBgra(byte[] pixels, int width, int height)
    {
        ForceOpaque(pixels);
        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
        using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Write);
        var plane = buffer.GetPlaneDescription(0);
        using var reference = buffer.CreateReference();
        var access = reference.As<IMemoryBufferByteAccess>();
        access.GetBuffer(out var pointer, out _);
        if (plane.Stride == width * 4)
        {
            Marshal.Copy(pixels, 0, pointer, pixels.Length);
        }
        else
        {
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(pixels, y * width * 4, pointer + y * plane.Stride, width * 4);
            }
        }

        return bitmap;
    }

    public static async Task<byte[]> ToPngAsync(byte[] pixels, int width, int height)
    {
        using var bitmap = FromBgra(pixels, width, height);
        return await EncodePngAsync(bitmap);
    }

    public static async Task SavePreviewAsync(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var inStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        using var input = inStream.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(input);
        // 宽高同时封顶（更紧的等比上限）：长图/竖图按高度 720 限制，避免 1440×20000 的预览仍占数 MB。
        const uint MaxWidth = 360;
        const uint MaxHeight = 720;
        var sourceWidth = Math.Max(1u, decoder.PixelWidth);
        var sourceHeight = Math.Max(1u, decoder.PixelHeight);
        var factor = Math.Min(MaxWidth / (double)sourceWidth, MaxHeight / (double)sourceHeight);
        if (factor > 1d)
        {
            factor = 1d; // 不放大
        }

        var targetWidth = Math.Max(1u, (uint)Math.Floor(sourceWidth * factor));
        var targetHeight = Math.Max(1u, (uint)Math.Floor(sourceHeight * factor));
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform
            {
                ScaledWidth = targetWidth,
                ScaledHeight = targetHeight,
                InterpolationMode = BitmapInterpolationMode.Linear,
            },
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage);
        await using var outStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        using var output = outStream.AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, targetWidth, targetHeight, 96, 96, pixels.DetachPixelData());
        await encoder.FlushAsync();
    }

    public static async Task<byte[]> ToPngAsync(SoftwareBitmap source)
    {
        var converted = EnsureBgra(source);
        try
        {
            return await EncodePngAsync(converted);
        }
        finally
        {
            if (!ReferenceEquals(converted, source))
            {
                converted.Dispose();
            }
        }
    }

    internal static bool IsMostlyBlank(SoftwareBitmap source)
    {
        if (source.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || source.PixelWidth < 2 || source.PixelHeight < 2)
        {
            return false;
        }

        using var buffer = source.LockBuffer(BitmapBufferAccessMode.Read);
        var plane = buffer.GetPlaneDescription(0);
        using var reference = buffer.CreateReference();
        var access = reference.As<IMemoryBufferByteAccess>();
        access.GetBuffer(out var pointer, out _);
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        int[] xs = [width / 8, width / 2, width * 7 / 8];
        int[] ys = [height / 8, height / 2, height * 7 / 8];
        foreach (var y in ys)
        {
            foreach (var x in xs)
            {
                var pixel = pointer + y * plane.Stride + x * 4;
                if (Marshal.ReadByte(pixel) != 0 || Marshal.ReadByte(pixel, 1) != 0 || Marshal.ReadByte(pixel, 2) != 0)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static async Task<byte[]> EncodePngAsync(SoftwareBitmap bitmap)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        return await ReadStreamAsync(stream);
    }

    /// <summary>
    /// 直接从 PNG 流解码并应用硬件缩放至目标尺寸，避免先解码为完整图再编码 BMP 的往返开销。
    /// </summary>
    public static async Task<(SoftwareBitmap Bitmap, double Scale)> DecodeForOcrAsync(
        byte[] png,
        uint max,
        double targetSide = 0,
        double maxUpscale = 1.0)
    {
        using var stream = await WriteStreamAsync(png);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var origW = (int)decoder.PixelWidth;
        var origH = (int)decoder.PixelHeight;
        var longSide = Math.Max(origW, origH);

        var transform = new BitmapTransform();
        double scale = 1.0;
        double factor = 1.0;
        if (max > 0 && ((uint)origW > max || (uint)origH > max))
        {
            factor = Math.Min(max / (double)origW, max / (double)origH);
        }
        else if (targetSide > 0 && longSide > 0 && longSide < targetSide)
        {
            // 小图（窗口截图常见 400×600）里的 12px 文字对系统 OCR 来说太小，
            // 先放大到目标长边再做识别，行切分和字形判别都会更稳。
            factor = Math.Min(targetSide / longSide, Math.Max(1.0, maxUpscale));
            if (factor < 1.15)
            {
                factor = 1.0;
            }
        }

        if (factor != 1.0)
        {
            var width = Math.Max(1, (uint)Math.Floor(origW * factor));
            var height = Math.Max(1, (uint)Math.Floor(origH * factor));
            transform.ScaledWidth = width;
            transform.ScaledHeight = height;
            transform.InterpolationMode = factor > 1.0
                ? BitmapInterpolationMode.Fant
                : BitmapInterpolationMode.Linear;
            scale = (double)origW / width;
        }

        var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage);

        return (bitmap, scale);
    }

    /// <summary>
    /// 发给视觉模型前的等比压缩：最长边超过 <paramref name="maxEdge"/> 才缩，小图原样返回。
    /// 视觉 token 按分辨率计费且部分网关拒收超大请求体；2048 对截图理解足够，小字识别交由用户追问原图。
    /// </summary>
    public static async Task<byte[]> ShrinkPngForVisionAsync(byte[] png, uint maxEdge = 2048)
    {
        try
        {
            using var stream = await WriteStreamAsync(png);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var sourceWidth = Math.Max(1u, decoder.PixelWidth);
            var sourceHeight = Math.Max(1u, decoder.PixelHeight);
            if (sourceWidth <= maxEdge && sourceHeight <= maxEdge)
            {
                return png;
            }

            var factor = Math.Min(maxEdge / (double)sourceWidth, maxEdge / (double)sourceHeight);
            var targetWidth = Math.Max(1u, (uint)Math.Floor(sourceWidth * factor));
            var targetHeight = Math.Max(1u, (uint)Math.Floor(sourceHeight * factor));
            var pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                new BitmapTransform
                {
                    ScaledWidth = targetWidth,
                    ScaledHeight = targetHeight,
                    InterpolationMode = BitmapInterpolationMode.Linear,
                },
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);

            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, targetWidth, targetHeight, 96, 96, pixels.DetachPixelData());
            await encoder.FlushAsync();
            return await ReadStreamAsync(output);
        }
        catch (Exception ex)
        {
            // 压缩失败不应阻断请求：退回原图交由服务端处理。
            Log.Error(ex);
            return png;
        }
    }

    public static async Task<SoftwareBitmap> ScaleDownAsync(SoftwareBitmap source, uint max)
    {
        if (max == 0 || ((uint)source.PixelWidth <= max && (uint)source.PixelHeight <= max))
        {
            return source;
        }

        var scale = Math.Min(max / (double)source.PixelWidth, max / (double)source.PixelHeight);
        var width = Math.Max(1, (int)Math.Floor(source.PixelWidth * scale));
        var height = Math.Max(1, (int)Math.Floor(source.PixelHeight * scale));

        var converted = EnsureBgra(source);
        var owned = !ReferenceEquals(converted, source);
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, stream);
            encoder.SetSoftwareBitmap(converted);
            await encoder.FlushAsync();

            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var scaled = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                new BitmapTransform
                {
                    ScaledWidth = (uint)width,
                    ScaledHeight = (uint)height,
                    InterpolationMode = BitmapInterpolationMode.Linear,
                },
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);
            return scaled;
        }
        finally
        {
            if (owned)
            {
                converted.Dispose();
            }
        }
    }

    public static async Task<SoftwareBitmap> FromPngAsync(byte[] png)
    {
        using var stream = await WriteStreamAsync(png);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }

    public static async Task<SoftwareBitmapSource> ToBitmapSourceAsync(SoftwareBitmap source)
    {
        var converted = EnsureBgra(source);
        try
        {
            var image = new SoftwareBitmapSource();
            await image.SetBitmapAsync(converted);
            return image;
        }
        finally
        {
            if (!ReferenceEquals(converted, source))
            {
                converted.Dispose();
            }
        }
    }

    private static SoftwareBitmap EnsureBgra(SoftwareBitmap source)
    {
        if (source.BitmapPixelFormat == BitmapPixelFormat.Bgra8 && source.BitmapAlphaMode == BitmapAlphaMode.Premultiplied)
        {
            return source;
        }

        return SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }

    public static async Task<BitmapImage> PngToBitmapAsync(byte[] png, int decodeWidth = 0)
    {
        var image = new BitmapImage();
        if (decodeWidth > 0)
        {
            image.DecodePixelWidth = decodeWidth;
        }

        using var stream = await WriteStreamAsync(png);
        await image.SetSourceAsync(stream);
        return image;
    }

    private static async Task<IRandomAccessStream> WriteStreamAsync(byte[] bytes)
    {
        var stream = new InMemoryRandomAccessStream();
        using var writer = new DataWriter(stream);
        writer.WriteBytes(bytes);
        await writer.StoreAsync();
        // 流要返回给调用方继续读：先解除 writer 对它的所有权，之后 Dispose writer 才不会把流一起关掉。
        writer.DetachStream();
        stream.Seek(0);
        return stream;
    }

    private static async Task<byte[]> ReadStreamAsync(IRandomAccessStream stream)
    {
        stream.Seek(0);
        var bytes = new byte[stream.Size];
        // 流由调用方持有（如 PngToBitmapAsync 的 using），reader 不 Detach、只释放自身即可，
        // Dispose reader 不会关掉它没有所有权的流。
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    internal static void ForceOpaque(byte[] pixels)
    {
        if (pixels.Length < 4)
        {
            return;
        }

        if (HasVisibleAlpha(pixels))
        {
            return;
        }

        FillAlpha(pixels);
    }

    private static readonly Vector<byte> AlphaMask = CreateAlphaMask();

    private static bool SimdAlpha => Vector.IsHardwareAccelerated && (Vector<byte>.Count & 3) == 0;

    private static Vector<byte> CreateAlphaMask()
    {
        var bytes = new byte[Vector<byte>.Count];
        for (var i = 3; i < bytes.Length; i += 4)
        {
            bytes[i] = 255;
        }

        return new Vector<byte>(bytes);
    }

    private static bool HasVisibleAlpha(byte[] pixels)
    {
        var span = pixels.AsSpan();
        var offset = 0;
        if (SimdAlpha)
        {
            var mask = AlphaMask;
            var step = Vector<byte>.Count;
            var limit = span.Length - step;
            while (offset <= limit)
            {
                var block = Unsafe.ReadUnaligned<Vector<byte>>(ref MemoryMarshal.GetReference(span[offset..]));
                if ((block & mask) != Vector<byte>.Zero)
                {
                    return true;
                }

                offset += step;
            }
        }

        for (var i = offset + 3; i < span.Length; i += 4)
        {
            if (span[i] != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void FillAlpha(byte[] pixels)
    {
        var span = pixels.AsSpan();
        var offset = 0;
        if (SimdAlpha)
        {
            var mask = AlphaMask;
            var step = Vector<byte>.Count;
            var limit = span.Length - step;
            while (offset <= limit)
            {
                ref var start = ref MemoryMarshal.GetReference(span[offset..]);
                var block = Unsafe.ReadUnaligned<Vector<byte>>(ref start);
                Unsafe.WriteUnaligned(ref start, block | mask);
                offset += step;
            }
        }

        for (var i = offset + 3; i < span.Length; i += 4)
        {
            span[i] = 255;
        }
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        void GetBuffer(out IntPtr buffer, out uint capacity);
    }
}
