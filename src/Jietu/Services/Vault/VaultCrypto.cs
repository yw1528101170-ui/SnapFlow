using System.Security.Cryptography;
using System.Text.Json;
using Isopoh.Cryptography.Argon2;
using Isopoh.Cryptography.SecureArray;

namespace Jietu;

/// <summary>
/// 解锁/解析的结构化结果。<see cref="Detail"/> 只用于日志与诊断，**不得**直接展示给用户
/// （可能含库实现细节）；用户可见文案一律从 <see cref="VaultMessages"/> 取。
/// </summary>
public readonly record struct VaultOpenResult(VaultOpenStatus Status, VaultData? Data, string? Detail)
{
    public bool Ok => Status == VaultOpenStatus.Ok;

    public static VaultOpenResult Fail(VaultOpenStatus status, string? detail = null)
        => new(status, null, detail);
}

/// <summary>
/// 库容器的加解密：Argon2id 派生密钥 + AES-256-GCM（头整体作为 AAD）。
/// 纯函数、无 UI 依赖、全单测；任意线程可调用（<see cref="VaultKey"/> 由调用方保证独占）。
///
/// 明文清零的分工（谁分配谁清零）：
/// ① 本类内部产生的明文缓冲（解密出的 payload、AAD）自己清零，成功与异常路径都清；
/// ② 调用方持有的明文（序列化出的 JSON、口令 UTF-8 字节）由调用方 finally 清零，
///    本类只接收 <c>ReadOnlySpan</c>，物理上也无法替它清零。
/// </summary>
public static class VaultCrypto
{
    /// <summary>
    /// Argon2id 派生 32 字节密钥。参数来自头（调用方必须已经做过边界校验）。
    ///
    /// 与 v1 的两处关键差别：
    /// ① <c>ClearPassword = true</c>：交给库的那份口令副本由库在 Dispose 时清零；
    /// ② 不再由调用方先 <c>Encoding.UTF8.GetBytes</c> 再被库 <c>ToArray()</c> 复制一份——
    ///    全链路只允许存在一份"库能清零的副本"。
    /// </summary>
    public static VaultKey DeriveKey(ReadOnlySpan<byte> passwordUtf8, VaultHeader header)
    {
        if (passwordUtf8.Length == 0)
        {
            throw new ArgumentException("口令不能为空。", nameof(passwordUtf8));
        }

        if (header.Salt is not { Length: VaultFormat.SaltLength })
        {
            throw new ArgumentException("盐长度必须为 16 字节。", nameof(header));
        }

        if (header.Iterations is < VaultFormat.MinIterations or > VaultFormat.MaxIterations
            || header.MemoryKiB is < VaultFormat.MinMemoryKiB or > VaultFormat.MaxMemoryKiB
            || header.Parallelism is < VaultFormat.MinParallelism or > VaultFormat.MaxParallelism)
        {
            throw new ArgumentOutOfRangeException(nameof(header), "KDF 参数越界，拒绝派生（防 DoS 放大器）。");
        }

        var config = new Argon2Config
        {
            // HybridAddressing 即 Argon2id。
            Type = Argon2Type.HybridAddressing,
            Version = Argon2Version.Nineteen,
            TimeCost = header.Iterations,
            MemoryCost = header.MemoryKiB,
            Lanes = header.Parallelism,
            Threads = header.Parallelism,
            // 这一份被库接管，并由库在 Dispose 时清零（ClearPassword = true）。
            Password = passwordUtf8.ToArray(),
            Salt = header.Salt,
            HashLength = VaultKey.Length,
            ClearPassword = true,
            SecureArrayCall = new DefaultWindowsSecureArrayCall(),
        };

        byte[]? rawKey = null;
        try
        {
            using var argon2 = new Argon2(config);
            using var secureHash = argon2.Hash();
            rawKey = new byte[VaultKey.Length];
            secureHash.Buffer.AsSpan(0, VaultKey.Length).CopyTo(rawKey);

            // 库的 SecureArray 理论上自己会清；显式再清一次的成本极小，收益是"不依赖库的实现细节"。
            CryptographicOperations.ZeroMemory(secureHash.Buffer);
            return VaultKey.FromBytes(rawKey);
        }
        finally
        {
            if (rawKey != null)
            {
                CryptographicOperations.ZeroMemory(rawKey);
            }
        }
    }

    /// <summary>
    /// 加密明文载荷，返回「密文 + 16 字节 tag」（不含头）。
    ///
    /// <paramref name="header"/> 的 <c>CipherLength</c> 必须已经等于"明文长度 + 16"：
    /// AAD 用的是头的**实际写盘字节**，若这里与调用方稍后写盘的头不一致，
    /// 文件会自己解不开（且在测试里表现为"密码正确却认证失败"，极难查）。
    /// 所以这里直接断言，而不是替调用方猜。
    /// </summary>
    public static byte[] SealPayload(VaultHeader header, VaultKey key, ReadOnlySpan<byte> payloadJson)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (payloadJson.Length == 0)
        {
            throw new ArgumentException("载荷不能为空。", nameof(payloadJson));
        }

        var expected = (uint)(payloadJson.Length + VaultFormat.TagLength);
        if (header.CipherLength != expected)
        {
            throw new ArgumentException(
                $"头的 cipherLength({header.CipherLength}) 与明文长度（应写 {expected}）不一致，AAD 将与写盘字节不符。",
                nameof(header));
        }

