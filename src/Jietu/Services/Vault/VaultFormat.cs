using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Jietu;

/// <summary>
/// v2 库文件头（80 字节明文段）。字段不可变：换 nonce / 换密文长度都返回新实例，
/// 避免"改了一半的头"被写盘或参与 AAD 计算。
///
/// 敏感性：头是明文（任何拿到文件的人都能读），敏感信息全在 AES-GCM 密文里；
/// 头的职责有两个——让解密方知道怎么派生密钥，以及**整体作为 AAD 被认证**。
/// </summary>
public readonly struct VaultHeader
{
    /// <summary>容器格式版本（v2）。与载荷的 schemaVersion 是两个独立的东西。</summary>
    public int Version { get; init; }

    /// <summary>KDF 算法标识，1 = Argon2id（v1.3）。进 AAD，防"降级到弱算法"。</summary>
    public uint KdfAlgorithm { get; init; }

    /// <summary>AEAD 算法标识，1 = AES-256-GCM（12B nonce / 16B tag）。进 AAD，防算法降级。</summary>
    public uint AeadAlgorithm { get; init; }

    public int Iterations { get; init; }

    public int MemoryKiB { get; init; }

    public int Parallelism { get; init; }

    /// <summary>位标记。v2 必须为 0：非 0 表示"有本版本不认识的语义"，必须拒绝而不是忽略。</summary>
    public uint Flags { get; init; }

    /// <summary>预留槽（载荷压缩 / AEAD 算法族 / 容器族标识）。v2 必须为 0。</summary>
    public uint Reserved0 { get; init; }

    /// <summary>
    /// 密文体长度 = 明文字节数 + 16。冗余但值得：它让"截断/追加/伪造长度"在
    /// 派生密钥**之前**就能判成结构不合法，省掉半秒到两秒的 Argon2，
    /// 也避免用户为一个残缺文件反复试密码。它同时进 AAD，所以不可伪造。
    /// </summary>
    public uint CipherLength { get; init; }

    /// <summary>预留槽（条目数快照 / 写盘时间戳 / KDF 策略 id 各 4 字节）。v2 必须为 0。</summary>
    public ulong Reserved1 { get; init; }

    /// <summary>KDF 盐（16B）。只在"新建库 / 改主密码"时更换，同一解锁周期内不变。</summary>
    public byte[] Salt { get; init; }

    /// <summary>GCM nonce（12B）。每次写盘必须重新生成——同密钥下复用 nonce 会毁掉 GCM。</summary>
    public byte[] Nonce { get; init; }

    /// <summary>按当前 KDF 策略构造 v2 头（salt/nonce 随机，cipherLength 由写盘方补）。</summary>
    public static VaultHeader NewV2(int iterations, int memoryKiB, int parallelism)
    {
        return new VaultHeader
        {
            Version = VaultFormat.CurrentFormatVersion,
            KdfAlgorithm = VaultFormat.KdfArgon2id,
            AeadAlgorithm = VaultFormat.AeadAes256Gcm,
            Iterations = iterations,
            MemoryKiB = memoryKiB,
            Parallelism = parallelism,
            Flags = 0,
            Reserved0 = 0,
            CipherLength = 0,
            Reserved1 = 0,
            Salt = RandomNumberGenerator.GetBytes(VaultFormat.SaltLength),
            Nonce = RandomNumberGenerator.GetBytes(VaultFormat.NonceLength),
        };
    }

    /// <summary>换一个随机 nonce（每次写盘调用一次）。</summary>
    public VaultHeader WithFreshNonce()
        => this with { Nonce = RandomNumberGenerator.GetBytes(VaultFormat.NonceLength) };

    /// <summary>按明文长度补上 cipherLength（= 明文 + 16 tag）。</summary>
    public VaultHeader WithPlaintextLength(int plaintextLength)
    {
        if (plaintextLength < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(plaintextLength));
        }

        return this with { CipherLength = (uint)(plaintextLength + VaultFormat.TagLength) };
    }

    /// <summary>
    /// 序列化为写盘用的 80 字节。<see cref="VaultFormat.BuildAad"/> 必须使用本方法的输出
    /// （不允许"重新算一遍"），否则字段默认值差异会导致自己解不开自己的文件。
    /// </summary>
    public byte[] ToBytes()
    {
        if (Salt is not { Length: VaultFormat.SaltLength })
        {
            throw new InvalidOperationException("盐长度必须为 16 字节。");
        }

        if (Nonce is not { Length: VaultFormat.NonceLength })
        {
            throw new InvalidOperationException("nonce 长度必须为 12 字节。");
        }

        var bytes = new byte[VaultFormat.HeaderLength];

        // 0..8 magic
        VaultFormat.Magic.CopyTo(bytes);
        // 8..12 formatVersion
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), (uint)Version);
        // 12..16 kdfAlg
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), KdfAlgorithm);
        // 16..20 aeadAlg
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), AeadAlgorithm);
        // 20..24 iterations
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20, 4), Iterations);
        // 24..28 memoryKiB
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24, 4), MemoryKiB);
        // 28..32 parallelism
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28, 4), Parallelism);
        // 32..36 flags
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32, 4), Flags);
        // 36..40 reserved0
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(36, 4), Reserved0);
        // 40..56 salt
        Salt.CopyTo(bytes.AsSpan(40, VaultFormat.SaltLength));
        // 56..68 nonce
        Nonce.CopyTo(bytes.AsSpan(56, VaultFormat.NonceLength));
        // 68..72 cipherLength
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(68, 4), CipherLength);
        // 72..80 reserved1
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(72, 8), Reserved1);
        return bytes;
    }
}

