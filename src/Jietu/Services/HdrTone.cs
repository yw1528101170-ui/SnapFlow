namespace Jietu;

internal static class HdrTone
{
    // scRGB 基准白点：线性 1.0 对应 80 nit（Windows 高级颜色的既定约定）。
    private const float ReferenceWhiteNits = 80f;

    // 高光滚降起点（归一化后的线性值）。0.95 以下完全线性，SDR 内容中间调不受压缩影响。
    private const float HighlightKnee = 0.95f;

    private static readonly object TableGate = new();
    private static readonly byte[] ReferenceTable = CreateTable(ReferenceWhiteNits);
    private static byte[]? _whiteTable;
    private static float _whiteTableNits = float.NaN;

    /// <summary>
    /// 按 SDR 白点生成「半精度位 → sRGB 字节」的查表：
    /// 先把 scRGB 线性值归一化到「SDR 白 = 1」（<c>linear * 80 / sdrWhiteNits</c>），再走 sRGB 传输函数。
    /// 白点高于 80 nit（HDR 屏）时对 &gt;0.95 的部分做温和高光滚降，避免高光一片死白；
    /// 白点 = 80 nit 时与旧实现逐字节一致（SDR 屏/旧 4 参重载保持历史语义）。
    /// </summary>
    private static byte[] CreateTable(float sdrWhiteNits)
    {
        // 低于基准的白点按基准处理：正常显示器的 SDR 白不会低于 80 nit。
        var white = sdrWhiteNits > ReferenceWhiteNits ? sdrWhiteNits : ReferenceWhiteNits;
        var scale = ReferenceWhiteNits / white;
        var compress = white > ReferenceWhiteNits + 0.5f;
        var table = new byte[65536];
        for (var i = 0; i < 65536; i++)
        {
            var bits = (ushort)i;
            var linear = (float)BitConverter.UInt16BitsToHalf(bits);
            if (float.IsNaN(linear) || linear <= 0)
            {
                table[i] = 0;
                continue;
            }

            var normalized = linear * scale;
            if (compress && normalized > HighlightKnee)
            {
                // t = knee + (1 - knee) * (1 - e^(-(x - knee) / (1 - knee)))
                // 单调递增、值域 [knee, 1)：SDR 白映射到约 0.982（≈253/255），
                // HDR 高光（x≫1）平滑趋近 1，最终仍是纯白；不会出现硬截断的一刀切。
                normalized = HighlightKnee
                    + ((1f - HighlightKnee) * (1f - MathF.Exp(-(normalized - HighlightKnee) / (1f - HighlightKnee))));
            }

            table[i] = ToByte(normalized);
        }

        return table;
    }

    private static byte[] TableFor(float sdrWhiteNits)
    {
        if (float.IsNaN(sdrWhiteNits) || float.IsInfinity(sdrWhiteNits) || sdrWhiteNits <= ReferenceWhiteNits)
        {
            return ReferenceTable;
        }

        lock (TableGate)
        {
            if (_whiteTable != null && MathF.Abs(_whiteTableNits - sdrWhiteNits) < 0.01f)
            {
                return _whiteTable;
            }

            // 白点变化时重建一张 65536 项表：约几毫秒，只在切换显示器/HDR 设置时发生。
            var table = CreateTable(sdrWhiteNits);
            _whiteTable = table;
            _whiteTableNits = sdrWhiteNits;
            return table;
        }
    }

    /// <summary>
    /// scRGB 线性值按 80 nit 基准映射为 sRGB 字节（含义保持不变，SelfTest 依赖）。
    /// </summary>
    public static byte ToByte(float linear)
    {
        if (float.IsNaN(linear) || linear <= 0)
        {
            return 0;
        }

        if (linear >= 1)
        {
            return 255;
        }

        var encoded = linear <= 0.0031308f
            ? linear * 12.92f
            : (1.055f * MathF.Pow(linear, 1f / 2.4f)) - 0.055f;
        return (byte)Math.Clamp((int)Math.Round(encoded * 255f), 0, 255);
    }

    /// <summary>
    /// 旧签名：按 80 nit 基准白点转换（与历史版本行为一致，供现有调用方/SelfTest 使用）。
    /// </summary>
    public static byte[] FromFloat16(IntPtr data, int width, int height, int rowPitch)
        => FromFloat16(data, width, height, rowPitch, ReferenceWhiteNits);

    /// <summary>
    /// 把 DXGI 桌面复制给的 R16G16B16A16_FLOAT（scRGB）帧转成 BGRA 字节。
    /// <paramref name="sdrWhiteNits"/> 为显示器当前 SDR 白点（nit，见 <c>HdrDisplay.SdrWhiteLevel</c>）：
    /// SDR 内容在 200 nit 白点下线性值会被放大到 2.5，按 80/白点 归一化后才不会过曝。
    /// </summary>
    public static byte[] FromFloat16(IntPtr data, int width, int height, int rowPitch, float sdrWhiteNits)
    {
        var pixels = new byte[width * height * 4];
        var table = TableFor(sdrWhiteNits);
        // 逐像素 Marshal.ReadInt16 在 4K 一帧约 2500 万次 P/Invoke；改为整行 Copy 进托管数组后用普通索引。
        // 临时行缓冲只在本方法栈内使用（HdrTone 保持无可变共享状态，除既有的表缓存）。
        var rowBytes = rowPitch;
        if (rowBytes < width * 8)
        {
            // 理论上不会发生（DirectX 行距不小于像素跨度），防御性回退到逐像素读取。
            return FromFloat16Slow(data, width, height, rowPitch, table);
        }

        var row = new byte[rowBytes];
        for (var y = 0; y < height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(data + (y * rowPitch), row, 0, rowBytes);
            var source = 0;
            var target = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                // R16G16B16A16_FLOAT 小端内存序：R,G,B,A 各占 2 字节。
                // 输出缓冲是 BGRA（byte0=B）：写入顺序必须反过来，否则红蓝互换。
                var rBits = (ushort)(row[source] | (row[source + 1] << 8));
                var gBits = (ushort)(row[source + 2] | (row[source + 3] << 8));
                var bBits = (ushort)(row[source + 4] | (row[source + 5] << 8));
                pixels[target++] = table[bBits];
                pixels[target++] = table[gBits];
                pixels[target++] = table[rBits];
                pixels[target++] = 255;
                source += 8;
            }
        }

        return pixels;
    }

    private static byte[] FromFloat16Slow(IntPtr data, int width, int height, int rowPitch, byte[] table)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var row = data + (y * rowPitch);
            var target = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var pixel = row + (x * 8);
                var rBits = unchecked((ushort)System.Runtime.InteropServices.Marshal.ReadInt16(pixel));
                var gBits = unchecked((ushort)System.Runtime.InteropServices.Marshal.ReadInt16(pixel + 2));
                var bBits = unchecked((ushort)System.Runtime.InteropServices.Marshal.ReadInt16(pixel + 4));
                pixels[target++] = table[bBits];
                pixels[target++] = table[gBits];
                pixels[target++] = table[rBits];
                pixels[target++] = 255;
            }
        }

        return pixels;
    }

    private static float ReadHalf(IntPtr address)
    {
        var bits = unchecked((ushort)System.Runtime.InteropServices.Marshal.ReadInt16(address));
        return (float)BitConverter.UInt16BitsToHalf(bits);
    }
}
