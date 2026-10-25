using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Dispatching;

namespace Jietu;

/// <summary>
/// 密码库会话：唯一的解锁状态持有者，也是全仓唯一允许持有 <see cref="VaultKey"/> 的地方。
///
/// 线程纪律（WinUI 3）：本类的 <see cref="Data"/> / <see cref="State"/> / <see cref="Block"/> /
/// <see cref="IsUnlocked"/> 与 <see cref="Unlocked"/> / <see cref="Locked"/> 事件**只在 UI 线程**读写。
/// 桥（线程池）与托盘必须经 <c>DispatcherQueue.TryEnqueue</c> 编组后再碰这些成员。
/// KDF 与 AES 一律 <c>Task.Run</c> 到线程池，UI 线程只做状态切换。
///
/// 密钥清零口径（不许夸大）：本类保证自己持有的口令 UTF-8 缓冲、<see cref="VaultKey"/>、
/// 明文载荷在离开作用域后全为 0（含异常路径）。<c>string</c> 形式的主密码来自
/// PasswordBox.Password，**无法清零**；托管堆里的中间副本也无法证明不存在。
/// 所以可声明的只有"我们持有的缓冲在离开作用域后全为 0"。
/// </summary>
public sealed partial class VaultSession : IDisposable
{
    private readonly string _vaultPath;
    private readonly Func<AppSettings> _settings;
    private readonly DispatcherQueueTimer _idleTimer;

    /// <summary>异步保存编排（防抖合并 + 单飞 + 失败回退）。</summary>
    private readonly VaultSaveScheduler _scheduler;

    /// <summary>
    /// 密钥使用闸门。后台保存借用密钥时要先拿到它，锁定/改密要清零或替换密钥时同样要先拿到它——
    /// 否则会出现"密钥已清零而保存任务仍在使用"的崩溃或坏文件。
    /// </summary>
    private readonly SemaphoreSlim _keyGate = new(1, 1);

    /// <summary>头的读写锁：头是 struct，后台写、UI 读，不加锁可能出现撕裂读。</summary>
    private readonly Lock _headerGate = new();

    private VaultHeader _header;
    private VaultKey? _key;
    private VaultData? _data;

    /// <summary>改主密码期间禁止捕获新快照：否则旧密钥的快照会在新文件写完后把旧内容写回去。</summary>
    private volatile bool _suspendCapture;

    /// <summary>锁屏广播的订阅句柄（存起来才能退订——lambda 每次都是新实例）。</summary>
    private Action? _sessionLockedHandler;

    private VaultState _state = VaultState.Missing;
    private VaultBlockInfo _block = VaultBlockInfo.None;
    private DateTimeOffset _lastActivity = DateTimeOffset.UtcNow;

    public event Action? Unlocked;
    public event Action? Locked;

    /// <param name="vaultPath">
    /// 库文件路径。默认 <see cref="AppPaths.VaultFile"/>，生产行为与该默认值完全一致。
    /// 之所以做成构造参数：v1 字节不变、滚动备份轮转、tmp 清理这三件事只有在临时目录里才能可重复地测。
    /// </param>
    public VaultSession(DispatcherQueue dispatcherQueue, Func<AppSettings> settings, string? vaultPath = null)
    {
        _vaultPath = string.IsNullOrWhiteSpace(vaultPath) ? AppPaths.VaultFile : vaultPath;
        _settings = settings;
        _idleTimer = dispatcherQueue.CreateTimer();
        _idleTimer.IsRepeating = true;
        _idleTimer.Interval = TimeSpan.FromSeconds(15);
        _idleTimer.Tick += (_, _) => CheckIdleLock();
        _scheduler = new VaultSaveScheduler(dispatcherQueue, CaptureSnapshot, PersistSnapshotAsync);

        // 锁屏/睡眠立即锁：MessageWindow 的 WTS/电源广播在 UI 线程派发。
        // 走 LockAsync 是为了先把挂起的编辑落盘——直接在锁屏瞬间丢弃最后 400ms 的修改
        // 对用户来说是"我明明刚保存过"，比多花几毫秒糟糕得多。
        if (App.SessionLockedSink is { } sink)
        {
            _sessionLockedHandler = () => _ = LockAsync();
            sink.SessionLocked += _sessionLockedHandler;
        }

        // 启动即分类：让界面第一次打开就能显示"旧库/版本过新/损坏"的阻断卡，
        // 而不是等用户输入密码后才告诉他"这个库根本打不开"。
        ClassifyCurrentFile(createCorruptBackup: true);
    }

