using System.Security.Cryptography;
using Windows.Security.Credentials;
using Windows.Security.Cryptography;

namespace Jietu;

/// <summary>快速解锁失败的原因分类：UI 层据此区分「用户主动取消」与「真的坏了」。</summary>
internal enum HelloOutcome
{
    Ok,
    /// <summary>用户在 Hello 弹窗上点了取消 / 直接关掉。</summary>
    Canceled,
    /// <summary>用户点了「使用密码」，主动回到主密码流程。</summary>
    PreferPassword,
    /// <summary>本机 Hello 凭据已不存在（重装系统 / 手动删除 / PIN 重建）。</summary>
    NoCredential,
    /// <summary>其它失败：密码库副本损坏、包装密钥不匹配等。</summary>
    Failed,
}

/// <summary>
/// <see cref="HelloUnlock.UnwrapKeyAsync"/> 的结构化结果。
/// 成功时 <see cref="Key"/> 是一把有效的 <see cref="VaultKey"/>，**所有权交给调用方**
/// （拿到后要么交给 VaultSession，要么自己 Dispose）。
/// </summary>
internal readonly record struct HelloResult(VaultKey? Key, HelloOutcome Outcome, string? Detail)
{
    public bool Ok => Key != null;

    public static HelloResult Fail(HelloOutcome outcome, string? detail = null) => new(null, outcome, detail);
}

/// <summary>
/// Windows Hello 快速解锁：KeePassXC Quick Unlock 同款方案。
/// Hello 持久密钥对固定 challenge 做签名，SHA-256(签名) 作为包裹密钥加密
/// 「库主密钥副本」存 hello.blob。Hello 验证通过才能解出库密钥，跳过 Argon2。
/// 主密码仍是根：改主密码 / 锁定时副本即作废删除。
///
/// 清零：签名缓冲与由它派生的 wrapKey 都在 finally 里清零（含异常路径）。
/// 解出的密钥用 <see cref="VaultKey"/> 承载，不再以裸 <c>byte[]</c> 穿过调用链。
/// </summary>
internal static class HelloUnlock
{
    private const string CredentialName = "SnapFlowVaultKey";

    // 固定 challenge：签名值的保密性来自 Hello 硬件密钥本身，challenge 只需确定性。
    private static readonly byte[] Challenge = new byte[32];

    /// <summary>blob 格式：[12 nonce][密文 + 16 tag]，包裹密钥 = SHA256(Hello 签名)。</summary>
    private const int BlobHeaderLength = VaultFormat.NonceLength;

