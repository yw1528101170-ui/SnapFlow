using System.Diagnostics;
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

    // CPU 计算差值缓存
    private long _prevCpuIdle;
    private long _prevCpuKernel;
    private long _prevCpuUser;
    private bool _hasCpuSnapshot;

    // 网络计算差值缓存
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

        // 首次预采快照
        SnapshotCpu();
        SnapshotNet();
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
        // 1. CPU
        float cpuUsage = SampleCpuUsage();

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
            CpuFrequencyGhz: _cpuBaseFreqGhz,
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
        return 3.60f; // 默认备选值
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

    private (long downSpeed, long upSpeed) SampleNetSpeed()
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
/// 全品牌显卡通用监视适配器 (基于 Windows 原生 DXGI 1.1+ 与 PDH 性能计数器，支持 AMD Radeon、Intel Arc/Iris/iGPU 及 NVIDIA)
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

    [DllImport("pdh.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQuery(IntPtr szDataSource, IntPtr dwUserData, out IntPtr phQuery);

    [DllImport("pdh.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounterW(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);

    [DllImport("pdh.dll", SetLastError = true)]
    private static extern int PdhCollectQueryData(IntPtr hQuery);

    [DllImport("pdh.dll", SetLastError = true)]
    private static extern int PdhCloseQuery(IntPtr hQuery);

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE
    {
        public uint CStatus;
        public double doubleValue;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public IntPtr szName;
        public PDH_FMT_COUNTERVALUE FmtValue;
    }

    [DllImport("pdh.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int PdhGetFormattedCounterArrayW(
        IntPtr hCounter,
        uint dwFormat,
        ref uint lpdwBufferSize,
        ref uint lpdwItemCount,
        IntPtr pItemBuffer);

    private string _gpuName = "GPU";
    private ulong _totalVram;
    private IntPtr _pdhQuery = IntPtr.Zero;
    private IntPtr _pdhEngineCounter = IntPtr.Zero;
    private IntPtr _pdhMemCounter = IntPtr.Zero;
    private bool _initialized;
    private readonly object _lock = new();

    // PDH 每次读取都要申请一块返回缓冲区，复用同一块内存可避免每秒的堆分配。
    private IntPtr _pdhBuffer = IntPtr.Zero;
    private uint _pdhBufferSize;

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
                Log.Info($"DXGI+PDH GPU monitor initialized: {_gpuName} (VRAM: {_totalVram / (1024 * 1024)}MB)");
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

            ulong maxVram = 0;
            string bestName = "GPU";

            for (uint i = 0; ; i++)
            {
                if (enumAdapters1(factory, i, out var adapter) != 0 || adapter == IntPtr.Zero) break;

                try
                {
                    IntPtr adapterVtbl = Marshal.ReadIntPtr(adapter);
                    var getDesc1 = Marshal.GetDelegateForFunctionPointer<GetDesc1Delegate>(Marshal.ReadIntPtr(adapterVtbl, 10 * IntPtr.Size));
                    if (getDesc1(adapter, out var desc) == 0 && (desc.Flags & 2) == 0) // 排除软件渲染器
                    {
                        ulong vram = (ulong)desc.DedicatedVideoMemory;
                        if (vram > maxVram || (maxVram == 0 && bestName == "GPU"))
                        {
                            maxVram = vram;
                            bestName = string.IsNullOrWhiteSpace(desc.Description) ? "GPU" : desc.Description.Trim();
                        }
                    }
                }
                finally
                {
                    IntPtr adapterVtbl = Marshal.ReadIntPtr(adapter);
                    var rel = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(Marshal.ReadIntPtr(adapterVtbl, 2 * IntPtr.Size));
                    rel(adapter);
                }
            }

            _gpuName = bestName;
            _totalVram = maxVram;
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
            if (PdhOpenQuery(IntPtr.Zero, IntPtr.Zero, out _pdhQuery) == 0 && _pdhQuery != IntPtr.Zero)
            {
                PdhAddEnglishCounterW(_pdhQuery, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out _pdhEngineCounter);
                PdhAddEnglishCounterW(_pdhQuery, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out _pdhMemCounter);
                PdhCollectQueryData(_pdhQuery);
            }
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
            if (!_initialized)
            {
                return new GpuSnapshot(0f, -1f, 0UL, _totalVram, _gpuName);
            }

            float utilPercent = 0f;
            ulong usedMemBytes = 0;

            if (_pdhQuery != IntPtr.Zero)
            {
                try
                {
                    if (PdhCollectQueryData(_pdhQuery) == 0)
                    {
                        if (_pdhEngineCounter != IntPtr.Zero)
                        {
                            utilPercent = SampleEngineUsage(_pdhEngineCounter);
                        }

                        if (_pdhMemCounter != IntPtr.Zero)
                        {
                            usedMemBytes = SampleDedicatedMemory(_pdhMemCounter);
                        }
                    }
                }
                catch
                {
                    // 计数器读取异常容错
                }
            }

            return new GpuSnapshot(utilPercent, -1f, usedMemBytes, _totalVram, _gpuName);
        }
    }

    private float SampleEngineUsage(IntPtr counter)
    {
        return (float)Math.Clamp(SampleCounter(counter, PdhAggregate.Sum, reuseBuffer: true), 0.0, 100.0);
    }

    private ulong SampleDedicatedMemory(IntPtr counter)
    {
        return (ulong)Math.Max(0, SampleCounter(counter, PdhAggregate.Max, reuseBuffer: false));
    }

    private enum PdhAggregate
    {
        Sum,
        Max,
    }

    /// <summary>
    /// 读取 PDH 计数器数组。引擎计数器每帧都读，缓冲区按需扩容后复用，避免每次采样都向堆申请内存。
    /// </summary>
    private double SampleCounter(IntPtr counter, PdhAggregate mode, bool reuseBuffer)
    {
        uint bufSize = 0, itemCount = 0;
        PdhGetFormattedCounterArrayW(counter, 0x00000200 /* PDH_FMT_DOUBLE */, ref bufSize, ref itemCount, IntPtr.Zero);
        if (bufSize == 0 || itemCount == 0) return 0;

        IntPtr buffer = IntPtr.Zero;
        bool owns = false;
        try
        {
            if (reuseBuffer)
            {
                if (_pdhBufferSize < bufSize)
                {
                    if (_pdhBuffer != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(_pdhBuffer);
                        _pdhBuffer = IntPtr.Zero;
                    }

                    _pdhBuffer = Marshal.AllocHGlobal((int)bufSize);
                    _pdhBufferSize = bufSize;
                }

                buffer = _pdhBuffer;
            }
            else
            {
                buffer = Marshal.AllocHGlobal((int)bufSize);
                owns = true;
            }

            if (PdhGetFormattedCounterArrayW(counter, 0x00000200, ref bufSize, ref itemCount, buffer) != 0)
            {
                return 0;
            }

            double acc = 0;
            int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
            for (int i = 0; i < itemCount; i++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(IntPtr.Add(buffer, i * itemSize));
                if (item.FmtValue.CStatus != 0 || item.FmtValue.doubleValue <= 0)
                {
                    continue;
                }

                if (mode == PdhAggregate.Sum)
                {
                    acc += item.FmtValue.doubleValue;
                }
                else if (item.FmtValue.doubleValue > acc)
                {
                    acc = item.FmtValue.doubleValue;
                }
            }

            return acc;
        }
        finally
        {
            if (owns && buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_pdhQuery != IntPtr.Zero)
            {
                try { PdhCloseQuery(_pdhQuery); } catch { }
                _pdhQuery = IntPtr.Zero;
            }

            if (_pdhBuffer != IntPtr.Zero)
            {
                try { Marshal.FreeHGlobal(_pdhBuffer); } catch { }
                _pdhBuffer = IntPtr.Zero;
                _pdhBufferSize = 0;
            }

            _initialized = false;
        }
    }
}