    /// <summary>库文件路径（诊断与"打开所在文件夹"用）。</summary>
    public string VaultPath => _vaultPath;

    /// <summary>库文件是否存在（任何分类，含被阻断的旧库）。</summary>
    public bool Exists => VaultStore.Exists(_vaultPath);

    public bool IsUnlocked => _data != null;

    /// <summary>当前解密数据；锁定/阻断状态返回 null。只在 UI 线程读取。</summary>
    public VaultData? Data => _data;

    /// <summary>会话状态（Missing / Locked / Unlocked / Blocked）。只在 UI 线程读取。</summary>
    public VaultState State => _state;

    /// <summary>阻断信息；未阻断时为 <see cref="VaultBlockInfo.None"/>。只在 UI 线程读取。</summary>
    public VaultBlockInfo Block => _block;

    /// <summary>是否处于阻断态（文件存在但本版本无法打开）。</summary>
    public bool IsBlocked => _state == VaultState.Blocked;

    /// <summary>
    /// 用主密码解锁。Argon2 全在线程池，UI 线程只做状态切换。
    /// 返回结构化结果：UI 依据 <see cref="VaultUnlockOutcome.Blocked"/> 决定"显示阻断卡"还是
    /// "保留输入区让人改密码重试"——认证失败**永不**进入阻断态。
    /// </summary>
    public async Task<VaultUnlockOutcome> TryUnlockAsync(string masterPassword)
    {
        if (IsUnlocked)
        {
            return VaultUnlockOutcome.Success;
        }

        var read = VaultStore.Read(_vaultPath);
        if (ApplyClassification(read, createCorruptBackup: true) is { } blocked)
        {
            return blocked;
        }

        if (string.IsNullOrEmpty(masterPassword))
        {
            // 空口令不可能解开任何库，也没必要付一次 Argon2 的代价。
            return new VaultUnlockOutcome(
                false,
                VaultOpenStatus.AuthenticationFailed,
                VaultMessages.UnlockFailedTitle,
                VaultMessages.UnlockFailedBody,
                VaultMessages.UnlockFailedHint,
                VaultBlockKind.None);
        }

        var passwordBytes = Encoding.UTF8.GetBytes(masterPassword);
        VaultMemoryDebug.Track("vault-master-password", passwordBytes);
        VaultOpenResult opened;
        VaultKey? key = null;
        try
        {
            // 步骤 8：唯一昂贵的一步，放在所有静态检查之后（Precheck 已在 ApplyClassification 里跑完）。
            key = await Task.Run(() => VaultCrypto.DeriveKey(passwordBytes, read.Header));

            // 步骤 9～11。
            opened = await Task.Run(() => VaultOpenPipeline.OpenWithKey(read, key));
        }
        catch (Exception ex)
        {
            key?.Dispose();
            Log.Error(ex);
            return new VaultUnlockOutcome(
                false, VaultOpenStatus.IoFailed, VaultMessages.UnlockFailedTitle, VaultMessages.VaultReadFailed(ex.Message), null, VaultBlockKind.None);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }

        if (!opened.Ok)
        {
            key?.Dispose();
            return MapFailure(opened);
        }

        Adopt(read.Header, opened.Data!, key!);
        return VaultUnlockOutcome.Success;
    }

