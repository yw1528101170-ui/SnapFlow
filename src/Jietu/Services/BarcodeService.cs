using ZXing;
using ZXing.Common;

namespace Jietu;

internal record BarcodeResult(
    string Text,
    string Format,
    bool IsUrl,
    int Left,
    int Top,
    int Width,
    int Height
);

internal static class BarcodeService
{
    public static BarcodeResult? Scan(byte[] bgraPixels, int width, int height)
    {
        if (bgraPixels.Length < width * height * 4 || width <= 0 || height <= 0)
        {
            return null;
        }

        try
        {
            var luminance = new RGBLuminanceSource(bgraPixels, width, height, RGBLuminanceSource.BitmapFormat.BGRA32);
            var binarizer = new HybridBinarizer(luminance);
            var binBitmap = new BinaryBitmap(binarizer);

            var reader = new MultiFormatReader();
            var hints = new Dictionary<DecodeHintType, object>
            {
                [DecodeHintType.TRY_HARDER] = true,
                [DecodeHintType.POSSIBLE_FORMATS] = new List<BarcodeFormat>
                {
                    BarcodeFormat.QR_CODE,
                    BarcodeFormat.DATA_MATRIX,
                    BarcodeFormat.CODE_128,
                    BarcodeFormat.EAN_13,
                    BarcodeFormat.CODE_39,
                    BarcodeFormat.UPC_A
                }
            };

            var result = reader.decode(binBitmap, hints);
            if (result == null || string.IsNullOrWhiteSpace(result.Text))
            {
                return null;
            }

            var points = result.ResultPoints;
            int minX = width;
            int minY = height;
            int maxX = 0;
            int maxY = 0;
            if (points != null && points.Length > 0)
            {
                foreach (var p in points)
                {
                    minX = Math.Min(minX, (int)p.X);
                    minY = Math.Min(minY, (int)p.Y);
                    maxX = Math.Max(maxX, (int)p.X);
                    maxY = Math.Max(maxY, (int)p.Y);
                }
            }
            else
            {
                minX = 0;
                minY = 0;
                maxX = width;
                maxY = height;
            }

            var text = result.Text.Trim();
            var isUrl = Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
                        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

            return new BarcodeResult(
                text,
                result.BarcodeFormat.ToString(),
                isUrl,
                Math.Clamp(minX, 0, width),
                Math.Clamp(minY, 0, height),
                Math.Max(1, Math.Min(width, maxX) - Math.Clamp(minX, 0, width)),
                Math.Max(1, Math.Min(height, maxY) - Math.Clamp(minY, 0, height))
            );
        }
        catch (Exception ex)
        {
            Log.Info("条码识别跳过: " + ex.Message);
            return null;
        }
    }
}
