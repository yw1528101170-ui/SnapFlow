using System;
using System.IO;
using System.IO.Compression;

namespace IconGen;

// SnapFlow 图标生成器：扁平单色风格。
// 设计：品牌蓝 #2D6EFA 纯色方圆基底（无渐变无光影），
//       图案为两片层叠卡片（后片半透白微露旋转，前片实心白）+ 右上四芒星光。
//       全部 SDF 程序化绘制 + 超采样抗锯齿。
internal static class Program
{
    private static void Main()
    {
        Console.WriteLine("Generating SnapFlow Flat Icon...");
        var sizes = new[] { 256, 128, 64, 48, 40, 32, 24, 20, 16 };
        var images = new (int Size, byte[] Png)[sizes.Length];

        var outDir = AppContext.BaseDirectory;
        for (var i = 0; i < sizes.Length; i++)
        {
            var size = sizes[i];
            var rgba = RenderIcon(size);
            var png = EncodePng(rgba, size, size);
            images[i] = (size, png);
            var previewPath = Path.Combine(outDir, $"icon_{size}.png");
            File.WriteAllBytes(previewPath, png);
            Console.WriteLine($"Rendered {size}x{size} ({png.Length} bytes)");
        }

        var ico = PackIco(images);
        var icoPath = Path.Combine(outDir, "App.ico");
        File.WriteAllBytes(icoPath, ico);
        Console.WriteLine($"Saved App.ico ({ico.Length} bytes)");

        // Copy directly into src/Jietu/Assets/App.ico if found
        var projectAssets = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\src\Jietu\Assets\App.ico"));
        if (Directory.Exists(Path.GetDirectoryName(projectAssets)!))
        {
            File.WriteAllBytes(projectAssets, ico);
            Console.WriteLine($"Updated project asset: {projectAssets}");
        }
        else
        {
            var altPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\src\Jietu\Assets\App.ico"));
            if (Directory.Exists(Path.GetDirectoryName(altPath)))
            {
                File.WriteAllBytes(altPath, ico);
                Console.WriteLine($"Updated project asset: {altPath}");
            }
        }
    }

