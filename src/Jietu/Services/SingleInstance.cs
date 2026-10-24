namespace Jietu;

internal static class SingleInstance
{
    private const string MutexName = @"Local\SnapFlow.SingleInstance";
    private const string EventName = @"Local\SnapFlow.Activate";
    private static Mutex? _mutex;
    private static EventWaitHandle? _event;
    private static Thread? _thread;
    private static volatile bool _stop;

    public static bool Acquire()
    {
        _stop = false;
        _mutex = new Mutex(false, MutexName);
        try
        {
            if (!_mutex.WaitOne(0))
            {
                return false;
            }
        }
        catch (AbandonedMutexException)
        {
            // 上次进程异常退出，这次接管。
        }

        _event = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        return true;
    }

    public static void SignalExisting()
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(EventName);
            handle.Set();
        }
        catch (Exception ex)
        {
            Log.Info("通知已运行的实例失败：" + ex.Message);
        }
    }

    public static void Listen(Action callback)
    {
        _thread = new Thread(() =>
        {
            while (!_stop)
            {
                // 先把句柄取到本地：Stop 会把字段置 null，避免“判空后字段被清空”的空引用竞态。
                var handle = _event;
                if (handle == null)
                {
                    break;
                }

                try
                {
                    if (handle.WaitOne(500) && !_stop)
                    {
                        callback();
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Stop 已经释放事件句柄，监听线程安静退出，不能再往上抛。
                    break;
                }
                catch (Exception ex)
                {
                    // 回调异常不能带走监听线程（否则进程直接退出）。
                    Log.Error(ex);
                }
            }
        })
        {
            IsBackground = true,
            Name = "SnapFlow.SingleInstance",
        };
        _thread.Start();
    }

    /// <summary>停止单实例监听并释放句柄；可重复调用，重复调用只做空操作。</summary>
    public static void Stop()
    {
        _stop = true;

        // 先把字段全部摘下来，后续无论哪一步失败，重复调用 Stop 都不会再碰到已释放的句柄。
        var thread = _thread;
        var handle = _event;
        var mutex = _mutex;
        _thread = null;
        _event = null;
        _mutex = null;

        try
        {
            // 唤醒监听线程，让它从 WaitOne(500) 立刻返回并看到 _stop。
            handle?.Set();
        }
        catch (Exception ex)
        {
            Log.Info("通知单实例监听线程退出失败：" + ex.Message);
        }

        try
        {
            // 当前就是监听线程时不能 Join 自己（托盘菜单退出会走到这条路径）。
            if (thread != null && thread != Thread.CurrentThread && !thread.Join(1000))
            {
                Log.Info("单实例监听线程未在 1 秒内退出。");
            }
        }
        catch (Exception ex)
        {
            Log.Info("等待单实例监听线程退出失败：" + ex.Message);
        }

        try
        {
            handle?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Info("释放单实例事件句柄失败：" + ex.Message);
        }

        try
        {
            // 未持有互斥量（例如 Acquire 失败后误调 Stop）时 ReleaseMutex 会抛 ApplicationException，这里忽略。
            mutex?.ReleaseMutex();
        }
        catch (Exception ex)
        {
            Log.Info("释放单实例互斥量失败：" + ex.Message);
        }

        try
        {
            mutex?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("释放单实例互斥量句柄失败：" + ex.Message);
        }
    }
}
