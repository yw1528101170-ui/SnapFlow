using Microsoft.UI.Dispatching;

namespace Jietu;

/// <summary>
/// 一次"待落盘"的快照。<see cref="Data"/> 是 UI 线程克隆出的副本，
/// <see cref="Header"/> 已换好新 nonce 与 cipherLength——后台只负责序列化、加密、写盘。
/// </summary>
public sealed class VaultSnapshot
{
    public required VaultHeader Header { get; init; }

    public required VaultData Data { get; init; }

    /// <summary>捕获快照时的编辑版本号，用于判断"这一轮之后是否又有新编辑"。</summary>
    public long DataVersion { get; init; }
}

public enum VaultSaveState
{
    Idle,
    Saving,
    Saved,
    Failed,
}

/// <summary>
/// 异步保存调度器：防抖合并 + 单飞 + 失败回退。
///
/// 三条设计要点（都是踩过坑的地方）：
/// ① **UI 线程只做克隆**。序列化与加密全部在后台——5000 条时 JSON 序列化可达 40~120ms，
///    放在 UI 线程会直接掉帧；但如果为了"不卡"把序列化切碎在 UI 线程做，
///    切碎期间用户继续编辑会让快照半新半旧（撕裂），正确性风险远大于抖动。
/// ② **单飞**：同一时刻只允许一个写盘任务。tmp 文件名固定，并发写必然互相踩。
/// ③ **失败不清 dirty**：写失败时保留版本号，下一次 <see cref="Request"/> 自动重试，
///    磁盘上始终留着上一版可用文件。
///
/// 线程亲和：<see cref="Request"/> / <see cref="FlushAsync"/> 可在任意线程调用；
/// 捕获快照与 <see cref="StateChanged"/> 会被编组回 UI 线程（<paramref name="ui"/> 为 null 时同步执行，供单测）。
/// </summary>
public sealed class VaultSaveScheduler : IDisposable
{
    private readonly DispatcherQueue? _ui;
    private readonly Func<VaultSnapshot?> _captureOnUi;
    private readonly Func<VaultSnapshot, CancellationToken, Task> _persist;
    private readonly Timer _debounce;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _gate = new();

    private long _dirtyVersion;
    private long _savedVersion;
    private int _inFlight;
    private long _flushTarget;

    private TaskCompletionSource<bool>? _flushTcs;
    private VaultSaveState _state = VaultSaveState.Idle;
    private volatile bool _disposed;

