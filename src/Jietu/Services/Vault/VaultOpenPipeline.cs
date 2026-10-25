namespace Jietu;

/// <summary>
/// 解锁校验顺序的唯一实现（设计 2.4 的 11 步）。
///
/// **顺序本身就是安全措施，绝对不能重排**：
/// ① 版本分派、算法/flags/reserved、KDF 参数边界、长度一致性必须在 Argon2 之前完成，
///    否则一个 1GiB/16 迭代的畸形头就能把用户机器拖死（DoS 放大器）；
/// ② 长度校验必须在解密之前，否则"截断文件"会被伪装成"密码错误"，让用户反复试密码；
/// ③ 载荷 schemaVersion 检查必须在 Normalize 之前（先判能不能读，再决定怎么兜底）。
///
/// 为什么单独成类而不是塞进 <see cref="VaultSession"/>：会话需要 UI 线程的 DispatcherQueue，
/// 单测里造不出来；而"顺序"这件事必须有可失败的断言盯着，所以把它做成纯静态函数。
/// </summary>
public static class VaultOpenPipeline
{
    /// <summary>
    /// 第 1～7 步：把 <see cref="VaultFileRead"/> 的分类映射成可以**在不派生密钥的前提下**
    /// 下结论的结果。返回 null 表示"确实是一个结构合法的 v2 文件，可以进入 KDF"。
    /// </summary>
    public static VaultOpenResult? Precheck(VaultFileRead read)
    {
        if (read.IoFailed)
        {
            return VaultOpenResult.Fail(VaultOpenStatus.IoFailed, VaultStore.LastNotice);
        }

        switch (read.Kind)
        {
            case VaultFileKind.Missing:
                return VaultOpenResult.Fail(VaultOpenStatus.UnknownFormat, "库文件不存在");
            case VaultFileKind.LegacyV1:
                return VaultOpenResult.Fail(VaultOpenStatus.LegacyFormat, "formatVersion=1");
            case VaultFileKind.NewerVersion:
                return VaultOpenResult.Fail(VaultOpenStatus.NewerFormat, "formatVersion>2");
            case VaultFileKind.Unknown:
                return VaultOpenResult.Fail(VaultOpenStatus.UnknownFormat, "magic 不匹配或版本无法识别");
            case VaultFileKind.StructurallyInvalid:
                return VaultOpenResult.Fail(VaultOpenStatus.StructureInvalid, "头部结构/参数/长度不合法");
            case VaultFileKind.V2:
                break;
            default:
                return VaultOpenResult.Fail(VaultOpenStatus.UnknownFormat, "未分类");
        }

        // V2：Read 已经过完步骤 2～7，这里只做最后一道"字段齐不齐"的自检。
        if (read.Body == null || read.Header.Version != VaultFormat.CurrentFormatVersion)
        {
            return VaultOpenResult.Fail(VaultOpenStatus.StructureInvalid, "已分类为 v2 但缺少头/体");
        }

        return null;
    }

    /// <summary>
    /// 完整的 11 步解锁（口令路径）。派生的密钥在方法返回前清零，不交给调用方——
    /// 需要长期持有密钥的场景（会话）请自己走 Precheck + DeriveKey + <see cref="OpenWithKey"/>。
    /// </summary>
    public static VaultOpenResult Open(VaultFileRead read, ReadOnlySpan<byte> passwordUtf8)
    {
        var precheck = Precheck(read);
        if (precheck is { } early)
        {
            return early;
        }

        using var key = VaultCrypto.DeriveKey(passwordUtf8, read.Header);
        return OpenWithKey(read, key);
    }

    /// <summary>第 9～11 步：用现成密钥解密并解析（Hello 快速解锁路径也走这里）。</summary>
    public static VaultOpenResult OpenWithKey(VaultFileRead read, VaultKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var precheck = Precheck(read);
        if (precheck is { } early)
        {
            return early;
        }

        return VaultCrypto.Open(read.Header, key, read.Body!);
    }
}
