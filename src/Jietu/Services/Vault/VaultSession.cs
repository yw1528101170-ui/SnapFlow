using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Dispatching;

namespace Jietu;

/// <summary>
/// 密码库会话：唯一的解锁状态持有者。密钥只在内存（锁定即 Array.Clear），
/// 自动锁定覆盖三个维度：空闲超时、Windows 锁屏、系统睡眠（后两者经
/// MessageWindow 的 WTS/电源广播，零额外依赖）。
/// 所有公开方法都必须在 UI 线程调用（窗口/托盘/热键入口都在 UI 线程上）。
/// </summary>
public sealed class VaultSession : IDisposable
{
    private readonly string _vaultPath;
    private readonly Func<AppSettings> _settings;
    private readonly DispatcherQueueTimer _idleTimer;

    private VaultHeader _header;
    private byte[]? _key;
    private VaultData? _data;
    private DateTimeOffset _lastActivity = DateTimeOffset.UtcNow;

    public event Action? Unlocked;
    public event Action? Locked;

    public VaultSession(DispatcherQueue dispatcherQueue, Func<AppSettings> settings)
    {
        _vaultPath = AppPaths.VaultFile;
        _settings = settings;
        _idleTimer = dispatcherQueue.CreateTimer();
        _idleTimer.IsRepeating = true;
        _idleTimer.Interval = TimeSpan.FromSeconds(15);
        _idleTimer.Tick += (_, _) => CheckIdleLock();

        // 锁屏/睡眠立即锁：MessageWindow 的 WTS/电源广播在 UI 线程派发，直接调用即可。
        if (App.SessionLockedSink is { } sink)
        {
            sink.SessionLocked += Lock;
        }
    }

    /// <summary>库文件是否存在（决定 UI 走「创建」还是「解锁」）。</summary>
    public bool Exists => VaultStore.Exists(_vaultPath);

    public bool IsUnlocked => _data != null;

    /// <summary>当前解密数据；锁定状态返回 null。只在 UI 线程读取。</summary>
    public VaultData? Data => _data;

    /// <summary>解锁（Argon2 在线程池跑，不阻塞 UI）。失败返回可展示的错误文本。</summary>
    public async Task<(bool Ok, string? Error)> TryUnlockAsync(string masterPassword)
    {
        if (IsUnlocked)
        {
            return (true, null);
        }

        var parsed = VaultStore.Parse(_vaultPath, VaultStore.Load(_vaultPath));
        if (parsed == null)
        {
            return (false, VaultStore.LastRecoveryMessage ?? "密码库文件无法读取。");
        }

        var (header, body) = parsed.Value;
        var password = Encoding.UTF8.GetBytes(masterPassword);
        var key = await Task.Run(() => VaultCrypto.DeriveKey(password, header));
        var data = VaultCrypto.Open(header, key, body);
        if (data == null)
        {
            Array.Clear(key);
            return (false, "主密码不正确。");
        }

        _header = header;
        _key = key;
        _data = data;
        _lastActivity = DateTimeOffset.UtcNow;
        StartIdleTimer();
        Unlocked?.Invoke();
        return (true, null);
    }

    /// <summary>新建库并解锁。库已存在或密码太弱时抛出，由 UI 层提示。</summary>
    public async Task CreateAsync(string masterPassword)
    {
        if (Exists)
        {
            throw new InvalidOperationException("密码库已存在。");
        }

        ValidateMasterPassword(masterPassword);
        var header = VaultHeader.NewHeader();
        var password = Encoding.UTF8.GetBytes(masterPassword);
        var key = await Task.Run(() => VaultCrypto.DeriveKey(password, header));
        var data = VaultDefaults.NewData();
        VaultStore.Save(_vaultPath, header, VaultCrypto.Seal(header, key, data));
        _header = header;
        _key = key;
        _data = data;
        _lastActivity = DateTimeOffset.UtcNow;
        StartIdleTimer();
        Unlocked?.Invoke();
    }

    /// <summary>用密钥直接解锁（Hello 快速解锁路径，跳过 Argon2）。失败清密钥返回 false。</summary>
    public bool TryUnlockWithKey(byte[] key, out string? error)
    {
        error = null;
        if (IsUnlocked)
        {
            return true;
        }

        var parsed = VaultStore.Parse(_vaultPath, VaultStore.Load(_vaultPath));
        if (parsed == null)
        {
            error = VaultStore.LastRecoveryMessage ?? "密码库文件无法读取。";
            Array.Clear(key);
            return false;
        }

        var (header, body) = parsed.Value;
        var data = VaultCrypto.Open(header, key, body);
        if (data == null)
        {
            Array.Clear(key);
            error = "快速解锁副本已失效，请用主密码解锁。";
            return false;
        }

        _header = header;
        _key = key;
        _data = data;
        _lastActivity = DateTimeOffset.UtcNow;
        StartIdleTimer();
        Unlocked?.Invoke();
        return true;
    }

