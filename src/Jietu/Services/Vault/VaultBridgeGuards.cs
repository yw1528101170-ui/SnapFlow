namespace Jietu;

/// <summary>审计条目：只记 host 与结果，**不记用户名、不记口令、不落盘**。</summary>
public sealed record VaultAuditRecord(DateTimeOffset AtUtc, string Host, VaultAuditResult Result, int Count);

public enum VaultAuditResult
{
    Ok,
    NotPaired,
    VaultLocked,
    RateLimited,
    IntegrationDisabled,
    Blocked,
}

/// <summary>
/// 配对槽位：同一时刻只允许一个待确认请求。
///
/// 为什么单独抽出来：原实现是 VaultBridge 上的一个字段，多个客户端连接（每个一个线程）
/// 同时判断 null 再赋值，存在"两个请求都认为自己抢到了"的竞态。这里把
/// 判断 + 占用放进同一个临界区，迟到者立刻收到 pair-busy，不会覆盖正在等待的那个。
/// </summary>
public sealed class VaultPairingGate
{
    public sealed record PendingPair(
        string PairId,
        string DisplayName,
        string TokenHash,
        TaskCompletionSource<bool> Completion);

    private readonly Lock _gate = new();
    private PendingPair? _current;

    public PendingPair? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>尝试占用槽位。返回 false 表示已有请求在等待用户确认。</summary>
    public bool TryBegin(PendingPair pair)
    {
        lock (_gate)
        {
            if (_current != null)
            {
                return false;
            }

            _current = pair;
            return true;
        }
    }

    /// <summary>提交结果并释放槽位。返回 false 表示 pairId 已过期（超时后被别人顶替）。</summary>
    public bool Complete(string pairId, bool accept)
    {
        PendingPair? pair;
        lock (_gate)
        {
            if (_current?.PairId != pairId)
            {
                return false;
            }

            pair = _current;
            _current = null;
        }

        // 在临界区外完成：避免延续同步执行订阅方的代码时又回头进这个锁。
        pair.Completion.TrySetResult(accept);
        return true;
    }

    /// <summary>超时清理：只清掉自己那一个，不能误清已经被新请求占用的槽位。</summary>
    public void Abandon(string pairId)
    {
        lock (_gate)
        {
            if (_current?.PairId == pairId)
            {
                _current = null;
            }
        }
    }
}

/// <summary>
/// 桥接限流：滑动窗口 <c>perMinute</c> 次，超限后暂停 <c>pauseSeconds</c> 秒。
///
/// 目的不是防"外人"（管道 ACL 已限当前用户），而是防**同用户下的异常程序**：
/// 一个死循环重连的扩展、或者用户误操作导致的高频请求，不该把主程序的 CPU 打满。
/// 超限返回 retryAfter，让扩展侧能给出"已暂停 N 秒"而不是静默失败。
/// </summary>
public sealed class VaultRateLimiter
{
    private readonly int _capacity;
    private readonly TimeSpan _window;
    private readonly TimeSpan _pause;
    private readonly Lock _gate = new();
    private readonly Queue<DateTimeOffset> _hits = new();
    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;

    public VaultRateLimiter(int perMinute = 60, int pauseSeconds = 60)
    {
        _capacity = Math.Max(1, perMinute);
        _window = TimeSpan.FromMinutes(1);
        _pause = TimeSpan.FromSeconds(Math.Max(1, pauseSeconds));
    }

    public int Capacity => _capacity;

    /// <summary>取一个令牌。被拒时 <paramref name="retryAfterSeconds"/> 为建议重试等待秒数。</summary>
    public bool TryAcquire(out int retryAfterSeconds)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;

            if (now < _pausedUntil)
            {
                retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((_pausedUntil - now).TotalSeconds));
                return false;
            }

            while (_hits.Count > 0 && now - _hits.Peek() > _window)
            {
                _hits.Dequeue();
            }

            if (_hits.Count >= _capacity)
            {
                // 首次撞线就进入冷却：比"每来一次拒一次"更容易让扩展侧停止重试。
                _pausedUntil = now + _pause;
                retryAfterSeconds = (int)_pause.TotalSeconds;
                return false;
            }

            _hits.Enqueue(now);
            retryAfterSeconds = 0;
            return true;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _hits.Clear();
            _pausedUntil = DateTimeOffset.MinValue;
        }
    }
}

/// <summary>
/// 桥接审计：内存环形缓冲，最近 <c>capacity</c> 条。
///
/// 刻意**不落盘**：审计的目的是"用户能在设置页看到刚才有哪些站点被取过凭据"，
/// 落盘会额外制造一份可被取证的使用痕迹。也不记用户名与口令——记录本身不能成为泄漏源。
/// </summary>
public sealed class VaultAuditLog
{
    private readonly int _capacity;
    private readonly Queue<VaultAuditRecord> _records = new();
    private readonly Lock _gate = new();

    public VaultAuditLog(int capacity = 200)
    {
        _capacity = Math.Max(1, capacity);
    }

    public void Record(string host, VaultAuditResult result, int count = 0)
    {
        lock (_gate)
        {
            _records.Enqueue(new VaultAuditRecord(DateTimeOffset.Now, host ?? "", result, count));
            while (_records.Count > _capacity)
            {
                _records.Dequeue();
            }
        }
    }

    /// <summary>按时间正序快照（最新的在最后）。</summary>
    public IReadOnlyList<VaultAuditRecord> Snapshot()
    {
        lock (_gate)
        {
            return _records.ToArray();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _records.Clear();
        }
    }
}
