using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Jietu;

public sealed record HardwareMetrics(
    float GpuUsagePercent,
    float GpuTemperature,
    ulong GpuMemoryUsedBytes,
    ulong GpuMemoryTotalBytes,
    string GpuName,
    float CpuUsagePercent,
    float CpuFrequencyGhz,
    float RamUsagePercent,
    ulong RamUsedBytes,
    ulong RamTotalBytes,
    long NetDownloadSpeedBytesPerSec,
    long NetUploadSpeedBytesPerSec,
    float FrameRate,
    bool FrameRateMeasured,
    int RefreshRateHz);

public sealed class HardwareMonitorService : IDisposable
{
    public event Action<HardwareMetrics>? MetricsUpdated;

    private readonly System.Threading.Timer _timer;
    private readonly NvmlAdapter _nvml = new();
    private readonly FrameRateMeter _frameRate = new();
    private readonly DxgiPdhGpuAdapter _dxgiPdh = new();
    private float _cpuBaseFreqGhz;
    private float _lastValidGhz;

    // 服务级 PDH 查询：CPU 实时频率与网卡速率共用，一次 Collect 同时刷新两组计数器。
    private PdhQuery? _servicePdh;
    private IntPtr _cpuPerfCounter;
    private IntPtr _netRecvCounter;
    private IntPtr _netSentCounter;
    private readonly PdhArrayBuffer _netRecvBuffer = new();
    private readonly PdhArrayBuffer _netSentBuffer = new();
    private readonly List<(string Instance, double Value)> _netRecvItems = new();
    private readonly List<(string Instance, double Value)> _netSentItems = new();
    private readonly List<(string Name, double TotalBytesPerSec)> _netRates = new();
    private string? _netActiveAdapter;

    // CPU 计算差值缓存
    private long _prevCpuIdle;
    private long _prevCpuKernel;
    private long _prevCpuUser;
    private bool _hasCpuSnapshot;

    // 网络计算差值缓存（PDH 不可用时的 .NET IPv4 兜底）
    private long _prevBytesRecv;
    private long _prevBytesSent;
    private DateTime _prevNetTime = DateTime.MinValue;
    private NetworkInterface[] _netInterfaces = [];
    private DateTime _netInterfacesTime = DateTime.MinValue;

    private bool _disposed;
    private bool _enabled = false;
    private bool _useNvml;
    private bool _initialized;
    private readonly int _intervalMs;

    public HardwareMonitorService(int intervalMs = 1000)
    {
        _intervalMs = intervalMs;
        _timer = new System.Threading.Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _cpuBaseFreqGhz = QueryCpuBaseFrequency();
        _nvml.Initialize();
        _useNvml = _nvml.IsAvailable;
        if (!_useNvml)
        {
            _dxgiPdh.Initialize();
        }

        InitializeServicePdh();

        // 首次预采快照
        SnapshotCpu();
        SnapshotNet();
    }

    private void InitializeServicePdh()
    {
        var query = PdhQuery.TryOpen();
        if (query == null)
        {
            return;
        }

        _cpuPerfCounter = query.AddCounter(@"\Processor Information(_Total)\% Processor Performance");
        _netRecvCounter = query.AddCounter(@"\Network Interface(*)\Bytes Received/sec");
        _netSentCounter = query.AddCounter(@"\Network Interface(*)\Bytes Sent/sec");
        // 速率计数器需要两个原始样本才有格式化值，先埋第一个，100ms 后的首个 tick 补第二个
        query.Collect();
        _servicePdh = query;
    }

