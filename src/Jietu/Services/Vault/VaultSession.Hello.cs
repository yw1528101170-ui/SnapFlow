namespace Jietu;

/// <summary>
/// Windows Hello 快速解锁相关的会话成员（partial）。
///
/// 拆成独立文件的原因：主文件已经承载解锁/保存/状态机，Hello 是一块边界清晰、
/// 且天然"可有可无"的能力（不支持 Hello 的机器上完全不参与主流程）。
///
/// 线程：与主文件一致，所有公开成员只在 UI 线程调用（Hello 弹窗由系统派发回 UI 线程）。
/// </summary>
public sealed partial class VaultSession
{
    /// <summary>Windows Hello 副本是否可用（有 hello.blob 且库文件存在）。</summary>
    public bool HasHelloBlob => VaultStore.Exists(_vaultPath) && File.Exists(AppPaths.HelloBlob);

    /// <summary>启用快速解锁：创建/复用 Hello 凭据并把"库密钥副本"用 Hello 派生密钥包裹后落盘。</summary>
    public async Task<bool> EnableHelloAsync()
    {
        if (_key == null)
        {
            throw new InvalidOperationException("库未解锁。");
        }

        var wrapped = await HelloUnlock.WrapKeyAsync(_key);
        if (wrapped == null)
        {
            return false;
        }

        try
        {
            StoreHelloBlob(wrapped);
            return true;
        }
        finally
        {
            // 包裹后的副本本身不是密钥，但它是"用 Hello 签名解出来的门禁"，没必要留在堆上。
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(wrapped);
        }
    }

    /// <summary>
    /// 用 Hello 解出的密钥直接解锁（跳过 Argon2）。
    /// 成功时接管 <paramref name="key"/> 的所有权；失败时由本方法清零，调用方不要再碰它。
    /// </summary>
    public VaultUnlockOutcome TryUnlockWithKey(VaultKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (IsUnlocked)
        {
            key.Dispose();
            return VaultUnlockOutcome.Success;
        }

        try
        {
            var read = VaultStore.Read(_vaultPath);
            if (ApplyClassification(read, createCorruptBackup: true) is { } blocked)
            {
                return blocked;
            }

            var opened = VaultOpenPipeline.OpenWithKey(read, key);
            if (!opened.Ok)
            {
                return MapFailure(opened);
            }

            Adopt(read.Header, opened.Data!, key);
            return VaultUnlockOutcome.Success;
        }
        finally
        {
            // 成功路径里 key 已被会话接管（Adopt 把它存进 _key），Dispose 幂等但不该误伤——
            // 所以只在"还没有成为会话密钥"时清零。
            if (!ReferenceEquals(_key, key))
            {
                key.Dispose();
            }
        }
    }

    /// <summary>关闭快速解锁：删除系统凭据与本地副本。尽力而为，失败不抛。</summary>
    public async Task DisableHelloAsync() => await HelloUnlock.DisableAsync();

    /// <summary>启用 Hello 后由 <see cref="HelloUnlock"/> 写入包裹密钥副本。</summary>
    public void StoreHelloBlob(byte[] wrappedKey) => File.WriteAllBytes(AppPaths.HelloBlob, wrappedKey);

    /// <summary>读取 Hello 副本；不存在或读失败返回 null（读失败只记日志，不打扰用户）。</summary>
    public byte[]? ReadHelloBlob()
    {
        try
        {
            return File.Exists(AppPaths.HelloBlob) ? File.ReadAllBytes(AppPaths.HelloBlob) : null;
        }
        catch (Exception ex)
        {
            Log.Info("读取 Hello 副本失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>删除 Hello 副本文件（改主密码后旧密钥的包裹副本必然失效）。尽力而为。</summary>
    private static void TryDeleteHelloBlob()
    {
        try
        {
            if (File.Exists(AppPaths.HelloBlob))
            {
                File.Delete(AppPaths.HelloBlob);
            }
        }
        catch (Exception ex)
        {
            // 删不掉不影响主流程；下次改密码/关闭快速解锁时会再清。
            Log.Info("删除 Hello 副本失败：" + ex.Message);
        }
    }
}
