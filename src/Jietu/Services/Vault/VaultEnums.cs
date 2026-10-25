namespace Jietu;

/// <summary>
/// 库文件的字节层分类。判别只看文件字节本身（magic + formatVersion + 结构），
/// 不看路径、不看长度推断、不做任何部分解密——这是 v1 与 v2 的唯一分派入口。
/// 纪律：只能追加，不能改序（日志与测试都锚定枚举值）。
/// </summary>
public enum VaultFileKind
{
    /// <summary>文件不存在。</summary>
    Missing,

    /// <summary>不是 SnapFlow 库文件（magic 不符 / 版本为 0 等）。</summary>
    Unknown,

    /// <summary>formatVersion == 1：旧版容器，本版本一律阻断且零写盘。</summary>
    LegacyV1,

    /// <summary>formatVersion == 2 且结构合法。</summary>
    V2,

    /// <summary>formatVersion &gt; 2：更新的版本创建的文件。</summary>
    NewerVersion,

    /// <summary>magic/版本对得上，但头部结构、参数或长度不合法。</summary>
    StructurallyInvalid,
}

/// <summary>
/// 解锁结果状态。枚举即契约：UI 文案、桥协议 reason、日志都从这里派生。
/// 注意 <see cref="AuthenticationFailed"/> 与 <see cref="StructureInvalid"/> 必须严格区分：
/// 前者可能只是输错密码（不备份、不阻断），后者是不可解析的字节垃圾（可备份、要阻断）。
/// </summary>
public enum VaultOpenStatus
{
    Ok,

    /// <summary>tag 校验失败：密码错，或头/体被篡改。密码学上不可区分，也不该区分。</summary>
    AuthenticationFailed,

    /// <summary>头部结构/参数/长度不合法（含未知算法与未知 flags）。</summary>
    StructureInvalid,

    /// <summary>明文载荷的 schemaVersion 高于本版本支持的上限。</summary>
    PayloadTooNew,

    /// <summary>formatVersion == 1。</summary>
    LegacyFormat,

    /// <summary>formatVersion &gt; 2。</summary>
    NewerFormat,

    /// <summary>magic 不匹配 / 版本为 0 等。</summary>
    UnknownFormat,

    /// <summary>读取文件时发生 IO 异常。</summary>
    IoFailed,
}

/// <summary>阻断态的种类（决定给用户看哪张卡、给扩展回哪个 reason）。</summary>
public enum VaultBlockKind
{
    None,

    /// <summary>结构不合法或 tag 校验反复失败后的完整性阻断。</summary>
    IntegrityFailure,

    /// <summary>更高版本创建的文件。</summary>
    NewerVersion,

    /// <summary>不是我们的文件，或版本号无法识别。</summary>
    UnknownFile,
}

/// <summary>
/// 会话状态。<see cref="Locked"/> 与 <see cref="Blocked"/> 的区别是"能不能输入密码"：
/// 阻断态下密码输入没有意义（文件根本不是本版本能读的格式），但仍必须给出路径与恢复路径。
/// </summary>
public enum VaultState
{
    /// <summary>本机还没有库文件，UI 走创建流程。</summary>
    Missing,

    /// <summary>库存在且可解锁。</summary>
    Locked,

    /// <summary>已解锁，持有密钥与明文。</summary>
    Unlocked,

    /// <summary>库存在但不可解锁（旧版/过新/结构损坏），需用户先移走或恢复备份。</summary>
    Blocked,
}

/// <summary>
/// 阻断信息：给 UI 与桥协议共用。<see cref="FileBytes"/> 只用于日志与"文件没被动过"的
/// 用户提示（字节数不变是零写盘的可观测证据之一），不含任何明文。
/// </summary>
public sealed record VaultBlockInfo(VaultBlockKind Kind, string Path, long FileBytes, string Message)
{
    public static readonly VaultBlockInfo None = new(VaultBlockKind.None, "", 0, "");

    public bool IsBlocked => Kind != VaultBlockKind.None;

    /// <summary>桥协议里的 reason 串（与 VaultBridgeErrors.VaultBlocked 搭配）。</summary>
    public string Reason => Kind switch
    {
        VaultBlockKind.NewerVersion => "newer-version",
        VaultBlockKind.IntegrityFailure => "integrity",
        VaultBlockKind.UnknownFile => "unknown-file",
        _ => "",
    };
}

/// <summary>
/// 解锁尝试的结果。UI 需要三件事：成功与否、失败属于哪一类（决定文案层级与是否保留输入）、
/// 以及是否进入了阻断态（决定是否显示阻断卡而不是输入区）。
/// </summary>
public readonly record struct VaultUnlockOutcome(
    bool Ok,
    VaultOpenStatus Status,
    string? Title,
    string? Body,
    string? Hint,
    VaultBlockKind Blocked)
{
    public bool IsBlocked => Blocked != VaultBlockKind.None;

    public static VaultUnlockOutcome Success => new(true, VaultOpenStatus.Ok, null, null, null, VaultBlockKind.None);
}