    /// <summary>把内存中的改动重新加密落盘。锁定状态调用是空操作。</summary>
    public void Save()
    {
        if (_data == null || _key == null)
        {
            return;
        }

        // GCM 同密钥下 nonce 绝不能复用：每次保存都换新 nonce，头随之重写。
        _header = _header with { Nonce = RandomNumberGenerator.GetBytes(VaultHeader.NonceLength) };
        VaultStore.Save(_vaultPath, _header, VaultCrypto.Seal(_header, _key, _data));
    }

    /// <summary>修改主密码：用新密码重新派生密钥并整库重加密，Hello 副本作废（需重新启用）。</summary>
    public void ChangeMasterPassword(string newPassword)
    {
        if (_data == null)
        {
            throw new InvalidOperationException("库未解锁。");
        }

        ValidateMasterPassword(newPassword);
        var newHeader = VaultHeader.NewHeader();
        var newKey = VaultCrypto.DeriveKey(Encoding.UTF8.GetBytes(newPassword), newHeader);
        VaultStore.Save(_vaultPath, newHeader, VaultCrypto.Seal(newHeader, newKey, _data));
        Array.Clear(_key!);
        _key = newKey;
        _header = newHeader;
        TryDeleteHelloBlob();
    }

    /// <summary>启用 Windows Hello 快速解锁：创建/复用 Hello 凭据并保存包裹密钥副本。</summary>
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

        StoreHelloBlob(wrapped);
        return true;
    }

    public void TouchActivity() => _lastActivity = DateTimeOffset.UtcNow;

    /// <summary>立即锁定并清场：清密钥、清数据引用、停计时器。幂等。
    /// Hello 副本保留（包裹密钥绑定本机 TPM 的 Hello 凭据，拷走也无法离线解密），
    /// 仅在修改主密码或用户显式关闭快速解锁时删除。</summary>
    public void Lock()
    {
        if (_data == null && _key == null)
        {
            return;
        }

        _data = null;
        if (_key != null)
        {
            Array.Clear(_key);
            _key = null;
        }

        _idleTimer.Stop();
        Locked?.Invoke();
    }

    /// <summary>Windows Hello 副本是否可用（有 hello.blob 且库存在）。</summary>
    public bool HasHelloBlob => File.Exists(AppPaths.HelloBlob);

    /// <summary>启用 Hello 后由 HelloUnlock 写入包裹密钥副本；改主密码/关闭快速解锁时删除。</summary>
    public void StoreHelloBlob(byte[] wrappedKey) => File.WriteAllBytes(AppPaths.HelloBlob, wrappedKey);

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

    /// <summary>关闭快速解锁：删除 Hello 副本与系统凭据（设置页调用）。尽力而为。</summary>
    public async Task DisableHelloAsync() => await HelloUnlock.DisableAsync();

    /// <summary>删除 Hello 副本文件（改主密码后旧密钥的包裹副本必然失效）。</summary>
    private static void TryDeleteHelloBlob()
    {
        try
        {
            if (File.Exists(AppPaths.HelloBlob))
            {
                File.Delete(AppPaths.HelloBlob);
            }
        }
        catch
        {
            // 删不掉不影响主流程；下次改密码/关闭快速解锁时会再清。
        }
    }

    private void StartIdleTimer()
    {
        if (_settings().VaultLockMinutes > 0)
        {
            _idleTimer.Start();
        }
    }

    private void CheckIdleLock()
    {
        if (!IsUnlocked)
        {
            _idleTimer.Stop();
            return;
        }

        var minutes = _settings().VaultLockMinutes;
        if (minutes <= 0)
        {
            return;
        }

        if (DateTimeOffset.UtcNow - _lastActivity >= TimeSpan.FromMinutes(minutes))
        {
            Lock();
        }
    }

    public void Dispose()
    {
        Lock();
        _idleTimer.Stop();
        if (App.SessionLockedSink is { } sink)
        {
            sink.SessionLocked -= Lock;
        }
    }

    /// <summary>内部校验：主密码长度 12+，与创建/修改界面的要求一致。强度提示由 UI 层用熵估计给出。</summary>
    private static void ValidateMasterPassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 12)
        {
            throw new ArgumentException("主密码至少需要 12 个字符。");
        }
    }
}
