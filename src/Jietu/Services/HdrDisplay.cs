using System.Runtime.InteropServices;

namespace Jietu;

internal static class HdrDisplay
{
    // SDR 白点查询结果的短缓存：避免每次抓屏都走一遍 DisplayConfig 查询（切换 HDR/换屏 2 秒内生效）。
    private const long WhiteCacheMs = 2000;

    private static readonly object WhiteGate = new();
    private static IntPtr _whiteMonitor;
    private static float _whiteValue = 80f;
    private static long _whiteStamp;
    private static bool _whiteKnown;

    public static bool IsActive(IntPtr monitor)
    {
        try
        {
            var info = new NativeMethods.MonitorInfo { CbSize = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
            if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                return false;
            }

            return AnyAdvancedColor(info.RcMonitor.Left, info.RcMonitor.Top);
        }
        catch (Exception ex)
        {
            Log.Info("检查高动态范围失败：" + ex.Message);
            return false;
        }
    }

    private static bool AnyAdvancedColor(int x, int y)
    {
        if (!TryReadActivePaths(out var paths, out var pathCount, out var modes, out var modeCount))
        {
            return false;
        }

        for (var index = 0; index < pathCount; index++)
        {
            var modeIndex = paths[index].SourceModeIndex;
            if (modeIndex >= modeCount || modes[modeIndex].InfoType != 1)
            {
                continue;
            }

            if (modes[modeIndex].PositionX != x || modes[modeIndex].PositionY != y)
            {
                continue;
            }

            var query = new AdvancedColorInfo
            {
                Type = 9,
                Size = (uint)Marshal.SizeOf<AdvancedColorInfo>(),
                AdapterLow = paths[index].TargetAdapterLow,
                AdapterHigh = paths[index].TargetAdapterHigh,
                Id = paths[index].TargetId,
            };
            if (DisplayConfigGetDeviceInfo(ref query) == 0 && (query.Value & 2) != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 查询显示器当前的 SDR 白点（返回 nit）。Windows 高级颜色规范里
    /// <c>DISPLAYCONFIG_SDR_WHITE_LEVEL.SDRWhiteLevel</c> 以「80 nit 的倍数 × 1000」表示
    /// （1000 = 80 nit、2500 = 200 nit）。查询失败、显示器不支持（非 HDR）、驱动器回 0
    /// 或数值异常（&lt;=0 / &gt;100000）时返回 80f（scRGB 基准白点）。
    /// </summary>
    public static float SdrWhiteLevel(IntPtr monitor)
    {
        try
        {
            lock (WhiteGate)
            {
                if (_whiteKnown && _whiteMonitor == monitor && Environment.TickCount64 - _whiteStamp < WhiteCacheMs)
                {
                    return _whiteValue;
                }
            }

            var value = QuerySdrWhiteLevel(monitor);
            lock (WhiteGate)
            {
                _whiteMonitor = monitor;
                _whiteValue = value;
                _whiteStamp = Environment.TickCount64;
                _whiteKnown = true;
            }

            return value;
        }
        catch (Exception ex)
        {
            Log.Info("查询 SDR 白点失败：" + ex.Message);
            return 80f;
        }
    }

    private static float QuerySdrWhiteLevel(IntPtr monitor)
    {
        if (!TryReadActivePaths(out var paths, out var pathCount, out var modes, out var modeCount))
        {
            return 80f;
        }

        var info = new NativeMethods.MonitorInfo { CbSize = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return 80f;
        }

        var x = info.RcMonitor.Left;
        var y = info.RcMonitor.Top;
        for (var index = 0; index < pathCount; index++)
        {
            // 与 AnyAdvancedColor 相同的定位方式：用源模式的位置把 DisplayConfig 路径对上显示器。
            var modeIndex = paths[index].SourceModeIndex;
            if (modeIndex >= modeCount || modes[modeIndex].InfoType != 1)
            {
                continue;
            }

            if (modes[modeIndex].PositionX != x || modes[modeIndex].PositionY != y)
            {
                continue;
            }

            var query = new SdrWhiteLevelInfo
            {
                Type = 11, // DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL
                Size = (uint)Marshal.SizeOf<SdrWhiteLevelInfo>(),
                AdapterLow = paths[index].TargetAdapterLow,
                AdapterHigh = paths[index].TargetAdapterHigh,
                Id = paths[index].TargetId,
            };
            if (DisplayConfigGetDeviceInfo(ref query) != 0)
            {
                continue;
            }

            if (query.SdrWhiteLevel == 0 || query.SdrWhiteLevel > 100000)
            {
                // 非 HDR / 未启用高级颜色时部分驱动回 0，按 scRGB 基准白点处理。
                return 80f;
            }

            var nits = query.SdrWhiteLevel * 80f / 1000f;
            return nits is >= 10f and <= 1000f ? nits : 80f;
        }

        return 80f;
    }

    private static bool TryReadActivePaths(
        out DisplayConfigPath[] paths,
        out uint pathCount,
        out DisplayConfigMode[] modes,
        out uint modeCount)
    {
        const uint activePaths = 2;
        paths = Array.Empty<DisplayConfigPath>();
        modes = Array.Empty<DisplayConfigMode>();
        pathCount = 0;
        modeCount = 0;
        if (GetDisplayConfigBufferSizes(activePaths, out var foundPaths, out var foundModes) != 0 || foundPaths == 0 || foundModes == 0)
        {
            return false;
        }

        paths = new DisplayConfigPath[foundPaths];
        modes = new DisplayConfigMode[foundModes];
        pathCount = foundPaths;
        modeCount = foundModes;
        if (QueryDisplayConfig(activePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
        {
            return false;
        }

        return true;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint pathCount,
        [Out] DisplayConfigPath[] paths,
        ref uint modeCount,
        [Out] DisplayConfigMode[] modes,
        IntPtr topology);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref AdvancedColorInfo info);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref SdrWhiteLevelInfo info);

    /// <summary>
    /// DISPLAYCONFIG_PATH_INFO（wingdi.h）：sourceInfo(20) + targetInfo(48) + flags(4) = <b>72 字节</b>。
    /// QueryDisplayConfig 原生侧按 sizeof(DISPLAYCONFIG_PATH_INFO)=72 字节/个写入数组，
    /// 因此托管结构必须保持 72 字节；若按更大的尺寸（如 128）填充，从第 2 个路径起整体错位。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigPath
    {
        // DISPLAYCONFIG_PATH_SOURCE_INFO（0..19）
        public uint SourceAdapterLow;    // LUID.LowPart   @0
        public int SourceAdapterHigh;    // LUID.HighPart  @4
        public uint SourceId;            //                @8
        public uint SourceModeIndex;     // modeInfoIdx 联合 @12
        public uint SourceStatus;        // statusFlags    @16
        // DISPLAYCONFIG_PATH_TARGET_INFO（20..67）
        public uint TargetAdapterLow;    // LUID.LowPart   @20
        public int TargetAdapterHigh;    // LUID.HighPart  @24
        public uint TargetId;            //                @28
        public uint TargetModeIndex;     // modeInfoIdx 联合 @32
        public uint OutputTechnology;    //                @36
        public uint Rotation;            //                @40
        public uint Scaling;             //                @44
        public uint RefreshNumerator;    // refreshRate    @48
        public uint RefreshDenominator;  //                @52
        public uint ScanLineOrdering;    //                @56
        public int TargetAvailable;      // BOOL           @60
        public uint TargetStatus;        // statusFlags    @64
        // 路径级 flags（68..71）
        public uint Flags;               //                @68
    }

    /// <summary>
    /// DISPLAYCONFIG_MODE_INFO（wingdi.h）：infoType(0) + id(4) + adapterId(8..15) + union(16..63) = <b>64 字节</b>。
    /// 本代码只用 source 分支：DISPLAYCONFIG_SOURCE_MODE = { width, height, pixelFormat, position }，
    /// 映射进联合后 Width@16、Height@20、PixelFormat@24、PositionX@28、PositionY@32。
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct DisplayConfigMode
    {
        [FieldOffset(0)] public uint InfoType;      // DISPLAYCONFIG_MODE_INFO_TYPE（1 = SOURCE）
        [FieldOffset(16)] public uint Width;        // sourceMode.width
        [FieldOffset(20)] public uint Height;       // sourceMode.height
        [FieldOffset(24)] public uint PixelFormat;  // sourceMode.pixelFormat
        [FieldOffset(28)] public int PositionX;     // sourceMode.position.x
        [FieldOffset(32)] public int PositionY;     // sourceMode.position.y
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdvancedColorInfo
    {
        public uint Type;
        public uint Size;
        public uint AdapterLow;
        public int AdapterHigh;
        public uint Id;
        public uint Value;
        public uint ColorEncoding;
        public uint BitsPerChannel;
    }

    // DISPLAYCONFIG_SDR_WHITE_LEVEL：header（type/size/adapterId/id）+ SDRWhiteLevel（80 nit 的倍数 × 1000）。
    [StructLayout(LayoutKind.Sequential)]
    private struct SdrWhiteLevelInfo
    {
        public uint Type;
        public uint Size;
        public uint AdapterLow;
        public int AdapterHigh;
        public uint Id;
        public uint SdrWhiteLevel;
    }
}
