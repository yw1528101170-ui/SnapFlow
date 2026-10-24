using System;
using System.IO;
using System.IO.Compression;

namespace IconGen;

internal static class Program
{
    private static void Main()
    {
        Console.WriteLine("Generating SnapFlow Fluent Icon...");
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

        // Size-dependent squircle parameters
        var squircleRad = size <= 20 ? 0.92 : (size <= 32 ? 0.88 : 0.85);
        var shadowOffset = size <= 20 ? 0.035 : (size <= 32 ? 0.045 : 0.065);
        var shadowBlur = size <= 20 ? 0.08 : (size <= 32 ? 0.12 : 0.18);

        // 1. Ambient Drop Shadow under Squircle (Deep Indigo/Slate)
        var sDist = SquircleDist(cx, cy - shadowOffset, squircleRad, squircleRad, 3.8);
        if (sDist <= 0)
        {
            Blend(ref r, ref g, ref b, ref a, 0.06, 0.08, 0.22, 0.26);
        }
        else if (sDist < shadowBlur)
        {
            var sa = Math.Clamp(1.0 - sDist / shadowBlur, 0.0, 1.0);
            sa = sa * sa * 0.26;
            Blend(ref r, ref g, ref b, ref a, 0.06, 0.08, 0.22, sa);
        }

        // 2. Main Squircle Body (Fluent Aurora: Electric Sky Blue -> Royal Blue -> Iris / Violet)
        var bodyDist = SquircleDist(cx, cy, squircleRad, squircleRad, 3.8);
        if (bodyDist <= 0)
        {
            // Diagonal gradient from top-left (-0.7, -0.7) to bottom-right (0.7, 0.7)
            var t = Math.Clamp((cx * 0.65 + cy * 0.75 + 1.1) / 2.2, 0.0, 1.0);

            // Top-left: Vibrant Cyan / Sky Blue (0, 185, 255)
            // Mid: Deep Electric Royal (45, 110, 250)
            // Bottom-right: Iris / Violet (115, 65, 245)
            double bgR, bgG, bgB;
            if (t < 0.45)
            {
                var k = t / 0.45;
                bgR = 0.0 + (45.0 - 0.0) * k;
                bgG = 185.0 + (110.0 - 185.0) * k;
                bgB = 255.0 + (250.0 - 255.0) * k;
            }
            else
            {
                var k = (t - 0.45) / 0.55;
                bgR = 45.0 + (115.0 - 45.0) * k;
                bgG = 110.0 + (65.0 - 110.0) * k;
                bgB = 250.0 + (245.0 - 250.0) * k;
            }

            // Upper-left specular light wash
            var lightDist = Math.Sqrt((x - 0.24) * (x - 0.24) + (y - 0.20) * (y - 0.20));
            var radial = Math.Clamp(1.0 - lightDist / 0.90, 0.0, 1.0);
            bgR += radial * 28.0;
            bgG += radial * 32.0;
            bgB += radial * 15.0;

            // Micro-chamfer / Bevel rim
            if (bodyDist > -0.065)
            {
                var rimT = (bodyDist + 0.065) / 0.065;
                var facingTL = Math.Clamp((-cx - cy + 0.3) * 0.75, 0.0, 1.0);
                var facingBR = Math.Clamp((cx + cy + 0.3) * 0.75, 0.0, 1.0);

                // Top-left catches crisp white reflection; bottom-right catches subtle occlusion
                bgR = bgR * (1.0 - facingBR * 0.25 * rimT) + 255.0 * facingTL * 0.35 * rimT;
                bgG = bgG * (1.0 - facingBR * 0.25 * rimT) + 255.0 * facingTL * 0.35 * rimT;
                bgB = bgB * (1.0 - facingBR * 0.25 * rimT) + 255.0 * facingTL * 0.35 * rimT;
            }

            Blend(ref r, ref g, ref b, ref a, bgR / 255.0, bgG / 255.0, bgB / 255.0, 1.0);

            // 3. Inner Acrylic Translucent Plate (for size >= 32)
            if (size >= 32)
            {
                var innerDist = SquircleDist(cx, cy, 0.68, 0.68, 3.2);
                if (innerDist <= 0)
                {
                    // Subtle translucent frosty sheen
                    var innerT = Math.Clamp((-cy * 0.5 + 0.5), 0.0, 1.0);
                    var innerAlpha = 0.08 + 0.06 * innerT;
                    Blend(ref r, ref g, ref b, ref a, 1.0, 1.0, 1.0, innerAlpha);
                }
            }
        }

        // 4. Center Aperture / Focus Ring (The "Flow" Lens element, for size >= 32)
        if (size >= 32)
        {
            var ringRadius = size <= 48 ? 0.26 : 0.28;
            var ringThick = size <= 48 ? 0.040 : 0.034;
            var distToCenter = Math.Sqrt(cx * cx + cy * cy);
            var ringDist = Math.Abs(distToCenter - ringRadius) - ringThick;

            // Ring shadow
            if (size >= 48)
            {
                var ringShadowDist = Math.Abs(Math.Sqrt(cx * cx + (cy - 0.02) * (cy - 0.02)) - ringRadius) - ringThick;
                if (ringShadowDist <= 0)
                {
                    Blend(ref r, ref g, ref b, ref a, 0.05, 0.08, 0.25, 0.22);
                }
            }

            if (ringDist <= 0)
            {
                // Ring gradient: cyan-white to soft purple
                var ringAngleT = Math.Clamp((cx - cy + 0.5) * 0.7, 0.0, 1.0);
                var ringR = 0.85 + 0.15 * ringAngleT;
                var ringG = 0.95 + 0.05 * ringAngleT;
                var ringB = 1.0;
                Blend(ref r, ref g, ref b, ref a, ringR, ringG, ringB, 0.75);
            }
            else if (ringDist < 0.015)
            {
                var ringAlpha = (1.0 - ringDist / 0.015) * 0.75;
                Blend(ref r, ref g, ref b, ref a, 0.90, 0.98, 1.0, ringAlpha);
            }
        }

        // 5. Precision Viewfinder Corner Reticles (The "Snap" Frames)
        // Shadow under reticles (size >= 24)
        if (size >= 24)
        {
            var shadowReticleDist = ReticleSdf(cx, cy - 0.025, size);
            if (shadowReticleDist <= 0)
            {
                Blend(ref r, ref g, ref b, ref a, 0.04, 0.06, 0.20, 0.36);
            }
            else if (shadowReticleDist < 0.045)
            {
                var sAlpha = (1.0 - shadowReticleDist / 0.045) * 0.36;
                Blend(ref r, ref g, ref b, ref a, 0.04, 0.06, 0.20, sAlpha);
            }
        }

        // Viewfinder reticle body (Pure crisp luminous white)
        var reticleDist = ReticleSdf(cx, cy, size);
        if (reticleDist <= 0)
        {
            Blend(ref r, ref g, ref b, ref a, 1.0, 1.0, 1.0, 1.0);
        }
        else if (reticleDist < 0.018)
        {
            var alpha = 1.0 - reticleDist / 0.018;
            Blend(ref r, ref g, ref b, ref a, 1.0, 1.0, 1.0, alpha);
        }

        // 6. Central Focus Core (Precision Dot & Glow)
        if (size >= 20)
        {
            var dotRadius = size <= 20 ? 0.095 : (size <= 24 ? 0.090 : (size <= 48 ? 0.082 : 0.075));

            // Soft cyan halo around center dot (at size >= 32)
            if (size >= 32)
            {
                var haloDist = Math.Sqrt(cx * cx + cy * cy) - dotRadius;
                if (haloDist > 0 && haloDist < 0.08)
                {
                    var haloAlpha = Math.Clamp(1.0 - haloDist / 0.08, 0.0, 1.0) * 0.38;
                    Blend(ref r, ref g, ref b, ref a, 0.55, 0.88, 1.0, haloAlpha);
                }
            }

            // Dot shadow
            var dotShadowDist = Math.Sqrt(cx * cx + (cy - 0.02) * (cy - 0.02)) - dotRadius;
            if (dotShadowDist <= 0)
            {
                Blend(ref r, ref g, ref b, ref a, 0.04, 0.06, 0.20, 0.30);
            }

            // Dot body (Pure crisp white)
            var dotDist = Math.Sqrt(cx * cx + cy * cy) - dotRadius;
            if (dotDist <= 0)
            {
                Blend(ref r, ref g, ref b, ref a, 1.0, 1.0, 1.0, 1.0);
            }
            else if (dotDist < 0.015)
            {
                var alpha = 1.0 - dotDist / 0.015;
                Blend(ref r, ref g, ref b, ref a, 1.0, 1.0, 1.0, alpha);
            }
        }
    }