    /// <summary>
    /// 新建库并解锁。库已存在（含 v1 旧库）时抛异常——**绝不覆盖**：
    /// 旧库里可能是用户唯一的凭据副本，程序内不提供任何删除/覆盖/重置它的入口。
    /// </summary>
    public async Task CreateAsync(string masterPassword)
    {
        ClassifyCurrentFile(createCorruptBackup: false);
        if (Exists)
        {
            var read = VaultStore.Read(_vaultPath);
            if (read.Kind == VaultFileKind.LegacyV1)
            {
                // 开发期：v1 旧库没有真实用户，直接让用户重建。
                // 但仍**改名留档**而不是覆盖或删除——成本是一次 File.Move，
                // 收益是"任何情况下都不出现不可恢复的数据销毁"这条底线不被打破。
                var orphan = _vaultPath + ".v1-orphan-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Move(_vaultPath, orphan, overwrite: true);
                Log.Info($"密码库：创建新库前把 v1 旧文件改名留档 → {Path.GetFileName(orphan)}");
            }
            else
            {
                throw new InvalidOperationException(VaultMessages.CreateBlockedByExistingVault);
            }
        }

        ValidateMasterPassword(masterPassword);

        var header = VaultKdfPolicy.NewVaultHeader();
        var data = VaultDefaults.NewData();
        var passwordBytes = Encoding.UTF8.GetBytes(masterPassword);
        VaultMemoryDebug.Track("vault-master-password-create", passwordBytes);
        VaultKey? key = null;
        try
        {
            key = await Task.Run(() => VaultCrypto.DeriveKey(passwordBytes, header));
            var file = VaultCrypto.SealFile(data, header, key, out var sealedHeader);
            await VaultStore.SaveAsync(_vaultPath, file).ConfigureAwait(true);
            Adopt(sealedHeader, data, key);
            key = null; // 所有权已交给会话，finally 不再清零它
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            key?.Dispose();
        }
    }

    /// <summary>
    /// 记一次编辑：防抖合并后异步落盘，UI 线程 O(1) 返回。
    /// 编辑路径一律用它；需要"立刻落盘再继续"的场景（导入、恢复、改密前）用 <see cref="FlushAsync"/>。
    /// </summary>
    public void RequestSave()
    {
        if (_data == null || _key == null)
        {
            return;
        }

        _scheduler.Request();
    }

    /// <summary>等待所有挂起的编辑落盘。返回 false 表示等待期间写盘失败（磁盘仍是上一版）。</summary>
    public Task<bool> FlushAsync(CancellationToken ct = default) => _scheduler.FlushAsync(ct);

    /// <summary>当前保存状态（供界面显示"已保存 / 保存中 / 保存失败"）。</summary>
    public VaultSaveState SaveState => _scheduler.State;

    public event Action<VaultSaveState, string?>? SaveStateChanged
    {
        add => _scheduler.StateChanged += value;
        remove => _scheduler.StateChanged -= value;
    }

    /// <summary>
    /// 同步落盘（阻塞调用线程）。只用于必须立刻见到文件的极少数场景；
    /// UI 线程优先用 <see cref="RequestSave"/> + <see cref="FlushAsync"/>，避免卡界面。
    /// </summary>
    public void Save()
    {
        if (_data == null || _key == null)
        {
            return;
        }

        _keyGate.Wait();
        try
        {
            var key = _key ?? throw new InvalidOperationException("库已锁定。");
            var header = ReadHeader().WithFreshNonce();
            var file = VaultCrypto.SealFile(_data, header, key, out var sealedHeader);
            VaultStore.Save(_vaultPath, file);
            WriteHeader(sealedHeader);
        }
        finally
        {
            _keyGate.Release();
        }
    }