    public VaultSaveScheduler(
        DispatcherQueue? ui,
        Func<VaultSnapshot?> captureOnUi,
        Func<VaultSnapshot, CancellationToken, Task> persist,
        int debounceMs = 400)
    {
        _ui = ui;
        _captureOnUi = captureOnUi;
        _persist = persist;
        DebounceMs = debounceMs;
        _debounce = new Timer(_ => Pump(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public int DebounceMs { get; }

    /// <summary>当前保存状态（可在任意线程读）。</summary>
    public VaultSaveState State => _state;

    public DateTimeOffset? LastSavedUtc { get; private set; }

    /// <summary>最近一次失败的用户可见原因；成功时清空。</summary>
    public string? LastError { get; private set; }

    /// <summary>状态变化（已编组到 UI 线程）。</summary>
    public event Action<VaultSaveState, string?>? StateChanged;

    /// <summary>
    /// 记一次编辑并重置防抖窗口。O(1) 立即返回，调用方（UI 线程）不会被阻塞。
    /// </summary>
    public void Request()
    {
        if (_disposed)
        {
            return;
        }

        Interlocked.Increment(ref _dirtyVersion);
        try
        {
            _debounce.Change(DebounceMs, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // 退出竞态：停止后到来的编辑直接忽略，不抛给 UI。
        }
    }

    /// <summary>
    /// 等待"到目前为止的所有编辑"落盘。用于锁定、退出、导入/恢复之后。
    /// 返回 false 表示等待期间写盘失败（此时磁盘上是上一版，调用方应提示用户）。
    /// </summary>
    public async Task<bool> FlushAsync(CancellationToken ct = default)
    {
        if (_disposed)
        {
            return false;
        }

        var target = Interlocked.Read(ref _dirtyVersion);
        if (target <= Interlocked.Read(ref _savedVersion) && Volatile.Read(ref _inFlight) == 0)
        {
            return true;
        }

        Task<bool> wait;
        lock (_gate)
        {
            _flushTarget = Math.Max(_flushTarget, target);
            _flushTcs ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            wait = _flushTcs.Task;
        }

        // 不等防抖窗口：Flush 的语义就是"立刻落盘"。
        try
        {
            _debounce.Change(Timeout.Infinite, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        Pump();

        return ct.CanBeCanceled ? await wait.WaitAsync(ct).ConfigureAwait(false) : await wait.ConfigureAwait(false);
    }

    /// <summary>停止接受新的保存请求，并等待在飞的写盘结束。</summary>
    public async Task StopAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _debounce.Change(Timeout.Infinite, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
        }

        // 在飞的写盘不取消：写到一半中断只会留下 tmp，比写完更危险。
        // 这里只等它自然结束（pump 的 finally 会释放单飞标记）。
        for (var i = 0; i < 60 && Volatile.Read(ref _inFlight) != 0; i++)
        {
            await Task.Delay(50).ConfigureAwait(false);
        }

        _cts.Cancel();
    }

    public void Dispose()
    {
        _disposed = true;
        try
        {
            _debounce.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        _cts.Dispose();
    }

    /// <summary>单飞泵：同一时刻只有一个实例在跑，循环直到版本号稳定。</summary>
    private async void Pump()
    {
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
        {
            // 已有任务在写；它的循环会看到更新后的 dirtyVersion 并继续，不需要再起一个。
            return;
        }

        try
        {
            while (!_cts.IsCancellationRequested && !_disposed)
            {
                var version = Interlocked.Read(ref _dirtyVersion);
                if (version <= Interlocked.Read(ref _savedVersion))
                {
                    break;
                }

                SetState(VaultSaveState.Saving, null);

                VaultSnapshot? snapshot;
                try
                {
                    snapshot = await CaptureAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Error(ex);
                    LastError = VaultMessages.SaveFailed;
                    SetState(VaultSaveState.Failed, LastError);
                    break;
                }

                // null = 当前不可保存（库已锁定/阻断/未解锁）：不算失败，直接收手。
                if (snapshot == null)
                {
                    break;
                }

                try
                {
                    await _persist(snapshot, _cts.Token).ConfigureAwait(false);
                    Interlocked.Exchange(ref _savedVersion, version);
                    LastSavedUtc = DateTimeOffset.UtcNow;
                    LastError = null;
                    SetState(VaultSaveState.Saved, null);
                }
                catch (Exception ex)
                {
                    // 失败时**不推进 savedVersion**：下一次 Request 会自动重试，
                    // 而磁盘上仍然是上一次成功的完整文件（VaultStore 的 tmp+move 保证）。
                    Log.Error(ex);
                    LastError = VaultMessages.SaveFailed;
                    SetState(VaultSaveState.Failed, LastError);
                    break;
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _inFlight, 0);
            CompleteFlushIfSettled();
        }
    }

    /// <summary>把"克隆快照"编组回 UI 线程；<see cref="_ui"/> 为 null 时同步执行（单测）。</summary>
    private Task<VaultSnapshot?> CaptureAsync()
    {
        if (_ui == null)
        {
            return Task.FromResult(_captureOnUi());
        }

        var tcs = new TaskCompletionSource<VaultSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_ui.TryEnqueue(() =>
            {
                try
                {
                    tcs.TrySetResult(_captureOnUi());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }))
        {
            // UI 队列已关闭（退出中）：当作"不可保存"。
            tcs.TrySetResult(null);
        }

        return tcs.Task;
    }

    private void CompleteFlushIfSettled()
    {
        lock (_gate)
        {
            if (_flushTcs == null)
            {
                return;
            }

            if (Volatile.Read(ref _inFlight) != 0)
            {
                return;
            }

            var settled = Interlocked.Read(ref _savedVersion) >= _flushTarget;
            _flushTcs.TrySetResult(settled);
            _flushTcs = null;
        }
    }

    private void SetState(VaultSaveState state, string? message)
    {
        _state = state;
        var handler = StateChanged;
        if (handler == null)
        {
            return;
        }

        if (_ui == null)
        {
            handler(state, message);
            return;
        }

        if (!_ui.TryEnqueue(() => handler(state, message)))
        {
            // UI 已关闭：没人需要这个通知了。
        }
    }
}