    private static double SquircleDist(double x, double y, double rx, double ry, double n)
    {
        var nx = Math.Abs(x / rx);
        var ny = Math.Abs(y / ry);
        var p = Math.Pow(Math.Pow(nx, n) + Math.Pow(ny, n), 1.0 / n);
        return p - 1.0;
    }

    private static double ReticleSdf(double x, double y, int size)
    {
        var px = Math.Abs(x);
        var py = Math.Abs(y);

        double cornerC, radius, armLen, halfThick;

        if (size <= 20)
        {
            // Crisp, bold corner brackets for 16x16 / 20x20
            cornerC = 0.56;
            radius = 0.13;
            armLen = 0.38;
            halfThick = 0.115;
        }
        else if (size <= 32)
        {
            cornerC = 0.53;
            radius = 0.14;
            armLen = 0.33;
            halfThick = 0.095;
        }
        else
        {
            cornerC = 0.50;
            radius = 0.15;
            armLen = 0.28;
            halfThick = 0.080;
        }

        var kx = cornerC - radius;
        var ky = cornerC - radius;

        double distToCenterline;

        if (px >= kx && py >= ky)
        {
            var arcDist = Math.Sqrt((px - kx) * (px - kx) + (py - ky) * (py - ky));
            distToCenterline = Math.Abs(arcDist - radius);
        }
        else if (py >= ky)
        {
            distToCenterline = DistToSegment(px, py, cornerC - armLen, cornerC, kx, cornerC);
        }
        else if (px >= kx)
        {
            distToCenterline = DistToSegment(px, py, cornerC, cornerC - armLen, cornerC, ky);
        }
        else
        {
            var dHoriz = DistToSegment(px, py, cornerC - armLen, cornerC, kx, cornerC);
            var dVert = DistToSegment(px, py, cornerC, cornerC - armLen, cornerC, ky);
            distToCenterline = Math.Min(dHoriz, dVert);
        }

        return distToCenterline - halfThick;
    }

    private static double DistToSegment(double px, double py, double x1, double y1, double x2, double y2)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        var l2 = dx * dx + dy * dy;
        if (l2 < 1e-9)
        {
            return Math.Sqrt((px - x1) * (px - x1) + (py - y1) * (py - y1));
        }

        var t = Math.Clamp(((px - x1) * dx + (py - y1) * dy) / l2, 0.0, 1.0);
        var qx = x1 + t * dx;
        var qy = y1 + t * dy;
        return Math.Sqrt((px - qx) * (px - qx) + (py - qy) * (py - qy));
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
