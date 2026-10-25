using System.Security.Cryptography;
using Windows.Security.Credentials;
using Windows.Security.Cryptography;

namespace Jietu;

/// <summary>
/// Windows Hello 快速解锁：KeePassXC Quick Unlock 同款方案。
/// Hello 持久密钥对固定 challenge 做签名，SHA-256(签名) 作为包裹密钥加密
/// 「库主密钥副本」存 hello.blob。Hello 验证通过才能解出库密钥，跳过 Argon2。
/// 主密码仍是根：改主密码 / 锁定时副本即作废删除。
/// </summary>
internal static class HelloUnlock
{
    private const string CredentialName = "SnapFlowVaultKey";

    // 固定 challenge：签名值的保密性来自 Hello 硬件密钥本身，challenge 只需确定性。
    private static readonly byte[] Challenge = new byte[32];

    /// <summary>blob 格式：[12 nonce][密文 + 16 tag]，包裹密钥 = SHA256(Hello 签名)。</summary>
    private const int BlobHeaderLength = VaultHeader.NonceLength;

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
    public static async Task<byte[]?> WrapKeyAsync(byte[] vaultKey)
    {
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

    /// <summary>Hello 验证并解出库密钥；取消/失败/无副本返回 null。</summary>
    public static async Task<byte[]?> UnwrapKeyAsync(byte[] blob)
    {
        if (blob.Length < BlobHeaderLength + VaultHeader.TagLength)
        {
            return null;
        }

        try
        {
            var openResult = await KeyCredentialManager.OpenAsync(CredentialName);
            if (openResult.Status != KeyCredentialStatus.Success || openResult.Credential == null)
            {
                Log.Info("Hello 凭据打开失败：" + openResult.Status);
                return null;
            }

            var signResult = await openResult.Credential.RequestSignAsync(CryptographicBuffer.CreateFromByteArray(Challenge));
            if (signResult.Status != KeyCredentialStatus.Success || signResult.Result == null)
            {
                Log.Info("Hello 验证失败：" + signResult.Status);
                return null;
            }

            CryptographicBuffer.CopyToByteArray(signResult.Result, out var signature);
            var wrapKey = SHA256.HashData(signature);
            var nonce = blob[..BlobHeaderLength];
            var cipher = blob[BlobHeaderLength..];
            var key = new byte[cipher.Length - VaultHeader.TagLength];
            using var aes = new AesGcm(wrapKey, VaultHeader.TagLength);
            aes.Decrypt(nonce, cipher[..^VaultHeader.TagLength], cipher[^VaultHeader.TagLength..], key);
            return key;
        }
        catch (Exception ex)
        {
            Log.Info("Hello 解包失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>删除 Hello 凭据与本地副本（关闭快速解锁时调用）。尽力而为。</summary>
    public static async Task DisableAsync()
    {
        try
        {
            await KeyCredentialManager.DeleteAsync(CredentialName);
        }
        catch
        {
            // 凭据可能已不存在。
        }

        try
        {
            if (File.Exists(AppPaths.HelloBlob))
            {
                File.Delete(AppPaths.HelloBlob);
            }
        }
        catch
        {
            // 同上。
        }
    }

    private static async Task<byte[]?> SealAsync(KeyCredential credential, byte[] vaultKey)
    {
        var signResult = await credential.RequestSignAsync(CryptographicBuffer.CreateFromByteArray(Challenge));
        if (signResult.Status != KeyCredentialStatus.Success || signResult.Result == null)
        {
            Log.Info("Hello 签名失败：" + signResult.Status);
            return null;
        }

        CryptographicBuffer.CopyToByteArray(signResult.Result, out var signature);
        var wrapKey = SHA256.HashData(signature);
        var nonce = RandomNumberGenerator.GetBytes(VaultHeader.NonceLength);
        var sealedKey = new byte[vaultKey.Length + VaultHeader.TagLength];
        using var aes = new AesGcm(wrapKey, VaultHeader.TagLength);
        aes.Encrypt(nonce, vaultKey, sealedKey.AsSpan(..^VaultHeader.TagLength), sealedKey.AsSpan(^VaultHeader.TagLength..));
        return [.. nonce, .. sealedKey];
    }
}
