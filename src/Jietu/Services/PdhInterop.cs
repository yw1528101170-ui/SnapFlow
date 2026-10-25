using System.Runtime.InteropServices;

namespace Jietu;

/// <summary>
/// PDH 性能计数器互操作（任务管理器/资源监视器的同源数据）。
/// 统一走 PdhAddEnglishCounterW 的英文计数器路径，任何显示语言的 Windows 都能取到。
/// 速率类计数器（Bytes/sec、Utilization）需要两次 Collect 之后才有格式化值。
/// </summary>
internal static class PdhInterop
{
    public const uint FmtDouble = 0x00000200; // PDH_FMT_DOUBLE
    private const int PdhMoreData = unchecked((int)0x800007D2);

    [DllImport("pdh.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int PdhOpenQuery(IntPtr szDataSource, IntPtr dwUserData, out IntPtr phQuery);

    [DllImport("pdh.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int PdhAddEnglishCounterW(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);

    [DllImport("pdh.dll", SetLastError = true)]
    internal static extern int PdhCollectQueryData(IntPtr hQuery);

    [DllImport("pdh.dll", SetLastError = true)]
    internal static extern int PdhCloseQuery(IntPtr hQuery);

    [DllImport("pdh.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int PdhGetFormattedCounterArrayW(
        IntPtr hCounter, uint dwFormat, ref uint lpdwBufferSize, ref uint lpdwItemCount, IntPtr pItemBuffer);

    [DllImport("pdh.dll", SetLastError = true)]
    private static extern int PdhGetFormattedCounterValue(
        IntPtr hCounter, uint dwFormat, out IntPtr lpdwType, out PdhCounterValue pValue);

    [StructLayout(LayoutKind.Explicit)]
    private struct PdhCounterValue
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PdhCounterValueItem
    {
        public IntPtr Name;
        public PdhCounterValue Value;
    }

    /// <summary>读单实例计数器（如 _Total）；数据未就绪或无效返回 false。</summary>
    public static bool TryReadSingle(IntPtr counter, out double value)
    {
        value = 0;
        if (counter == IntPtr.Zero)
        {
            return false;
        }

        if (PdhGetFormattedCounterValue(counter, FmtDouble, out _, out var raw) != 0 || raw.CStatus != 0)
        {
            return false;
        }

        value = raw.DoubleValue;
        return value >= 0;
    }

    /// <summary>
    /// 读通配实例计数器。缓冲区由调用方持有并跨次复用；实例数据未就绪（首次 Collect 后）
    /// 或没有有效实例时返回 false。PDH 偶发给出的负哨兵值实例直接跳过。
    /// </summary>
    public static bool TryReadArray(IntPtr counter, ref IntPtr buffer, ref uint bufferSize,
        List<(string Instance, double Value)> results)
    {
        results.Clear();
        if (counter == IntPtr.Zero)
        {
            return false;
        }

        uint need = 0, count = 0;
        if (PdhGetFormattedCounterArrayW(counter, FmtDouble, ref need, ref count, IntPtr.Zero) != PdhMoreData || count == 0)
        {
            return false;
        }

        if (bufferSize < need)
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }

            buffer = Marshal.AllocHGlobal((int)need);
            bufferSize = need;
        }

        need = bufferSize;
        count = 0;
        if (PdhGetFormattedCounterArrayW(counter, FmtDouble, ref need, ref count, buffer) != 0)
        {
            return false;
        }

        int itemSize = Marshal.SizeOf<PdhCounterValueItem>();
        for (int i = 0; i < count; i++)
        {
            var item = Marshal.PtrToStructure<PdhCounterValueItem>(IntPtr.Add(buffer, i * itemSize));
            if (item.Value.CStatus != 0 || item.Value.DoubleValue <= 0 || item.Name == IntPtr.Zero)
            {
                continue;
            }

            results.Add((Marshal.PtrToStringUni(item.Name) ?? string.Empty, item.Value.DoubleValue));
        }

        return results.Count > 0;
    }
}

/// <summary>已打开的 PDH 查询：一次 Collect 同时刷新查询内全部计数器。</summary>
internal sealed class PdhQuery : IDisposable
{
    private IntPtr _handle;

    public static PdhQuery? TryOpen()
        => PdhInterop.PdhOpenQuery(IntPtr.Zero, IntPtr.Zero, out var handle) == 0 && handle != IntPtr.Zero
            ? new PdhQuery(handle)
            : null;

    private PdhQuery(IntPtr handle) => _handle = handle;

    /// <summary>添加英文计数器；路径在当前系统不存在（如无 GPU 计数器）返回 IntPtr.Zero。</summary>
    public IntPtr AddCounter(string englishPath)
        => _handle != IntPtr.Zero && PdhInterop.PdhAddEnglishCounterW(_handle, englishPath, IntPtr.Zero, out var counter) == 0
            ? counter
            : IntPtr.Zero;

    public bool Collect() => _handle != IntPtr.Zero && PdhInterop.PdhCollectQueryData(_handle) == 0;

    public void Dispose()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        try { PdhInterop.PdhCloseQuery(_handle); } catch { }
        _handle = IntPtr.Zero;
    }
}

/// <summary>多实例计数器的读取缓冲：跨次采样复用同一块非托管内存，Dispose 时释放。</summary>
internal sealed class PdhArrayBuffer : IDisposable
{
    private IntPtr _buffer;
    private uint _size;

    public bool Read(IntPtr counter, List<(string Instance, double Value)> results)
        => PdhInterop.TryReadArray(counter, ref _buffer, ref _size, results);

    public void Dispose()
    {
        if (_buffer == IntPtr.Zero)
        {
            return;
        }

        try { Marshal.FreeHGlobal(_buffer); } catch { }
        _buffer = IntPtr.Zero;
        _size = 0;
    }
}
