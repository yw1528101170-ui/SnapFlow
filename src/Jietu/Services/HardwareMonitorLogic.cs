namespace Jietu;

/// <summary>
/// OSD 硬件指标的纯计算逻辑（不碰互操作，可单测）。
/// 口径对齐任务管理器：
/// - GPU 利用率 = 同一适配器内按引擎类型求和、跨类型取最大（任务管理器的总 GPU%）；
/// - CPU 实时频率 = 基频 × % Processor Performance（任务管理器的"速度"）。
/// </summary>
internal static class HardwareMonitorLogic
{
    /// <summary>
    /// 从 GPU Engine / GPU Adapter Memory 的实例名解析适配器 LUID。
    /// 实例名形如 pid_10208_luid_0x00000000_0x00010437_phys_0_eng_0_engtype_3D。
    /// </summary>
    public static bool TryGetLuid(string? instance, out ulong luid)
    {
        luid = 0;
        if (string.IsNullOrEmpty(instance))
        {
            return false;
        }

        var parts = instance.Split('_');
        for (int i = 0; i + 2 < parts.Length; i++)
        {
            if (parts[i] != "luid" || !TryHexPart(parts[i + 1], out var high) || !TryHexPart(parts[i + 2], out var low))
            {
                continue;
            }

            luid = (high << 32) | low;
            return true;
        }

        return false;
    }

    private static bool TryHexPart(string token, out ulong value)
    {
        value = 0;
        return token.Length is > 2 and <= 10
            && token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && TryHexDigits(token[2..], out value);
    }

    private static bool TryHexDigits(string digits, out ulong value)
    {
        value = 0;
        if (digits.Length == 0 || digits.Length > 8)
        {
            return false;
        }

        ulong acc = 0;
        foreach (var ch in digits)
        {
            var d = ch is >= '0' and <= '9' ? ch - '0'
                : ch is >= 'a' and <= 'f' ? ch - 'a' + 10
                : ch is >= 'A' and <= 'F' ? ch - 'A' + 10
                : -1;
            if (d < 0)
            {
                return false;
            }

            acc = acc * 16 + (ulong)d;
        }

        value = acc;
        return true;
    }

    /// <summary>取实例名的引擎类型（末段 engtype_X；类型名本身可含下划线，如 Security_1）。旧系统无 engtype 段时归入 3D。</summary>
    public static string EngineTypeOf(string instance)
    {
        var at = instance.LastIndexOf("engtype_", StringComparison.Ordinal);
        return at < 0 ? "3D" : instance[(at + "engtype_".Length)..];
    }

    /// <summary>
    /// 每适配器利用率（0..100）：同一 LUID 内同引擎类型求和（跨进程），跨类型取最大。
    /// 若错成跨类型求和，Copy 与 3D 并行时立刻爆表到 100%+；若错成跨适配器求和，
    /// 核显+独显整机利用率会被翻倍。
    /// </summary>
    public static Dictionary<ulong, float> GpuUsageByAdapter(IReadOnlyList<(string Instance, double Value)> engineItems)
    {
        var byType = new Dictionary<ulong, Dictionary<string, double>>();
        foreach (var (instance, value) in engineItems)
        {
            if (!TryGetLuid(instance, out var luid))
            {
                continue;
            }

            if (!byType.TryGetValue(luid, out var types))
            {
                types = new Dictionary<string, double>();
                byType[luid] = types;
            }

            var type = EngineTypeOf(instance);
            types[type] = types.GetValueOrDefault(type) + value;
        }

        var result = new Dictionary<ulong, float>(byType.Count);
        foreach (var (luid, types) in byType)
        {
            var max = 0.0;
            foreach (var (_, sum) in types)
            {
                if (sum > max)
                {
                    max = sum;
                }
            }

            result[luid] = (float)Math.Clamp(max, 0.0, 100.0);
        }

        return result;
    }

    /// <summary>从 GPU Adapter Memory 计数器项中挑出指定 LUID 的占用（字节）。</summary>
    public static double GpuMemoryForAdapter(IReadOnlyList<(string Instance, double Value)> items, ulong luid)
    {
        double sum = 0;
        foreach (var (instance, value) in items)
        {
            if (TryGetLuid(instance, out var itemLuid) && itemLuid == luid)
            {
                sum += value;
            }
        }

        return sum;
    }

    /// <summary>
    /// 选择展示的网卡：取吞吐最大的一张。TUN/虚拟网桥（VPN、Hyper-V）会让同一份流量
    /// 在两张网卡上各计一次，全网卡求和会虚高一倍，所以用最大值而非总和。
    /// 上一选择仍达到当前最优的 60% 时保持不变，避免速率接近的两张卡逐秒来回跳。
    /// </summary>
    public static string? SelectNetAdapter(IReadOnlyList<(string Name, double TotalBytesPerSec)> rates, string? previous)
    {
        if (rates.Count == 0)
        {
            return previous;
        }

        var best = rates[0];
        for (var i = 1; i < rates.Count; i++)
        {
            if (rates[i].TotalBytesPerSec > best.TotalBytesPerSec)
            {
                best = rates[i];
            }
        }

        if (previous != null)
        {
            foreach (var rate in rates)
            {
                if (string.Equals(rate.Name, previous, StringComparison.OrdinalIgnoreCase) &&
                    rate.TotalBytesPerSec >= best.TotalBytesPerSec * 0.6)
                {
                    return previous;
                }
            }
        }

        return best.Name;
    }

    /// <summary>
    /// 双显卡选择：默认显示显存最大的主适配器（独显）；仅当另一张适配器的平滑利用率
    /// 高出 3 个百分点以上时才切换——核显干活、独显休眠的轻薄本场景跟着真实负载走。
    /// </summary>
    public static ulong SelectGpuAdapter(ulong primaryLuid, IReadOnlyDictionary<ulong, float> smoothedUsage)
    {
        var primaryUsage = smoothedUsage.GetValueOrDefault(primaryLuid, 0f);
        ulong best = primaryLuid;
        var bestUsage = primaryUsage;
        foreach (var (luid, usage) in smoothedUsage)
        {
            if (usage > bestUsage)
            {
                best = luid;
                bestUsage = usage;
            }
        }

        return bestUsage > primaryUsage + 3f ? best : primaryLuid;
    }

    /// <summary>实时频率 = 基频 × % Processor Performance / 100（大于 100 表示已睿频）。基频未知返回 0，UI 隐藏频率。</summary>
    public static float ComputeGhz(float baseGhz, double percentProcessorPerformance)
        => baseGhz <= 0f || percentProcessorPerformance <= 0
            ? 0f
            : (float)Math.Round(baseGhz * percentProcessorPerformance / 100.0, 2);
}
