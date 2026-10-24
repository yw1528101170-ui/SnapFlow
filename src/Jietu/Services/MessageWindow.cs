namespace Jietu;

internal sealed class MessageWindow
{
    private readonly NativeMethods.WndProc _proc;
    private readonly Action<int> _hotkey;
    private readonly Action _clipboard;
    private readonly Action<uint> _tray;
    // 系统分配的 "TaskbarCreated" 广播消息 id；注册失败时为 0，此时不会触发事件。
    private readonly uint _taskbarCreated;

    /// <summary>Explorer 崩溃/重启后任务栏重建时触发，订阅方需要按需重新添加托盘图标。</summary>
    public event Action? TaskbarCreated;

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
    }

    public IntPtr Handle { get; }

    public void Destroy()
    {
        if (Handle != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(Handle);
        }
    }

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
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }

        return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
