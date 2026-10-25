namespace Jietu.Tests;

/// <summary>
/// OSD 硬件指标采集的纯计算逻辑。锁的都是"错了不会报错、只会悄悄算错数"的行为：
/// LUID 解析错一位就把引擎利率记到错误的显卡上；跨引擎类型求和会让 Copy+3D 并行直接爆表；
/// 全网卡求和在 VPN TUN 场景虚高一倍；基频未知时编造频率是假数据。
/// </summary>
public sealed class HardwareMonitorLogicTests
{
    // ---------------- LUID 解析 ----------------

    [Theory]
    [InlineData("pid_10208_luid_0x00000000_0x00010437_phys_0_eng_0_engtype_3D", 0x10437UL)]
    [InlineData("luid_0x00000000_0x00012D46_phys_0", 0x12D46UL)]
    [InlineData("pid_1_luid_0x00000001_0x000000FF_phys_0", 0x1_000000FFUL)]
    [InlineData("pid_1_luid_0x0000ABCD_0xFFFFFFFF_phys_0", 0xABCD_FFFFFFFFUL)]
    public void TryGetLuid_ParsesRealInstanceNameFormats(string instance, ulong expected)
    {
        Assert.True(HardwareMonitorLogic.TryGetLuid(instance, out var luid));
        Assert.Equal(expected, luid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("pid_123_phys_0_engtype_3D")]
    [InlineData("luid_0x0000000Z_0x00010437")]
    [InlineData("luid_10437")]
    [InlineData("luid_0x123456789_0x00010437")] // 超过 8 位十六进制，不是合法 LUID 段
    public void TryGetLuid_RejectsMalformedInstances(string instance)
        => Assert.False(HardwareMonitorLogic.TryGetLuid(instance, out _));

    // ---------------- GPU 引擎聚合 ----------------

    [Fact]
    public void GpuUsageByAdapter_SumsWithinEngineType_TakesMaxAcrossTypes()
    {
        var items = new List<(string Instance, double Value)>
        {
            ("pid_1_luid_0x00000000_0x0000A_phys_0_eng_0_engtype_3D", 20),
            ("pid_2_luid_0x00000000_0x0000A_phys_0_eng_1_engtype_3D", 10),
            ("pid_1_luid_0x00000000_0x0000A_phys_0_eng_2_engtype_Copy", 50),
            ("pid_1_luid_0x00000000_0x0000B_phys_0_eng_0_engtype_3D", 35),
        };

        var usage = HardwareMonitorLogic.GpuUsageByAdapter(items);

        // A 适配器：3D=30、Copy=50 → 取 50（任务管理器口径），绝不能求和成 80
        Assert.Equal(50f, usage[0xA], 1);
        Assert.Equal(35f, usage[0xB], 1);
    }

    [Fact]
    public void GpuUsageByAdapter_ClampsTo100()
    {
        var items = new List<(string Instance, double Value)>
        {
            ("pid_1_luid_0x00000000_0x0000A_phys_0_eng_0_engtype_3D", 60),
            ("pid_2_luid_0x00000000_0x0000A_phys_0_eng_1_engtype_3D", 60),
            ("pid_3_luid_0x00000000_0x0000A_phys_0_eng_2_engtype_3D", 60),
        };

        var usage = HardwareMonitorLogic.GpuUsageByAdapter(items);

        Assert.Equal(100f, usage[0xA], 1);
    }

    [Fact]
    public void GpuUsageByAdapter_MultiAdapterNeverSumsAcrossGpus()
    {
        // 核显 + 独显同时有 3D 负载：各算各的，整机利用率绝不相加
        var items = new List<(string Instance, double Value)>
        {
            ("pid_1_luid_0x00000000_0x0000A_phys_0_eng_0_engtype_3D", 80),
            ("pid_1_luid_0x00000000_0x0000B_phys_0_eng_0_engtype_3D", 40),
        };

        var usage = HardwareMonitorLogic.GpuUsageByAdapter(items);

        Assert.Equal(80f, usage[0xA], 1);
        Assert.Equal(40f, usage[0xB], 1);
        Assert.Equal(2, usage.Count);
    }

    [Fact]
    public void GpuUsageByAdapter_MissingEngtypeFallsBackTo3D()
    {
        // 旧系统引擎实例无 engtype 段：归入 3D 桶而不是丢掉
        var items = new List<(string Instance, double Value)>
        {
            ("pid_1_luid_0x00000000_0x0000A_phys_0_eng_0", 10),
            ("pid_1_luid_0x00000000_0x0000A_phys_0_eng_1_engtype_3D", 25),
        };

        var usage = HardwareMonitorLogic.GpuUsageByAdapter(items);

        Assert.Equal(35f, usage[0xA], 1);
    }

    [Fact]
    public void EngineTypeOf_KeepsUnderscoredTypeNames()
        => Assert.Equal("Security_1", HardwareMonitorLogic.EngineTypeOf("pid_1_luid_0x0_0x1_phys_0_eng_13_engtype_Security_1"));

    [Fact]
    public void GpuMemoryForAdapter_MatchesLuidOnly()
    {
        var items = new List<(string Instance, double Value)>
        {
            ("luid_0x00000000_0x0000A_phys_0", 1000),
            ("luid_0x00000000_0x0000B_phys_0", 222),
        };

        Assert.Equal(1000, HardwareMonitorLogic.GpuMemoryForAdapter(items, 0xA), 1);
        Assert.Equal(0, HardwareMonitorLogic.GpuMemoryForAdapter(items, 0xC), 1);
    }

    // ---------------- 网卡选择 ----------------

    [Fact]
    public void SelectNetAdapter_PicksBusiest()
    {
        var rates = new List<(string Name, double TotalBytesPerSec)> { ("realtek", 1000), ("veth", 600) };

        Assert.Equal("realtek", HardwareMonitorLogic.SelectNetAdapter(rates, previous: null));
    }

    [Fact]
    public void SelectNetAdapter_SticksToPreviousWithin60Percent()
    {
        // VPN TUN 与物理网卡成对出现、速率接近：保持上一选择，不逐秒来回跳
        var rates = new List<(string Name, double TotalBytesPerSec)> { ("realtek", 1000), ("tun", 600) };

        Assert.Equal("tun", HardwareMonitorLogic.SelectNetAdapter(rates, previous: "tun"));
    }

    [Fact]
    public void SelectNetAdapter_SwitchesWhenPreviousFallsBehind()
    {
        var rates = new List<(string Name, double TotalBytesPerSec)> { ("realtek", 1000), ("tun", 500) };

        Assert.Equal("realtek", HardwareMonitorLogic.SelectNetAdapter(rates, previous: "tun"));
    }

    [Fact]
    public void SelectNetAdapter_EmptyListKeepsPrevious()
        => Assert.Null(HardwareMonitorLogic.SelectNetAdapter([], previous: null));

    // ---------------- 双显卡选择 ----------------

    [Fact]
    public void SelectGpuAdapter_KeepsPrimaryWhenIdleOrClose()
    {
        // 核显略忙但差距不足 3 个百分点：保持独显
        var usage = new Dictionary<ulong, float> { [0xA] = 10f, [0xB] = 12f };

        Assert.Equal(0xAUL, HardwareMonitorLogic.SelectGpuAdapter(0xA, usage));
    }

    [Fact]
    public void SelectGpuAdapter_SwitchesWhenIGpuClearlyBusier()
    {
        // 轻薄本日常负载全在核显、独显休眠：跟着真实负载走
        var usage = new Dictionary<ulong, float> { [0xA] = 2f, [0xB] = 40f };

        Assert.Equal(0xBUL, HardwareMonitorLogic.SelectGpuAdapter(0xA, usage));
    }

    [Fact]
    public void SelectGpuAdapter_EmptyUsageKeepsPrimary()
        => Assert.Equal(0xAUL, HardwareMonitorLogic.SelectGpuAdapter(0xA, new Dictionary<ulong, float>()));

    // ---------------- CPU 实时频率 ----------------

    [Theory]
    [InlineData(3.00f, 118, 3.54f)]  // 睿频：基频 3.0GHz × 118%
    [InlineData(3.00f, 100, 3.00f)]
    [InlineData(3.00f, 43, 1.29f)]   // 轻载降频
    [InlineData(2.99f, 150, 4.49f)]  // 本机注册表 2995MHz 场景的满睿频
    [InlineData(0f, 118, 0f)]        // 基频未知：返回 0 让 UI 隐藏，不编造
    [InlineData(3.00f, 0, 0f)]
    public void ComputeGhz_MatchesTaskManagerSemantics(float baseGhz, double percent, float expected)
        => Assert.Equal(expected, HardwareMonitorLogic.ComputeGhz(baseGhz, percent), 2);
}