    private static byte[] RenderIcon(int size)
    {
        var buffer = new byte[size * size * 4];
        var ssaa = size <= 24 ? 10 : (size <= 48 ? 8 : 4);
        var step = 1.0 / ssaa;

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                double accR = 0, accG = 0, accB = 0, accA = 0;
                for (var sy = 0; sy < ssaa; sy++)
                {
                    var py = (y + (sy + 0.5) * step) / size;
                    for (var sx = 0; sx < ssaa; sx++)
                    {
                        var px = (x + (sx + 0.5) * step) / size;
                        Sample(px, py, size, out var r, out var g, out var b, out var a);
                        accR += r * a;
                        accG += g * a;
                        accB += b * a;
                        accA += a;
                    }
                }

                var totalSamples = ssaa * ssaa;
                var finalA = accA / totalSamples;
                var finalR = finalA > 0.001 ? Math.Clamp(accR / accA, 0.0, 1.0) : 0;
                var finalG = finalA > 0.001 ? Math.Clamp(accG / accA, 0.0, 1.0) : 0;
                var finalB = finalA > 0.001 ? Math.Clamp(accB / accA, 0.0, 1.0) : 0;

                var idx = (y * size + x) * 4;
                buffer[idx] = (byte)Math.Round(finalR * 255.0);
                buffer[idx + 1] = (byte)Math.Round(finalG * 255.0);
                buffer[idx + 2] = (byte)Math.Round(finalB * 255.0);
                buffer[idx + 3] = (byte)Math.Round(finalA * 255.0);
            }
        }

        return buffer;
    }

    private static void Sample(double x, double y, int size, out double r, out double g, out double b, out double a)
    {
        r = 0; g = 0; b = 0; a = 0;

        var cx = (x - 0.5) * 2.0;
        var cy = (y - 0.5) * 2.0;

        var squircleRad = size <= 20 ? 0.92 : (size <= 32 ? 0.88 : 0.85);

        // 图案几何：后片左上微露并旋转，前片右下压住，右上一颗四芒星光
        double paneHalf, paneR, offX, offY, sparkleR;
        bool sparkle;
        switch (size)
        {
            case <= 20:
                paneHalf = 0.42; paneR = 0.14; offX = 0.085; offY = 0.10;
                sparkleR = 0; sparkle = false;
                break;
            case <= 48:
                paneHalf = 0.40; paneR = 0.13; offX = 0.12; offY = 0.14;
                sparkleR = 0.17; sparkle = true;
                break;
            default:
                paneHalf = 0.395; paneR = 0.135; offX = 0.13; offY = 0.15;
                sparkleR = 0.155; sparkle = true;
                break;
        }

        var rotRad = -9.0 * Math.PI / 180.0;
        var cosR = Math.Cos(rotRad);
        var sinR = Math.Sin(rotRad);

        // 1. 纯色方圆基底：品牌蓝 #2D6EFA，无渐变无光影
        var bodyDist = SquircleDist(cx, cy, squircleRad, squircleRad, 3.8);
        if (bodyDist <= 0)
        {
            Blend(ref r, ref g, ref b, ref a, 45.0 / 255.0, 110.0 / 255.0, 250.0 / 255.0, 1.0);
        }

        // 2. 后片：半透白（扁平设计的层次表达）
        var backDist = RoundedRectSdf(cx, cy, -offX, -offY, paneHalf, paneHalf, paneR, cosR, sinR);
        if (bodyDist <= 0 && backDist <= 0)
        {
            Blend(ref r, ref g, ref b, ref a, 1.0, 1.0, 1.0, 0.55);
        }

        // 3. 前片：实心白
        var frontDist = RoundedRectSdf(cx, cy, offX, offY, paneHalf, paneHalf, paneR, 1.0, 0.0);
        if (frontDist <= 0)
        {
            Blend(ref r, ref g, ref b, ref a, 1.0, 1.0, 1.0, 1.0);
        }

        // 4. 四芒星光（size>=32）
        if (sparkle && bodyDist <= 0)
        {
            var sd = SparkleSdf(cx - 0.50, cy + 0.44, sparkleR, 0.30);
            if (sd <= 0)
            {
                Blend(ref r, ref g, ref b, ref a, 1.0, 1.0, 1.0, 1.0);
            }
        }
    }

    /// <summary>圆角矩形 SDF（支持绕中心旋转）。</summary>
    private static double RoundedRectSdf(double px, double py, double cxx, double cyy, double halfW, double halfH, double rad, double cosA, double sinA)
    {
        var dx = px - cxx;
        var dy = py - cyy;
        var lx = dx * cosA + dy * sinA;
        var ly = -dx * sinA + dy * cosA;
        var qx = Math.Abs(lx) - (halfW - rad);
        var qy = Math.Abs(ly) - (halfH - rad);
        var ox = Math.Max(qx, 0);
        var oy = Math.Max(qy, 0);
        return Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - rad;
    }

    /// <summary>
    /// 四芒星光 SDF：两片细长菱形（横 + 竖）的并集——尖角在 N/E/S/W，
    /// 对角线方向深度收腰（腰宽 ≈ 0.24R），即经典的 "twinkle" 闪光轮廓。
    /// </summary>
    private static double SparkleSdf(double x, double y, double tipR, double waist)
    {
        var d1 = DiamondSdf(x, y, tipR, tipR * waist);
        var d2 = DiamondSdf(x, y, tipR * waist, tipR);
        return Math.Min(d1, d2);
    }

    private static double DiamondSdf(double x, double y, double a, double b)
    {
        var nx = Math.Abs(x) / a;
        var ny = Math.Abs(y) / b;
        var d = nx + ny - 1.0;
        return d / Math.Sqrt(1.0 / (a * a) + 1.0 / (b * b));
    }

    private static double SquircleDist(double x, double y, double rx, double ry, double n)
    {
        var nx = Math.Abs(x / rx);
        var ny = Math.Abs(y / ry);
        var p = Math.Pow(Math.Pow(nx, n) + Math.Pow(ny, n), 1.0 / n);
        return p - 1.0;
    }

    private static void Blend(ref double r, ref double g, ref double b, ref double a, double sr, double sg, double sb, double sa)
    {
        var outA = sa + a * (1.0 - sa);
        if (outA > 0.0001)
        {
            r = (sr * sa + r * a * (1.0 - sa)) / outA;
            g = (sg * sa + g * a * (1.0 - sa)) / outA;
            b = (sb * sa + b * a * (1.0 - sa)) / outA;
            a = outA;
        }
    }

    private static byte[] EncodePng(byte[] rgba, int width, int height)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        // IHDR
        using (var ihdr = new MemoryStream())
        {
            WriteBE32(ihdr, (uint)width);
            WriteBE32(ihdr, (uint)height);
            ihdr.WriteByte(8);
            ihdr.WriteByte(6); // RGBA
            ihdr.WriteByte(0);
            ihdr.WriteByte(0);
            ihdr.WriteByte(0);
            WriteChunk(ms, "IHDR", ihdr.ToArray());
        }

        // IDAT
        using (var raw = new MemoryStream())
        {
            for (var y = 0; y < height; y++)
            {
                raw.WriteByte(0);
                raw.Write(rgba, y * width * 4, width * 4);
            }

            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                var rawBytes = raw.ToArray();
                zlib.Write(rawBytes, 0, rawBytes.Length);
            }

            WriteChunk(ms, "IDAT", compressed.ToArray());
        }

        // IEND
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream dest, string type, byte[] data)
    {
        WriteBE32(dest, (uint)data.Length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        dest.Write(typeBytes, 0, 4);
        if (data.Length > 0)
        {
            dest.Write(data, 0, data.Length);
        }

        var crcPayload = new byte[4 + data.Length];
        Buffer.BlockCopy(typeBytes, 0, crcPayload, 0, 4);
        if (data.Length > 0)
        {
            Buffer.BlockCopy(data, 0, crcPayload, 4, data.Length);
        }

        var crc = ComputeCrc32(crcPayload);
        WriteBE32(dest, crc);
    }

    private static void WriteBE32(Stream s, uint val)
    {
        s.WriteByte((byte)(val >> 24));
        s.WriteByte((byte)(val >> 16));
        s.WriteByte((byte)(val >> 8));
        s.WriteByte((byte)val);
    }

    private static readonly uint[] CrcTable = CreateCrcTable();
    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            table[i] = c;
        }
        return table;
    }

    private static uint ComputeCrc32(byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }
        return c ^ 0xFFFFFFFFu;
    }

    private static byte[] PackIco((int Size, byte[] Png)[] images)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0); ms.WriteByte(0); // Reserved
        ms.WriteByte(1); ms.WriteByte(0); // Type 1: Icon
        ms.WriteByte((byte)images.Length); ms.WriteByte(0); // Count

        var offset = 6 + images.Length * 16;
        foreach (var (size, png) in images)
        {
            ms.WriteByte((byte)(size >= 256 ? 0 : size));
            ms.WriteByte((byte)(size >= 256 ? 0 : size));
            ms.WriteByte(0);
            ms.WriteByte(0);
            ms.WriteByte(1); ms.WriteByte(0);
            ms.WriteByte(32); ms.WriteByte(0);
            WriteLE32(ms, (uint)png.Length);
            WriteLE32(ms, (uint)offset);
            offset += png.Length;
        }

        foreach (var (_, png) in images)
        {
            ms.Write(png, 0, png.Length);
        }

        return ms.ToArray();
    }

    private static void WriteLE32(Stream s, uint val)
    {
        s.WriteByte((byte)val);
        s.WriteByte((byte)(val >> 8));
        s.WriteByte((byte)(val >> 16));
        s.WriteByte((byte)(val >> 24));
    }
}