    /// <summary>
    /// 修改主密码：换新 salt/nonce + 按当前策略重新派生并整库重加密。
    ///
    /// 异步化的理由：Argon2（64MiB / 4 迭代）在 UI 线程要 0.5~2 秒，界面会整段冻住。
    /// 取消语义：文件替换只发生在 <see cref="VaultStore.Save"/> 的 Move 那一刻；
    /// 之前任何取消或异常都不动原文件与旧密钥，用户仍可用原主密码解锁。
    /// </summary>
    /// <param name="progress">0~100 的真实进度（派生 → 序列化 → 写盘 → 换钥）。</param>
    public async Task ChangeMasterPasswordAsync(
        string newPassword,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        if (_data == null || _key == null)
        {
            throw new InvalidOperationException("库未解锁。");
        }

        ValidateMasterPassword(newPassword);

        // 暂停捕获：改密期间若有旧密钥的快照落盘，会把旧内容写回新文件（旧密钥已作废，等于数据回滚）。
        _suspendCapture = true;
        try
        {
            await _scheduler.FlushAsync(ct).ConfigureAwait(true);

            // 只克隆一次，之后全程不碰 UI 线程的数据（改密期间界面是模态的，不会再有编辑）。
            var data = _data.CloneForBackground();
            var header = VaultKdfPolicy.NewVaultHeader();
            var passwordBytes = Encoding.UTF8.GetBytes(newPassword);
            VaultMemoryDebug.Track("vault-master-password-change", passwordBytes);

            VaultKey? newKey = null;
            try
            {
                progress?.Report(0);
                newKey = await Task.Run(() => VaultCrypto.DeriveKey(passwordBytes, header), ct)
                    .ConfigureAwait(true);
                progress?.Report(50);

                var payload = await Task.Run(() => VaultPayloadWriter.Write(data), ct).ConfigureAwait(true);
                byte[] file;
                try
                {
                    var body = VaultCrypto.SealPayload(header, newKey, payload);
                    file = new byte[VaultFormat.HeaderLength + body.Length];
                    header.ToBytes().CopyTo(file, 0);
                    body.CopyTo(file, VaultFormat.HeaderLength);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(payload);
                }

                progress?.Report(80);
                await Task.Run(() => VaultStore.Save(_vaultPath, file, ct), ct).ConfigureAwait(true);
                progress?.Report(100);

                // 只有文件已经原子替换成功，才允许换内存里的密钥；
                // 任何异常都必须让旧密钥继续可用（用户还能用原密码解锁）。
                await _keyGate.WaitAsync(CancellationToken.None).ConfigureAwait(true);
                try
                {
                    var previous = _key;
                    _key = newKey;
                    newKey = null;
                    previous?.Dispose();
                    WriteHeader(header);
                }
                finally
                {
                    _keyGate.Release();
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
                newKey?.Dispose();
            }

            // Hello 包裹的是旧密钥，改密后必然失效。
            TryDeleteHelloBlob();
        }
        finally
        {
            _suspendCapture = false;
        }
    }

    public void TouchActivity() => _lastActivity = DateTimeOffset.UtcNow;

    // ---------------- 回收站 / 历史 / 体检（C 批） ----------------

    /// <summary>回收站条目（锁定后为空）。只在 UI 线程读取。</summary>
    public IReadOnlyList<VaultEntry> RecycleBin => _data?.RecycleBin ?? [];

    /// <summary>把条目移入回收站（不再直接销毁）。</summary>
    public bool MoveToRecycleBin(string entryId)
    {
        if (_data == null)
        {
            return false;
        }

        var moved = VaultRecycleBinService.MoveToBin(_data, entryId, DateTimeOffset.UtcNow);
        if (moved)
        {
            RequestSave();
        }

        return moved;
    }

    public bool RestoreFromRecycleBin(string entryId)
    {
        if (_data == null)
        {
            return false;
        }

        var restored = VaultRecycleBinService.Restore(_data, entryId);
        if (restored)
        {
            RequestSave();
        }

        return restored;
    }

    public bool PurgeRecycleBinEntry(string entryId)
    {
        if (_data == null)
        {
            return false;
        }

        var purged = VaultRecycleBinService.Purge(_data, entryId);
        if (purged)
        {
            RequestSave();
        }

        return purged;
    }

    public int ClearRecycleBin()
    {
        if (_data == null)
        {
            return 0;
        }

        var cleared = VaultRecycleBinService.Clear(_data);
        if (cleared > 0)
        {
            RequestSave();
        }

        return cleared;
    }

    /// <summary>体检（纯计算，不写盘）。条目多时调用方应放后台线程。</summary>
    public VaultHealthReport ComputeHealth(VaultHealthOptions? options = null, CancellationToken ct = default)
        => _data == null ? VaultHealthReport.Empty : VaultHealthService.Compute(_data, options, ct);

    /// <summary>
    /// 立即锁定（不等待挂起的保存）。只用于"已经来不及保存"的场景；
    /// 常规路径请用 <see cref="LockAsync"/> —— 它会先把编辑落盘再清密钥。
    /// </summary>
    public void Lock()
    {
        _keyGate.Wait();
        try
        {
            LockCore();
        }
        finally
        {
            _keyGate.Release();
        }
    }

    /// <summary>
    /// 先落盘、再锁定。这是**密钥清零屏障**：保存任务可能正在后台用密钥，
    /// 直接清零会让它拿到 ObjectDisposedException 或写出坏文件。
    /// </summary>
    public async Task LockAsync(CancellationToken ct = default)
    {
        try
        {
            await _scheduler.FlushAsync(ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 落盘失败也要锁：安全优先于"最后一次编辑"，但要让用户知道。
            Log.Info("密码库：锁定前落盘失败：" + ex.Message);
        }

        await _keyGate.WaitAsync(CancellationToken.None).ConfigureAwait(true);
        try
        {
            LockCore();
        }
        finally
        {
            _keyGate.Release();
        }
    }

    private void LockCore()
    {
        if (_data == null && _key == null)
        {
            return;
        }

        _data = null;
        _key?.Dispose();
        _key = null;

        _idleTimer.Stop();

        // 阻断态是"文件本身的属性"，不随锁定消失；否则锁一次之后阻断卡就没了。
        _state = _block.Kind != VaultBlockKind.None
            ? VaultState.Blocked
            : Exists ? VaultState.Locked : VaultState.Missing;

        Locked?.Invoke();
    }

    // ---------------- 保存管线（T07） ----------------

    /// <summary>UI 线程：克隆数据 + 换新 nonce。返回 null 表示当前不可保存。</summary>
    private VaultSnapshot? CaptureSnapshot()
    {
        if (_suspendCapture || _data == null || _key == null || _state != VaultState.Unlocked)
        {
            return null;
        }

        return new VaultSnapshot
        {
            Header = ReadHeader().WithFreshNonce(),
            Data = _data.CloneForBackground(),
        };
    }

    /// <summary>
    /// 后台：序列化 → 加密 → 落盘。这是唯一借用密钥的地方，与锁定/改密用 <see cref="_keyGate"/> 互斥。
    /// </summary>
    private async Task PersistSnapshotAsync(VaultSnapshot snapshot, CancellationToken ct)
    {
        await _keyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var key = _key ?? throw new InvalidOperationException("密码库已锁定，放弃本次保存。");

            var payload = VaultPayloadWriter.Write(snapshot.Data);
            try
            {
                var body = VaultCrypto.SealPayload(snapshot.Header, key, payload);
                var file = new byte[VaultFormat.HeaderLength + body.Length];
                snapshot.Header.ToBytes().CopyTo(file, 0);
                body.CopyTo(file, VaultFormat.HeaderLength);

                await VaultStore.SaveAsync(_vaultPath, file, ct).ConfigureAwait(false);

                // 写盘成功后才推进当前头：失败时内存里的 nonce 不该前进，
                // 否则重试会拿一个"从未写进磁盘"的头继续用。
                WriteHeader(snapshot.Header);
            }
            finally
            {
                // 明文 JSON 是本方法分配的，离开作用域前必须清零。
                CryptographicOperations.ZeroMemory(payload);
            }
        }
        finally
        {
            _keyGate.Release();
        }
    }

    private VaultHeader ReadHeader()
    {
        lock (_headerGate)
        {
            return _header;
        }
    }

    private void WriteHeader(VaultHeader header)
    {
        lock (_headerGate)
        {
            _header = header;
        }
    }

    public void Dispose()
    {
        // 退出前尽力把最后一次编辑落盘。
        // 这里不能 await FlushAsync（Dispose 在 UI 线程，捕获快照要回 UI 线程 ⇒ 死锁），
        // 所以直接同步写一次；写失败不阻断退出，但会记日志。
        try
        {
            Save();
        }
        catch (Exception ex)
        {
            Log.Info("密码库：退出前落盘失败：" + ex.Message);
        }

        Lock();
        _scheduler.Dispose();
        _idleTimer.Stop();
        if (App.SessionLockedSink is { } sink && _sessionLockedHandler != null)
        {
            sink.SessionLocked -= _sessionLockedHandler;
        }
    }

    /// <summary>内部校验：主密码长度与创建/修改界面的要求一致。强度提示由 UI 用熵估计给出。</summary>
    private static void ValidateMasterPassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < VaultKdfPolicy.MasterPasswordMinLength)
        {
            throw new ArgumentException(VaultMessages.MasterPasswordTooShort);
        }
    }

    /// <summary>接管一次成功的解锁：换密钥、设状态、起空闲计时、通知 UI。</summary>
    private void Adopt(VaultHeader header, VaultData data, VaultKey key)
    {
        _key?.Dispose();
        _key = key;
        WriteHeader(header);
        _data = data;
        _state = VaultState.Unlocked;
        _block = VaultBlockInfo.None;
        _lastActivity = DateTimeOffset.UtcNow;

        // 回收站淘汰放在解锁后做一次：这是明确的"会话开始"时机，
        // 而不是每次 Normalize 都扫——打开一份旧备份不该悄悄删掉里面的东西。
        VaultRecycleBinService.Sweep(data, DateTimeOffset.UtcNow);

        StartIdleTimer();
        Unlocked?.Invoke();
    }

    /// <summary>把"读+分类"的结果落到会话状态，并在需要时生成损坏备份。返回非空表示本次无法解锁。</summary>
    private VaultUnlockOutcome? ApplyClassification(VaultFileRead read, bool createCorruptBackup)
    {
        // 文件不存在不是"阻断"：UI 必须走创建流程，而不是给用户看一张"打不开"的卡。
        if (read.Kind == VaultFileKind.Missing && !read.IoFailed)
        {
            _state = VaultState.Missing;
            _block = VaultBlockInfo.None;
            return new VaultUnlockOutcome(
                false, VaultOpenStatus.UnknownFormat, VaultMessages.UnlockFailedTitle, VaultMessages.VaultMissing, null,
                VaultBlockKind.None);
        }

        var precheck = VaultOpenPipeline.Precheck(read);
        if (precheck is not { } result)
        {
            _state = VaultState.Locked;
            _block = VaultBlockInfo.None;
            return null;
        }

        var bytes = read.Length;
        switch (result.Status)
        {
            case VaultOpenStatus.LegacyFormat:
                // 开发期决策：密码库尚未发布，v1 时代只有开发者自测数据，没有真实用户。
                // 因此不做迁移、不做阻断——直接当作"还没有库"，让用户走创建流程。
                // 原文件在 CreateAsync 里改名留档（.v1-orphan-<时间戳>），不删除：
                // "不销毁"比"覆盖"只贵一次 File.Move，但省掉整类"手滑丢数据"的事故。
                _state = VaultState.Missing;
                _block = VaultBlockInfo.None;
                Log.Info($"密码库：检测到 v1 旧库，按未初始化处理（创建时改名留档）。路径={_vaultPath}，字节={bytes}");
                return null;

            case VaultOpenStatus.NewerFormat:
                _state = VaultState.Blocked;
                _block = new VaultBlockInfo(
                    VaultBlockKind.NewerVersion, _vaultPath, bytes, VaultMessages.BlockNewerBody(_vaultPath));
                return new VaultUnlockOutcome(
                    false, result.Status, VaultMessages.BlockNewerTitle, VaultMessages.BlockNewerBody(_vaultPath), null,
                    VaultBlockKind.NewerVersion);

            case VaultOpenStatus.UnknownFormat:
            case VaultOpenStatus.StructureInvalid:
            {
                var kind = result.Status == VaultOpenStatus.UnknownFormat
                    ? VaultBlockKind.UnknownFile
                    : VaultBlockKind.IntegrityFailure;
                if (createCorruptBackup)
                {
                    // 只有"不可解析的字节垃圾"才值得留证据；认证失败与 v1/更新版本都不在这里。
                    VaultStore.TryCreateCorruptBackup(_vaultPath, read.Kind);
                }

                var body = BuildIntegrityBody(kind);
                _state = VaultState.Blocked;
                _block = new VaultBlockInfo(kind, _vaultPath, bytes, body);
                return new VaultUnlockOutcome(
                    false, result.Status, BuildIntegrityTitle(kind), body, null, kind);
            }

            case VaultOpenStatus.IoFailed:
                // 读取异常不阻断：可能只是文件被占用，用户关掉占用程序就能重试。
                _state = VaultState.Locked;
                _block = VaultBlockInfo.None;
                return new VaultUnlockOutcome(
                    false, result.Status, VaultMessages.UnlockFailedTitle, VaultMessages.VaultReadFailed(result.Detail), null,
                    VaultBlockKind.None);

            default:
                _state = VaultState.Locked;
                _block = VaultBlockInfo.None;
                return null;
        }
    }

    private static string BuildIntegrityTitle(VaultBlockKind kind)
        => kind == VaultBlockKind.UnknownFile ? VaultMessages.BlockUnknownTitle : VaultMessages.BlockIntegrityTitle;

    private string BuildIntegrityBody(VaultBlockKind kind)
        => kind == VaultBlockKind.UnknownFile
            ? VaultMessages.BlockUnknownBody(_vaultPath)
            : VaultMessages.BlockIntegrityBody(_vaultPath);

    /// <summary>只刷新会话状态（构造时与锁定后使用），不返回文案。</summary>
    private void ClassifyCurrentFile(bool createCorruptBackup)
    {
        _data = null;
        var read = VaultStore.Read(_vaultPath);
        ApplyClassification(read, createCorruptBackup);
    }

    /// <summary>
    /// 把解密失败映射成用户可见的三层文案。
    /// 关键区分：<see cref="VaultOpenStatus.AuthenticationFailed"/> **不进入阻断态**——
    /// tag 失败可能只是一次手滑，阻断卡会剥夺用户"改个密码再试"的权利。
    /// </summary>
    private VaultUnlockOutcome MapFailure(VaultOpenResult result)
    {
        if (result.Status == VaultOpenStatus.PayloadTooNew)
        {
            return new VaultUnlockOutcome(
                false, result.Status, VaultMessages.BlockNewerTitle, VaultMessages.BlockNewerBody(_vaultPath), null,
                VaultBlockKind.NewerVersion);
        }

        if (result.Status == VaultOpenStatus.StructureInvalid)
        {
            return new VaultUnlockOutcome(
                false, result.Status, VaultMessages.BlockIntegrityTitle, VaultMessages.BlockIntegrityBody(_vaultPath), null,
                VaultBlockKind.IntegrityFailure);
        }

        return new VaultUnlockOutcome(
            false,
            result.Status,
            VaultMessages.UnlockFailedTitle,
            VaultMessages.UnlockFailedBody,
            VaultMessages.UnlockFailedHint,
            VaultBlockKind.None);
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
}