    /// <summary>
    /// 开关采样。悬浮窗关闭时完全停止轮询，避免常驻 CPU 与 PDH 采集开销。
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_disposed || _enabled == value)
            {
                return;
            }

            _enabled = value;
            if (value)
            {
                EnsureInitialized();
                // 重新启用时重置差值基线，避免把停机期间的时间算进使用率。
                SnapshotCpu();
                SnapshotNet();
                _servicePdh?.Collect();
                _timer.Change(100, _intervalMs);
            }
            else
            {
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }
    }

    private void OnTick(object? state)
    {
        if (_disposed || !_enabled) return;
        try
        {
            var metrics = Sample();
            MetricsUpdated?.Invoke(metrics);
        }
        catch (Exception ex)
        {
            Log.Error("HardwareMonitor sampling error: " + ex.Message);
        }
    }

    public HardwareMetrics Sample()
    {
        // 一次 Collect 同时刷新 CPU 频率与网卡速率计数器
        _servicePdh?.Collect();

        // 1. CPU
        float cpuUsage = SampleCpuUsage();
        float cpuGhz = SampleCpuFrequency();

        // 2. RAM
        var (ramUsage, ramUsed, ramTotal) = SampleRam();

        // 3. 网络
        var (downSpeed, upSpeed) = SampleNetSpeed();

        // 4. GPU (优先 NVML；若不可用则一次性切换到 DXGI+PDH，之后不再反复判断)
        // NVML 与 DXGI+PDH 都不可用时给显式的「不可用」快照：温度 -1 表示无数据，名称不能为 null。
        var gpu = _useNvml
            ? _nvml.Sample()
            : new NvmlAdapter.GpuSnapshot(0f, -1f, 0UL, 0UL, "GPU");
        if (!_useNvml && _dxgiPdh.IsAvailable)
        {
            var fallback = _dxgiPdh.Sample();
            gpu = new NvmlAdapter.GpuSnapshot(
                fallback.UsagePercent,
                -1f,
                fallback.MemoryUsedBytes,
                fallback.MemoryTotalBytes,
                fallback.Name);
        }

        float frameRate = _frameRate.Sample();

        return new HardwareMetrics(
            GpuUsagePercent: gpu.UsagePercent,
            GpuTemperature: gpu.Temperature,
            GpuMemoryUsedBytes: gpu.MemoryUsedBytes,
            GpuMemoryTotalBytes: gpu.MemoryTotalBytes,
            GpuName: gpu.Name,
            CpuUsagePercent: cpuUsage,
            CpuFrequencyGhz: cpuGhz,
            RamUsagePercent: ramUsage,
            RamUsedBytes: ramUsed,
            RamTotalBytes: ramTotal,
            NetDownloadSpeedBytesPerSec: downSpeed,
            NetUploadSpeedBytesPerSec: upSpeed,
            FrameRate: frameRate,
            FrameRateMeasured: _frameRate.IsMeasured,
            RefreshRateHz: _frameRate.RefreshHz);
    }

    #region CPU 采样

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long lpIdleTime, out long lpKernelTime, out long lpUserTime);

    private void SnapshotCpu()
    {
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            _prevCpuIdle = idle;
            _prevCpuKernel = kernel;
            _prevCpuUser = user;
            _hasCpuSnapshot = true;
        }
    }

    private float SampleCpuUsage()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return 0f;
        }

        if (!_hasCpuSnapshot)
        {
            _prevCpuIdle = idle;
            _prevCpuKernel = kernel;
            _prevCpuUser = user;
            _hasCpuSnapshot = true;
            return 0f;
        }

        var idleDiff = idle - _prevCpuIdle;
        var kernelDiff = kernel - _prevCpuKernel;
        var userDiff = user - _prevCpuUser;

        _prevCpuIdle = idle;
        _prevCpuKernel = kernel;
        _prevCpuUser = user;

        var total = kernelDiff + userDiff;
        if (total <= 0) return 0f;

        var usage = (float)((total - idleDiff) * 100.0 / total);
        return Math.Clamp(usage, 0f, 100f);
    }

    /// <summary>
    /// 实时频率 = 基频 × % Processor Performance（任务管理器"速度"的同款口径，
    /// 大于基频即睿频）。读不到时返回 0，显示端会隐藏频率而不是编造数值。
    /// </summary>
    private float SampleCpuFrequency()
    {
        if (_cpuBaseFreqGhz <= 0f || _cpuPerfCounter == IntPtr.Zero)
        {
            return 0f;
        }

        if (PdhInterop.TryReadSingle(_cpuPerfCounter, out var pct) && pct > 0)
        {
            _lastValidGhz = HardwareMonitorLogic.ComputeGhz(_cpuBaseFreqGhz, pct);
        }

        // 计数器偶发无数据（两次 Collect 间隔太近）时沿用上一有效值，避免副值闪空
        return _lastValidGhz;
    }

    /// <summary>注册表 ~MHz 是 BIOS 上报的基频（本机 i5-12490F 为 2995MHz）；读不到返回 0，UI 隐藏频率。</summary>
    private static float QueryCpuBaseFrequency()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key != null)
            {
                var val = key.GetValue("~MHz");
                if (val is int mhz && mhz > 0)
                {
                    return (float)Math.Round(mhz / 1000.0, 2);
                }
            }
        }
        catch
        {
            // 忽略注册表异常
        }
        return 0f;
    }

    #endregion

    #region 内存采样

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;

        public static MEMORYSTATUSEX Create()
        {
            return new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private static (float percent, ulong used, ulong total) SampleRam()
    {
        var mem = MEMORYSTATUSEX.Create();
        if (GlobalMemoryStatusEx(ref mem))
        {
            var total = mem.ullTotalPhys;
            var avail = mem.ullAvailPhys;
            var used = total > avail ? total - avail : 0UL;
            var percent = total > 0 ? (float)(used * 100.0 / total) : (float)mem.dwMemoryLoad;
            return (percent, used, total);
        }
        return (0f, 0UL, 0UL);
    }

    #endregion

    #region 网络采样

    /// <summary>
    /// PDH Network Interface 计数器（IPv4+IPv6 全统计，任务管理器网卡页同源）。
    /// 只展示吞吐最大的一张网卡：VPN TUN / Hyper-V 虚拟网桥会让同一份流量计两次，求和会虚高一倍。
    /// </summary>
    private (long downSpeed, long upSpeed) SampleNetSpeed()
    {
        if (_netRecvCounter != IntPtr.Zero && _netSentCounter != IntPtr.Zero &&
            _netRecvBuffer.Read(_netRecvCounter, _netRecvItems) &&
            _netSentBuffer.Read(_netSentCounter, _netSentItems))
        {
            _netRates.Clear();
            foreach (var (name, recv) in _netRecvItems)
            {
                if (IsTunnelOrLoopbackNetInstance(name))
                {
                    continue;
                }

                var sent = 0.0;
                foreach (var (sentName, sentValue) in _netSentItems)
                {
                    if (string.Equals(sentName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        sent = sentValue;
                        break;
                    }
                }

                _netRates.Add((name, recv + sent));
            }

            var chosen = HardwareMonitorLogic.SelectNetAdapter(_netRates, _netActiveAdapter);
            if (chosen == null)
            {
                return (0, 0);
            }

            _netActiveAdapter = chosen;
            foreach (var (name, recv) in _netRecvItems)
            {
                if (!string.Equals(name, chosen, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                long down = (long)Math.Max(0, recv);
                long up = 0;
                foreach (var (sentName, sentValue) in _netSentItems)
                {
                    if (string.Equals(sentName, chosen, StringComparison.OrdinalIgnoreCase))
                    {
                        up = (long)Math.Max(0, sentValue);
                        break;
                    }
                }

                return (down, up);
            }

            return (0, 0);
        }

        return SampleNetSpeedFallback();
    }

    /// <summary>排除回环与隧道伪网卡：流量要么是主机内部通信，要么已在物理网卡上计过一次。</summary>
    private static bool IsTunnelOrLoopbackNetInstance(string name)
        => name.Contains("loopback", StringComparison.OrdinalIgnoreCase)
            || name.Contains("isatap", StringComparison.OrdinalIgnoreCase)
            || name.Contains("teredo", StringComparison.OrdinalIgnoreCase)
            || name.Contains("6to4", StringComparison.OrdinalIgnoreCase);

    private void SnapshotNet()
    {
        try
        {
            var (recv, sent) = ReadNetBytes();
            _prevBytesRecv = recv;
            _prevBytesSent = sent;
            _prevNetTime = DateTime.UtcNow;
        }
        catch
        {
            // 忽略初始网络采样异常
        }
    }

    /// <summary>
    /// 网卡列表变化极少，缓存 30 秒，避免每秒枚举一次所有适配器。
    /// </summary>
    private NetworkInterface[] NetInterfaces()
    {
        var now = DateTime.UtcNow;
        if (_netInterfaces.Length > 0 && (now - _netInterfacesTime).TotalSeconds < 30)
        {
            return _netInterfaces;
        }

        var list = new List<NetworkInterface>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            list.Add(ni);
        }

        _netInterfaces = list.ToArray();
        _netInterfacesTime = now;
        return _netInterfaces;
    }

    private (long recv, long sent) ReadNetBytes()
    {
        long recv = 0;
        long sent = 0;
        foreach (var ni in NetInterfaces())
        {
            var stats = ni.GetIPv4Statistics();
            recv += stats.BytesReceived;
            sent += stats.BytesSent;
        }

        return (recv, sent);
    }

    /// <summary>.NET 差值兜底（仅 PDH 打不开时使用；只有 IPv4 计数）。</summary>
    private (long downSpeed, long upSpeed) SampleNetSpeedFallback()
    {
        try
        {
            var (recv, sent) = ReadNetBytes();

            var now = DateTime.UtcNow;
            var elapsedSec = (now - _prevNetTime).TotalSeconds;
            if (elapsedSec <= 0.05)
            {
                return (0, 0);
            }

            var downDiff = Math.Max(0, recv - _prevBytesRecv);
            var upDiff = Math.Max(0, sent - _prevBytesSent);

            _prevBytesRecv = recv;
            _prevBytesSent = sent;
            _prevNetTime = now;

            var downSpeed = (long)(downDiff / elapsedSec);
            var upSpeed = (long)(upDiff / elapsedSec);
            return (downSpeed, upSpeed);
        }
        catch
        {
            return (0, 0);
        }
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
        _nvml.Dispose();
        _dxgiPdh.Dispose();
        _servicePdh?.Dispose();
        _netRecvBuffer.Dispose();
        _netSentBuffer.Dispose();
    }
}

/// <summary>
/// NVIDIA NVML 硬件监控适配器 (显卡利用率、温度、专有显存)
/// </summary>
internal sealed class NvmlAdapter : IDisposable
{
    public readonly record struct GpuSnapshot(
        float UsagePercent,
        float Temperature,
        ulong MemoryUsedBytes,
        ulong MemoryTotalBytes,
        string Name);

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    private delegate int NvmlInitDelegate();
    private delegate int NvmlShutdownDelegate();
    private delegate int NvmlDeviceGetCountDelegate(out uint deviceCount);
    private delegate int NvmlDeviceGetHandleByIndexDelegate(uint index, out IntPtr device);
    private delegate int NvmlDeviceGetNameDelegate(IntPtr device, StringBuilder name, uint length);
    private delegate int NvmlDeviceGetTemperatureDelegate(IntPtr device, int sensorType, out uint temp);
    private delegate int NvmlDeviceGetUtilizationRatesDelegate(IntPtr device, out NvmlUtilization rates);
    private delegate int NvmlDeviceGetMemoryInfoDelegate(IntPtr device, out NvmlMemory mem);

    private IntPtr _dllHandle = IntPtr.Zero;
    private IntPtr _deviceHandle = IntPtr.Zero;
    private bool _initialized;
    private string _gpuName = "GPU";

    private NvmlShutdownDelegate? _shutdown;
    private NvmlDeviceGetTemperatureDelegate? _getTemperature;
    private NvmlDeviceGetUtilizationRatesDelegate? _getUtilizationRates;
    private NvmlDeviceGetMemoryInfoDelegate? _getMemoryInfo;

    public bool IsAvailable => _initialized && _deviceHandle != IntPtr.Zero;

    public void Initialize()
    {
        try
        {
            if (!NativeLibrary.TryLoad("nvml.dll", out _dllHandle))
            {
                return;
            }

            var initPtr = NativeLibrary.GetExport(_dllHandle, "nvmlInit_v2");
            var init = Marshal.GetDelegateForFunctionPointer<NvmlInitDelegate>(initPtr);
            if (init() != 0)
            {
                // nvmlInit 失败：立即释放已加载的库，避免句柄泄漏（此时无需 shutdown）。
                Cleanup();
                return;
            }

            _shutdown = Marshal.GetDelegateForFunctionPointer<NvmlShutdownDelegate>(NativeLibrary.GetExport(_dllHandle, "nvmlShutdown"));
            var getCount = Marshal.GetDelegateForFunctionPointer<NvmlDeviceGetCountDelegate>(NativeLibrary.GetExport(_dllHandle, "nvmlDeviceGetCount_v2"));
            var getHandle = Marshal.GetDelegateForFunctionPointer<NvmlDeviceGetHandleByIndexDelegate>(NativeLibrary.GetExport(_dllHandle, "nvmlDeviceGetHandleByIndex_v2"));
            var getName = Marshal.GetDelegateForFunctionPointer<NvmlDeviceGetNameDelegate>(NativeLibrary.GetExport(_dllHandle, "nvmlDeviceGetName"));

            _getTemperature = Marshal.GetDelegateForFunctionPointer<NvmlDeviceGetTemperatureDelegate>(NativeLibrary.GetExport(_dllHandle, "nvmlDeviceGetTemperature"));
            _getUtilizationRates = Marshal.GetDelegateForFunctionPointer<NvmlDeviceGetUtilizationRatesDelegate>(NativeLibrary.GetExport(_dllHandle, "nvmlDeviceGetUtilizationRates"));
            _getMemoryInfo = Marshal.GetDelegateForFunctionPointer<NvmlDeviceGetMemoryInfoDelegate>(NativeLibrary.GetExport(_dllHandle, "nvmlDeviceGetMemoryInfo"));

            if (getCount(out var count) == 0 && count > 0)
            {
                if (getHandle(0, out _deviceHandle) == 0 && _deviceHandle != IntPtr.Zero)
                {
                    var sb = new StringBuilder(64);
                    if (getName(_deviceHandle, sb, 64) == 0)
                    {
                        _gpuName = sb.ToString();
                    }
                    _initialized = true;
                    Log.Info($"NVML initialized: {_gpuName}");
                }
            }

            if (!_initialized)
            {
                // 拿不到设备（无 N 卡 / 枚举失败）：nvmlInit 已经生效，必须 shutdown 并释放库。
                Cleanup();
            }
        }
        catch (Exception ex)
        {
            Log.Error("NVML init failed: " + ex.Message);
            // 初始化中途抛异常同样可能已 nvmlInit 成功：清理要幂等（见 Cleanup）。
            Cleanup();
        }
    }

    /// <summary>关闭 NVML 并释放库句柄；幂等，可安全重复调用（库已释放时不再触碰任何函数指针）。</summary>
    private void Cleanup()
    {
        _initialized = false;
        _deviceHandle = IntPtr.Zero;
        if (_dllHandle != IntPtr.Zero)
        {
            try { _shutdown?.Invoke(); } catch { }
            try { NativeLibrary.Free(_dllHandle); } catch { }
            _dllHandle = IntPtr.Zero;
        }
    }

    public GpuSnapshot Sample()
    {
        if (!_initialized || _deviceHandle == IntPtr.Zero)
        {
            return new GpuSnapshot(0f, -1f, 0UL, 0UL, "GPU");
        }

        try
        {
            float utilPercent = 0f;
            if (_getUtilizationRates != null && _getUtilizationRates(_deviceHandle, out var util) == 0)
            {
                utilPercent = util.Gpu;
            }

            float temp = -1f;
            if (_getTemperature != null && _getTemperature(_deviceHandle, 0, out var t) == 0)
            {
                temp = t;
            }

            ulong memUsed = 0;
            ulong memTotal = 0;
            if (_getMemoryInfo != null && _getMemoryInfo(_deviceHandle, out var mem) == 0)
            {
                memUsed = mem.Used;
                memTotal = mem.Total;
            }

            return new GpuSnapshot(utilPercent, temp, memUsed, memTotal, _gpuName);
        }
        catch
        {
            return new GpuSnapshot(0f, -1f, 0UL, 0UL, _gpuName);
        }
    }

    public void Dispose()
    {
        // 无论 _initialized 与否都尝试 shutdown：初始化中途失败时 nvmlInit 可能已经生效；
        // shutdown 与 Free 都幂等，重复调用只会得到错误码，不影响正确性。
        Cleanup();
    }
}

/// <summary>
/// 全品牌显卡通用监视适配器（DXGI 1.1 选卡 + PDH GPU 计数器取数，支持 AMD / Intel / NVIDIA）。
/// 口径与任务管理器对齐：
/// - 利用率 = 目标适配器内按引擎类型（3D/Copy/VideoDecode/…）求和、跨类型取最大；
///   引擎实例按适配器 LUID 过滤，核显+独显双卡各算各的，绝不相加。
/// - 显存 = 目标适配器的 Dedicated Usage；核显（UMA 无专有显存）回退计入 Shared Usage。
/// - 双显卡默认显示显存最大的独显，另一张卡平滑利用率持续高出 3 个百分点才切换
///   （Optimus 轻薄本日常负载全在核显的场景跟着真实负载走）。
/// </summary>
internal sealed class DxgiPdhGpuAdapter : IDisposable
{
    public readonly record struct GpuSnapshot(
        float UsagePercent,
        float Temperature,
        ulong MemoryUsedBytes,
        ulong MemoryTotalBytes,
        string Name);

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1Delegate(IntPtr thisPtr, uint adapterIndex, out IntPtr ppAdapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1Delegate(IntPtr thisPtr, out DXGI_ADAPTER_DESC1 pDesc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseDelegate(IntPtr thisPtr);

    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    /// <summary>UMA 判定阈值：专有显存低于它的适配器（核显/虚拟卡）显存占用计入 Shared Usage。</summary>
    private const ulong MinDedicatedVramBytes = 64UL * 1024 * 1024;

    private const float UsageSmoothFactor = 0.35f;

    private sealed record GpuAdapterInfo(ulong Luid, string Name, ulong DedicatedVram);

    private readonly List<GpuAdapterInfo> _adapters = new();
    private ulong _primaryLuid;
    private string _gpuName = "GPU";
    private ulong _totalVram;
    private PdhQuery? _pdhQuery;
    private IntPtr _engineCounter;
    private IntPtr _dedCounter;
    private IntPtr _sharedCounter;
    private readonly PdhArrayBuffer _engineBuffer = new();
    private readonly PdhArrayBuffer _dedBuffer = new();
    private readonly PdhArrayBuffer _sharedBuffer = new();
    private readonly List<(string Instance, double Value)> _engineItems = new();
    private readonly List<(string Instance, double Value)> _dedItems = new();
    private readonly List<(string Instance, double Value)> _sharedItems = new();
    private readonly Dictionary<ulong, float> _usageEwma = new();
    private bool _initialized;
    private readonly object _lock = new();

    public bool IsAvailable => _initialized;
    public string GpuName => _gpuName;
    public ulong TotalVramBytes => _totalVram;

    public void Initialize()
    {
        lock (_lock)
        {
            if (_initialized) return;
            try
            {
                ProbeDxgiAdapter();
                InitPdh();
                _initialized = true;
                Log.Info($"DXGI+PDH GPU monitor initialized: {_gpuName} (VRAM: {_totalVram / (1024 * 1024)}MB, adapters: {_adapters.Count})");
            }
            catch (Exception ex)
            {
                Log.Error("DXGI+PDH init failed: " + ex.Message);
            }
        }
    }

    private void ProbeDxgiAdapter()
    {
        var guid = IID_IDXGIFactory1;
        if (CreateDXGIFactory1(ref guid, out var factory) != 0 || factory == IntPtr.Zero) return;

        try
        {
            IntPtr factoryVtbl = Marshal.ReadIntPtr(factory);
            var enumAdapters1 = Marshal.GetDelegateForFunctionPointer<EnumAdapters1Delegate>(Marshal.ReadIntPtr(factoryVtbl, 12 * IntPtr.Size));

            for (uint i = 0; ; i++)
            {
                if (enumAdapters1(factory, i, out var adapter) != 0 || adapter == IntPtr.Zero) break;

                try
                {
                    IntPtr adapterVtbl = Marshal.ReadIntPtr(adapter);
                    var getDesc1 = Marshal.GetDelegateForFunctionPointer<GetDesc1Delegate>(Marshal.ReadIntPtr(adapterVtbl, 10 * IntPtr.Size));
                    if (getDesc1(adapter, out var desc) == 0 && (desc.Flags & 2) == 0) // 排除软件渲染器
                    {
                        var luid = unchecked((ulong)desc.AdapterLuid);
                        var name = string.IsNullOrWhiteSpace(desc.Description) ? "GPU" : desc.Description.Trim();
                        _adapters.Add(new GpuAdapterInfo(luid, name, (ulong)desc.DedicatedVideoMemory));
                    }
                }
                finally
                {
                    IntPtr adapterVtbl = Marshal.ReadIntPtr(adapter);
                    var rel = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(Marshal.ReadIntPtr(adapterVtbl, 2 * IntPtr.Size));
                    rel(adapter);
                }
            }

            // 主适配器 = 专有显存最大者（首个并列最大）；全部无专有显存时取第一张，至少保住名称。
            var primary = _adapters.Count == 0 ? null : _adapters[0];
            foreach (var adapter in _adapters)
            {
                if (primary == null || adapter.DedicatedVram > primary.DedicatedVram)
                {
                    primary = adapter;
                }
            }

            if (primary != null)
            {
                _primaryLuid = primary.Luid;
                _gpuName = primary.Name;
                _totalVram = primary.DedicatedVram;
            }
        }
        finally
        {
            IntPtr factoryVtbl = Marshal.ReadIntPtr(factory);
            var rel = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(Marshal.ReadIntPtr(factoryVtbl, 2 * IntPtr.Size));
            rel(factory);
        }
    }

    private void InitPdh()
    {
        try
        {
            var query = PdhQuery.TryOpen();
            if (query == null)
            {
                return;
            }

            _pdhQuery = query;
            _engineCounter = query.AddCounter(@"\GPU Engine(*)\Utilization Percentage");
            _dedCounter = query.AddCounter(@"\GPU Adapter Memory(*)\Dedicated Usage");
            _sharedCounter = query.AddCounter(@"\GPU Adapter Memory(*)\Shared Usage");
            query.Collect();
        }
        catch
        {
            // PDH 初始化异常容错
        }
    }

    public GpuSnapshot Sample()
    {
        lock (_lock)
        {
            if (!_initialized || _adapters.Count == 0)
            {
                return new GpuSnapshot(0f, -1f, 0UL, 0UL, _gpuName);
            }

            if (_pdhQuery?.Collect() != true)
            {
                return new GpuSnapshot(0f, -1f, 0UL, _totalVram, _gpuName);
            }

            Dictionary<ulong, float>? usageByLuid = null;
            if (_engineBuffer.Read(_engineCounter, _engineItems))
            {
                usageByLuid = HardwareMonitorLogic.GpuUsageByAdapter(_engineItems);
            }

            UpdateUsageEwma(usageByLuid);
            // 显示值用原始瞬时利用率（UI 层已有指数平滑），EWMA 只用于选卡的防抖
            var chosenLuid = HardwareMonitorLogic.SelectGpuAdapter(_primaryLuid, _usageEwma);
            var chosen = AdapterByLuid(chosenLuid) ?? AdapterByLuid(_primaryLuid);
            if (chosen == null)
            {
                return new GpuSnapshot(0f, -1f, 0UL, _totalVram, _gpuName);
            }

            var usage = usageByLuid?.GetValueOrDefault(chosen.Luid, 0f) ?? 0f;

            double usedBytes = 0;
            if (_dedBuffer.Read(_dedCounter, _dedItems))
            {
                usedBytes += HardwareMonitorLogic.GpuMemoryForAdapter(_dedItems, chosen.Luid);
            }

            // 核显（UMA）：没有专有显存，Shared Usage（系统内存划拨）才是真实占用
            if (chosen.DedicatedVram < MinDedicatedVramBytes && _sharedBuffer.Read(_sharedCounter, _sharedItems))
            {
                usedBytes += HardwareMonitorLogic.GpuMemoryForAdapter(_sharedItems, chosen.Luid);
            }

            return new GpuSnapshot(usage, -1f, (ulong)Math.Max(0, usedBytes), chosen.DedicatedVram, chosen.Name);
        }
    }

    private GpuAdapterInfo? AdapterByLuid(ulong luid)
    {
        foreach (var adapter in _adapters)
        {
            if (adapter.Luid == luid)
            {
                return adapter;
            }
        }

        return null;
    }

    /// <summary>
    /// 平滑各适配器利用率：GPU Engine 实例按进程存在，进程退出实例即消失，
    /// 缺席的适配器目标按 0 衰减，避免选卡被残留的旧值钉死。
    /// </summary>
    private void UpdateUsageEwma(Dictionary<ulong, float>? raw)
    {
        foreach (var luid in _usageEwma.Keys.ToArray())
        {
            var target = raw != null && raw.TryGetValue(luid, out var value) ? value : 0f;
            _usageEwma[luid] = target * UsageSmoothFactor + _usageEwma[luid] * (1f - UsageSmoothFactor);
        }

        if (raw == null)
        {
            return;
        }

        foreach (var (luid, value) in raw)
        {
            if (!_usageEwma.ContainsKey(luid))
            {
                _usageEwma[luid] = value * UsageSmoothFactor;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _pdhQuery?.Dispose();
            _pdhQuery = null;
            _engineBuffer.Dispose();
            _dedBuffer.Dispose();
            _sharedBuffer.Dispose();
            _initialized = false;
        }
    }
}