/// <summary>
/// 容器格式规格与纯静态判定：常量、分类、头部解析、AAD 构造、KDF 边界。
/// 全部无 IO、无状态，任意线程可调用。
/// </summary>
public static class VaultFormat
{
    /// <summary>头部固定 80 字节：52（v1 全部字段）+ 4 kdfAlg + 4 aeadAlg + 4 flags
    /// + 4 reserved0 + 4 cipherLength + 8 reserved1。</summary>
    public const int HeaderLength = 80;

    public const int TagLength = 16;
    public const int NonceLength = 12;
    public const int SaltLength = 16;

    /// <summary>AAD = 16B 域分隔标签 + 1B NUL + 80B 头 = 97 字节定长。</summary>
    public const int AadLength = 16 + 1 + HeaderLength;

    public const int CurrentFormatVersion = 2;

    /// <summary>Argon2id（Argon2Version.Nineteen）。</summary>
    public const uint KdfArgon2id = 1;

    /// <summary>AES-256-GCM，12B nonce / 16B tag。</summary>
    public const uint AeadAes256Gcm = 1;

    /// <summary>KDF 结构边界：派生前校验，防"畸形参数变成 DoS 放大器"（上限与 v1 一致）。</summary>
    public const int MinMemoryKiB = 8 * 1024;

    public const int MaxMemoryKiB = 1024 * 1024;
    public const int MinIterations = 1;
    public const int MaxIterations = 16;
    public const int MinParallelism = 1;
    public const int MaxParallelism = 16;

    /// <summary>cipherLength 下限：至少 1 字节明文 + 16 字节 tag。</summary>
    public const int MinCipherLength = TagLength + 1;

    /// <summary>文件 magic：8 字节 ASCII，用于识别"这是不是一个 SnapFlow 库文件"。</summary>
    public static ReadOnlySpan<byte> Magic => "SPVAULT\0"u8;

    /// <summary>
    /// GCM 的域分隔标签（16 字节 ASCII）。它**不是** magic：magic 用来识别文件，
    /// 标签用来把"同一把密钥 + 同一套字节结构"的其它协议（hello.blob、未来 v3）
    /// 与我们区分开，避免跨协议互相验证通过。
    /// </summary>
    public static ReadOnlySpan<byte> AadLabel => "SnapFlowVault/v2"u8;

    /// <summary>
    /// 唯一的格式判别入口：只看字节本身，不看路径/长度推断、不做部分解密。
    /// </summary>
    public static VaultFileKind Classify(ReadOnlySpan<byte> raw)
    {
        if (raw.Length == 0)
        {
            return VaultFileKind.Unknown;
        }

        if (raw.Length < Magic.Length || !raw[..Magic.Length].SequenceEqual(Magic))
        {
            return VaultFileKind.Unknown;
        }

        if (raw.Length < 12)
        {
            // magic 对得上但读不出完整版本号：这是被截断的自家文件。
            return VaultFileKind.StructurallyInvalid;
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(8, 4));
        return version switch
        {
            1 => VaultFileKind.LegacyV1,
            2 => TryReadHeader(raw, out _, out _) ? VaultFileKind.V2 : VaultFileKind.StructurallyInvalid,
            > 2 => VaultFileKind.NewerVersion,
            _ => VaultFileKind.Unknown,
        };
    }

    /// <summary>
    /// 解析 v2 头并做全部静态校验（第 3～7 步：magic → 版本分派 → 算法/flags/reserved
    /// → KDF 参数边界 → 长度一致性）。返回 true 时 <paramref name="status"/> 为 Ok。
    ///
    /// 顺序本身就是安全措施：任何一步失败都必须在 Argon2 派生之前返回。
    /// </summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> raw, out VaultHeader header, out VaultOpenStatus status)
    {
        header = default;

        // 步骤 2：长度够不够读一个头。长度不足时不猜类型，只有 magic 明确不符才判 Unknown。
        if (raw.Length < HeaderLength)
        {
            status = raw.Length >= Magic.Length && !raw[..Magic.Length].SequenceEqual(Magic)
                ? VaultOpenStatus.UnknownFormat
                : VaultOpenStatus.StructureInvalid;
            return false;
        }

        // 步骤 3：magic。不是我们的文件 ⇒ 不能当"损坏"处理。
        if (!raw[..Magic.Length].SequenceEqual(Magic))
        {
            status = VaultOpenStatus.UnknownFormat;
            return false;
        }

        // 步骤 4：版本分派，必须在 Argon2 之前 —— 旧库/未来库都不该花半秒去试。
        var version = BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(8, 4));
        if (version == 1)
        {
            status = VaultOpenStatus.LegacyFormat;
            return false;
        }