        var body = new byte[payloadJson.Length + VaultFormat.TagLength];
        var aad = VaultFormat.BuildAad(header);
        try
        {
            using var aes = new AesGcm(key.Bytes, VaultFormat.TagLength);
            // tag 长度由 AesGcm 构造函数决定（.NET 8+ 的 span 重载不再收 tagSizeInBytes）。
            aes.Encrypt(
                header.Nonce,
                payloadJson,
                body.AsSpan(0, payloadJson.Length),
                body.AsSpan(payloadJson.Length),
                aad);
            return body;
        }
        catch
        {
            // 加密失败时不留半截密文（虽然它不是秘密，但半截数据只会误导后续排查）。
            CryptographicOperations.ZeroMemory(body);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(aad);
        }
    }

    /// <summary>
    /// 解密并解析载荷。<paramref name="body"/> 是「密文 + tag」（不含头）。
    /// 任何失败都返回具体分类，而不是 null——UI 需要区分"再试一次"（认证失败）与
    /// "别再试了"（结构不合法/版本过新）。
    /// </summary>
    public static VaultOpenResult Open(VaultHeader header, VaultKey key, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (body.Length < VaultFormat.TagLength)
        {
            return VaultOpenResult.Fail(VaultOpenStatus.StructureInvalid, "密文体短于 tag 长度");
        }

        if (header.CipherLength != (uint)body.Length)
        {
            return VaultOpenResult.Fail(VaultOpenStatus.StructureInvalid, "cipherLength 与密文体长度不一致");
        }

        if (header.Nonce is not { Length: VaultFormat.NonceLength })
        {
            return VaultOpenResult.Fail(VaultOpenStatus.StructureInvalid, "nonce 长度不为 12 字节");
        }

        var plaintext = new byte[body.Length - VaultFormat.TagLength];
        var aad = VaultFormat.BuildAad(header);
        try
        {
            using var aes = new AesGcm(key.Bytes, VaultFormat.TagLength);
            aes.Decrypt(
                header.Nonce,
                body[..^VaultFormat.TagLength],
                body[^VaultFormat.TagLength..],
                plaintext,
                aad);
        }
        catch (CryptographicException ex)
        {
            // 密码错 / 头被改 / 体被改 在这里汇合。不区分原因：区分等于给本地攻击者一个探测侧信道。
            CryptographicOperations.ZeroMemory(plaintext);
            return VaultOpenResult.Fail(VaultOpenStatus.AuthenticationFailed, ex.Message);
        }
        catch (ArgumentException ex)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            return VaultOpenResult.Fail(VaultOpenStatus.StructureInvalid, ex.Message);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(aad);
        }

        try
        {
            return ParsePayload(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// 反序列化明文载荷：schemaVersion 检查 + <see cref="VaultData.Normalize"/>。
    /// 走到这里说明字节已经通过 GCM 认证，所以失败基本只有两种可能：
    /// 载荷版本过新（旧程序读新库）或我们自己写出了坏 JSON。
    /// </summary>
    public static VaultOpenResult ParsePayload(ReadOnlySpan<byte> payloadJson)
    {
        VaultData? data;
        try
        {
            data = JsonSerializer.Deserialize(payloadJson, VaultJsonContext.Default.VaultData);
        }
        catch (JsonException ex)
        {
            return VaultOpenResult.Fail(VaultOpenStatus.StructureInvalid, ex.Message);
        }

        if (data == null)
        {
            return VaultOpenResult.Fail(VaultOpenStatus.StructureInvalid, "载荷反序列化为空");
        }

        if (data.SchemaVersion > VaultPayloadSchema.CurrentVersion)
        {
            // 绝不"尽量读然后写回"：未知字段在写回时会被丢掉，等于静默删用户数据。
            return VaultOpenResult.Fail(
                VaultOpenStatus.PayloadTooNew,
                $"载荷 schemaVersion={data.SchemaVersion} 高于支持的 {VaultPayloadSchema.CurrentVersion}");
        }

        data.Normalize();
        return new VaultOpenResult(VaultOpenStatus.Ok, data, null);
    }

    /// <summary>
    /// 整库加密为可直接写盘的文件字节（头 + 体）。
    /// <paramref name="sealedHeader"/> 回传本次**实际使用**的头（含新 nonce 与 cipherLength）：
    /// 调用方必须把它存为当前头，否则下一次保存会复用旧 nonce —— GCM 同密钥复用 nonce 等于自毁。
    /// </summary>
    public static byte[] SealFile(VaultData data, VaultHeader header, VaultKey key, out VaultHeader sealedHeader)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(key);

        var payload = VaultPayloadWriter.Write(data);
        try
        {
            // 每次写盘必须换 nonce：同密钥下复用 nonce 会同时毁掉 GCM 的机密性与完整性
            // （两段明文异或可还原，且可伪造任意密文）。这件事忘一次就是灾难级，
            // 所以放在这里而不是"要求调用方先 WithFreshNonce"——不能靠记性。
            sealedHeader = header.WithFreshNonce().WithPlaintextLength(payload.Length);
            var body = SealPayload(sealedHeader, key, payload);
            var file = new byte[VaultFormat.HeaderLength + body.Length];
            sealedHeader.ToBytes().CopyTo(file, 0);
            body.CopyTo(file, VaultFormat.HeaderLength);
            return file;
        }
        finally
        {
            // 明文 JSON 是本方法分配的，必须在这里清零（无论成功还是异常）。
            CryptographicOperations.ZeroMemory(payload);
        }
    }
}
