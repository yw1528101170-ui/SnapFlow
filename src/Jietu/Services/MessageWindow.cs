namespace Jietu;

internal sealed class MessageWindow
{
    private const uint WmWtsSessionChange = 0x02B1;
    private const nint WtsSessionLock = 0x7;
    private const uint WmPowerBroadcast = 0x0218;
    private const nint PbtApmSuspend = 0x4;

    private readonly NativeMethods.WndProc _proc;
    private readonly Action<int> _hotkey;
    private readonly Action _clipboard;
    private readonly Action<uint> _tray;
    // 系统分配的 "TaskbarCreated" 广播消息 id；注册失败时为 0，此时不会触发事件。
    private readonly uint _taskbarCreated;

    /// <summary>Explorer 崩溃/重启后任务栏重建时触发，订阅方需要按需重新添加托盘图标。</summary>
    public event Action? TaskbarCreated;

    /// <summary>Windows 会话锁定（Win+L）或系统进入睡眠时触发，密码库据此立即锁定。</summary>
    public event Action? SessionLocked;

    public MessageWindow(Action<int> hotkey, Action clipboard, Action<uint> tray)
    {
        _hotkey = hotkey;
        _clipboard = clipboard;
        _tray = tray;
        _taskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        _proc = WndProc;
        var windowClass = new NativeMethods.WndClass
        {
            LpfnWndProc = _proc,
            HInstance = NativeMethods.GetModuleHandle(null),
            LpszClassName = "SnapFlow.MessageWindow",
        };
        var atom = NativeMethods.RegisterClass(ref windowClass);
        if (atom == 0 && System.Runtime.InteropServices.Marshal.GetLastWin32Error() != 1410)
        {
            throw new InvalidOperationException("无法注册消息窗口。");
        }

        Handle = NativeMethods.CreateWindowEx(
            0,
            windowClass.LpszClassName,
            "SnapFlow",
            0,
            0, 0, 0, 0,
            IntPtr.Zero,
            IntPtr.Zero,
            windowClass.HInstance,
            IntPtr.Zero);
        if (Handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("无法创建消息窗口。");
        }

        try
        {
            NativeMethods.AllowDarkModeForWindow(Handle, true);
        }
        catch
        {
        }

        // 订阅会话锁定/解锁通知（不订阅 unlock）；失败只影响「锁屏立即锁」这一体验项。
        if (!WtsRegisterSessionNotification(Handle, 0 /*NOTIFY_FOR_THIS_SESSION*/))
        {
            Log.Info("注册会话锁定通知失败，锁屏时密码库将依赖空闲超时锁定。");
        }
    }

    public IntPtr Handle { get; }

    public void Destroy()
    {
        if (Handle != IntPtr.Zero)
        {
            WtsUnRegisterSessionNotification(Handle);
            NativeMethods.DestroyWindow(Handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("wtsapi32.dll")]
    private static extern bool WtsRegisterSessionNotification(IntPtr hWnd, uint dwFlags);

    [System.Runtime.InteropServices.DllImport("wtsapi32.dll")]
    private static extern bool WtsUnRegisterSessionNotification(IntPtr hWnd);

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == NativeMethods.WmHotkey)
            {
                _hotkey(wParam.ToInt32());
            }
            else if (msg == NativeMethods.WmClipboardUpdate)
            {
                _clipboard();
            }
            else if (msg == NativeMethods.TrayCallback)
            {
                _tray((uint)lParam);
            }
            else if (msg == _taskbarCreated && _taskbarCreated != 0)
            {
                // Explorer 重启后广播该消息，必须重新 NimAdd，否则托盘图标永久消失。
                TaskbarCreated?.Invoke();
            }
            else if (msg == WmWtsSessionChange && wParam == WtsSessionLock)
            {
                SessionLocked?.Invoke();
            }
            else if (msg == WmPowerBroadcast && wParam == PbtApmSuspend)
            {
                SessionLocked?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }

        return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
