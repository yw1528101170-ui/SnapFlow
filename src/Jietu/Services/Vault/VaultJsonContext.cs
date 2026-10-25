using System.Text.Json.Serialization;

namespace Jietu;

/// <summary>
/// System.Text.Json 源生成上下文（序列化/反序列化走生成的代码，不用反射）。
///
/// 三个必须保持的设置：
/// ① camelCase —— 既有库文件（v1 载荷）里的字段名就是 camelCase，改策略等于让老备份读不出来；
/// ② 不忽略 null —— <c>deletedUtc</c> / <c>criticalOverride</c> 的 null 是有语义的（"未删除/自动判定"），
///    忽略掉就变成"字段缺失"，虽然 Normalize 能兜底，但会让文件在不同版本间来回抖动；
/// ③ 未知字段忽略（STJ 默认）—— 绝不开启 Disallow，否则未来版本加的字段会让本版本直接拒读。
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = false,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(VaultData))]
[JsonSerializable(typeof(VaultEntry))]
[JsonSerializable(typeof(VaultEntryVersion))]
[JsonSerializable(typeof(VaultHealthIgnore))]
[JsonSerializable(typeof(VaultTrustedBrowser))]
internal sealed partial class VaultJsonContext : JsonSerializerContext
{
}