    public static async Task<bool> IsSupportedAsync()
    {
        try
        {
            return await KeyCredentialManager.IsSupportedAsync();
        }
        catch (Exception ex)
        {
            Log.Info("查询 Hello 支持失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 启用快速解锁：弹出 Hello 创建/验证界面，成功后返回待保存的包裹密钥副本。
    /// 返回 null 表示用户取消或系统拒绝。
    /// </summary>
    public static async Task<byte[]?> WrapKeyAsync(VaultKey vaultKey)
    {
        ArgumentNullException.ThrowIfNull(vaultKey);

        try
        {
            var createResult = await KeyCredentialManager.RequestCreateAsync(
                CredentialName, KeyCredentialCreationOption.FailIfExists);
            if (createResult.Status != KeyCredentialStatus.Success || createResult.Credential == null)
            {
                // 已存在同名凭据（上次启用留下的）直接复用。
                var openResult = await KeyCredentialManager.OpenAsync(CredentialName);
                if (openResult.Status != KeyCredentialStatus.Success || openResult.Credential == null)
                {
                    Log.Info("Hello 凭据创建失败：" + createResult.Status);
                    return null;
                }

                return await SealAsync(openResult.Credential, vaultKey);
            }

            return await SealAsync(createResult.Credential, vaultKey);
        }
        catch (Exception ex)
        {
            Log.Info("Hello 启用失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Hello 验证并解出库密钥。失败原因分门别类返回，让界面能给出「可操作」的提示：
    /// 用户取消不该被当成验证失败来吓人，凭据丢失也不该建议用户反复重试。
    /// </summary>
    public static async Task<HelloResult> UnwrapKeyAsync(byte[] blob)
    {
        if (blob.Length < BlobHeaderLength + VaultFormat.TagLength)
        {
            Log.Info("Hello 副本过短，无法解包");
            return HelloResult.Fail(HelloOutcome.Failed, "快速解锁副本已损坏");
        }

        byte[]? signature = null;
        byte[]? wrapKey = null;
        byte[]? rawKey = null;
        try
        {
            var openResult = await KeyCredentialManager.OpenAsync(CredentialName);
            if (openResult.Status != KeyCredentialStatus.Success || openResult.Credential == null)
            {
                Log.Info("Hello 凭据打开失败：" + openResult.Status);
                return HelloResult.Fail(MapStatus(openResult.Status), openResult.Status.ToString());
            }

            var signResult = await openResult.Credential.RequestSignAsync(CryptographicBuffer.CreateFromByteArray(Challenge));
            if (signResult.Status != KeyCredentialStatus.Success || signResult.Result == null)
            {
                Log.Info("Hello 验证失败：" + signResult.Status);
                return HelloResult.Fail(MapStatus(signResult.Status), signResult.Status.ToString());
            }

            CryptographicBuffer.CopyToByteArray(signResult.Result, out signature);
            wrapKey = SHA256.HashData(signature);
            var nonce = blob[..BlobHeaderLength];
            var cipher = blob[BlobHeaderLength..];
            rawKey = new byte[cipher.Length - VaultFormat.TagLength];
            using var aes = new AesGcm(wrapKey, VaultFormat.TagLength);
            aes.Decrypt(nonce, cipher[..^VaultFormat.TagLength], cipher[^VaultFormat.TagLength..], rawKey);

            var key = VaultKey.FromBytes(rawKey);
            return new HelloResult(key, HelloOutcome.Ok, null);
        }
        catch (CryptographicException ex)
        {
            // 常见于用户重置了 PIN / 重新录入指纹：Hello 密钥变了，旧副本再也解不开。
            Log.Info("Hello 解包失败（副本与凭据不匹配）：" + ex.Message);
            return HelloResult.Fail(HelloOutcome.Failed, "快速解锁副本与本机 Hello 凭据不再匹配");
        }
        catch (Exception ex)
        {
            Log.Info("Hello 解包失败：" + ex.Message);
            return HelloResult.Fail(HelloOutcome.Failed, ex.Message);
        }
        finally
        {
            // 签名与由它派生的包裹密钥等价于"钥匙的模具"，异常路径同样必须清零。
            if (signature != null)
            {
                CryptographicOperations.ZeroMemory(signature);
            }

            if (wrapKey != null)
            {
                CryptographicOperations.ZeroMemory(wrapKey);
            }

            if (rawKey != null)
            {
                CryptographicOperations.ZeroMemory(rawKey);
            }
        }
    }

    /// <summary>删除 Hello 凭据与本地副本（关闭快速解锁时调用）。尽力而为。</summary>
    public static async Task DisableAsync()
    {
        try
        {
            await KeyCredentialManager.DeleteAsync(CredentialName);
        }
        catch (Exception ex)
        {
            // 凭据可能已不存在。
            Log.Info("删除 Hello 凭据失败：" + ex.Message);
        }

        try
        {
            if (File.Exists(AppPaths.HelloBlob))
            {
                File.Delete(AppPaths.HelloBlob);
            }
        }
        catch (Exception ex)
        {
            Log.Info("删除 Hello 副本失败：" + ex.Message);
        }
    }

    private static HelloOutcome MapStatus(KeyCredentialStatus status) => status switch
    {
        KeyCredentialStatus.UserCanceled => HelloOutcome.Canceled,
        KeyCredentialStatus.UserPrefersPassword => HelloOutcome.PreferPassword,
        KeyCredentialStatus.NotFound => HelloOutcome.NoCredential,
        _ => HelloOutcome.Failed,
    };

    private static async Task<byte[]?> SealAsync(KeyCredential credential, VaultKey vaultKey)
    {
        byte[]? signature = null;
        byte[]? wrapKey = null;
        try
        {
            var signResult = await credential.RequestSignAsync(CryptographicBuffer.CreateFromByteArray(Challenge));
            if (signResult.Status != KeyCredentialStatus.Success || signResult.Result == null)
            {
                Log.Info("Hello 签名失败：" + signResult.Status);
                return null;
            }

            CryptographicBuffer.CopyToByteArray(signResult.Result, out signature);
            wrapKey = SHA256.HashData(signature);
            var nonce = RandomNumberGenerator.GetBytes(VaultFormat.NonceLength);
            var sealedKey = new byte[VaultKey.Length + VaultFormat.TagLength];
            using var aes = new AesGcm(wrapKey, VaultFormat.TagLength);
            aes.Encrypt(nonce, vaultKey.Bytes, sealedKey.AsSpan(..^VaultFormat.TagLength), sealedKey.AsSpan(^VaultFormat.TagLength..));
            return [.. nonce, .. sealedKey];
        }
        finally
        {
            if (signature != null)
            {
                CryptographicOperations.ZeroMemory(signature);
            }

            if (wrapKey != null)
            {
                CryptographicOperations.ZeroMemory(wrapKey);
            }
        }
    }
}