        if (version > CurrentFormatVersion)
        {
            status = VaultOpenStatus.NewerFormat;
            return false;
        }

        if (version != CurrentFormatVersion)
        {
            status = VaultOpenStatus.UnknownFormat;
            return false;
        }

        // 步骤 5：算法标识 / flags / reserved。未知语义必须显式拒绝，不许静默忽略。
        var kdfAlg = BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(12, 4));
        var aeadAlg = BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(16, 4));
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(32, 4));
        var reserved0 = BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(36, 4));
        var reserved1 = BinaryPrimitives.ReadUInt64LittleEndian(raw.Slice(72, 8));
        if (kdfAlg != KdfArgon2id || aeadAlg != AeadAes256Gcm || flags != 0 || reserved0 != 0 || reserved1 != 0)
        {
            status = VaultOpenStatus.StructureInvalid;
            return false;
        }

        // 步骤 6：KDF 参数边界（含导入外来文件时的策略上限由调用方另行判定）。
        var iterations = BinaryPrimitives.ReadInt32LittleEndian(raw.Slice(20, 4));
        var memoryKiB = BinaryPrimitives.ReadInt32LittleEndian(raw.Slice(24, 4));
        var parallelism = BinaryPrimitives.ReadInt32LittleEndian(raw.Slice(28, 4));
        if (iterations is < MinIterations or > MaxIterations
            || memoryKiB is < MinMemoryKiB or > MaxMemoryKiB
            || parallelism is < MinParallelism or > MaxParallelism)
        {
            status = VaultOpenStatus.StructureInvalid;
            return false;
        }

        // 步骤 7：长度一致性。截断/追加/伪造长度在这里就变成结构不合法。
        var cipherLength = BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(68, 4));
        if (cipherLength < MinCipherLength || (long)raw.Length != HeaderLength + (long)cipherLength)
        {
            status = VaultOpenStatus.StructureInvalid;
            return false;
        }

        header = new VaultHeader
        {
            Version = CurrentFormatVersion,
            KdfAlgorithm = kdfAlg,
            AeadAlgorithm = aeadAlg,
            Iterations = iterations,
            MemoryKiB = memoryKiB,
            Parallelism = parallelism,
            Flags = flags,
            Reserved0 = reserved0,
            CipherLength = cipherLength,
            Reserved1 = reserved1,
            Salt = raw.Slice(40, SaltLength).ToArray(),
            Nonce = raw.Slice(56, NonceLength).ToArray(),
        };
        status = VaultOpenStatus.Ok;
        return true;
    }

    /// <summary>
    /// 构造 AAD：<c>"SnapFlowVault/v2"(16) ‖ 0x00(1) ‖ header(80) = 97</c>。
    /// NUL 分隔是第二道保险：标签定长已经保证拼接无歧义，多一个字节不花钱。
    /// 调用方负责在加密/解密后清零返回值（它含 salt/nonce，不算秘密但没必要留着）。
    /// </summary>
    public static byte[] BuildAad(VaultHeader header)
    {
        var headerBytes = header.ToBytes();
        var aad = new byte[AadLength];
        AadLabel.CopyTo(aad.AsSpan(0, AadLabel.Length));
        aad[AadLabel.Length] = 0x00;
        headerBytes.CopyTo(aad.AsSpan(AadLabel.Length + 1, HeaderLength));
        return aad;
    }

    /// <summary>头里的参数是否低于当前 KDF 策略（仅用于诊断/日志；本版本不做静默升级）。</summary>
    public static bool IsBelowCurrentKdfPolicy(VaultHeader header)
        => header.Iterations < VaultKdfPolicy.Iterations
           || header.MemoryKiB < VaultKdfPolicy.MemoryKiB
           || header.Parallelism < VaultKdfPolicy.Parallelism;
}

/// <summary>
/// KDF 参数的**唯一来源**。新建库、改主密码、加密备份全部从这里取，不允许各写一份。
///
/// 明确不做的事：不做"解锁后检测参数偏弱就静默升级"。理由有两条——
/// ① 静默重加密要在解锁后立刻写盘，与"口令/密钥尽早清零"的生命周期冲突；
/// ② 本场景没有外部参数漂移来源（文件只有我们自己写），静默升级只会增加写盘风险。
/// 需要换参数时改这里的常量并发版，由"改主密码"这条用户可见的路径自然生效。
/// </summary>
public static class VaultKdfPolicy
{
    public const int Iterations = 4;

    /// <summary>64MiB：桌面端解一次约半秒，是"用户能忍"与"离线暴破够贵"的平衡点。</summary>
    public const int MemoryKiB = 64 * 1024;

    public const int Parallelism = 4;

    /// <summary>主密码最小长度（创建与修改一致）。</summary>
    public const int MasterPasswordMinLength = 12;

    public static VaultHeader NewVaultHeader()
        => VaultHeader.NewV2(Iterations, MemoryKiB, Parallelism);
}
