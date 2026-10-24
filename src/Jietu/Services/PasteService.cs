namespace Jietu;

public readonly record struct PasteTarget(IntPtr Window, IntPtr Focus)
{
    public static PasteTarget Capture()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var thread = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var info = new NativeMethods.GuiThreadInfo { CbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.GuiThreadInfo>() };
        var active = foreground;
        var focus = foreground;
        if (thread != 0 && NativeMethods.GetGUIThreadInfo(thread, ref info))
        {
            if (info.HwndActive != IntPtr.Zero)
            {
                active = info.HwndActive;
            }

            if (info.HwndFocus != IntPtr.Zero)
            {
                focus = info.HwndFocus;
            }
        }

        return new PasteTarget(active, focus);
    }
}

internal static class PasteService
{
    public static async Task<bool> TryPasteAsync(PasteTarget target)
    {
        var window = target.Window;
        if (window == IntPtr.Zero || !NativeMethods.IsWindow(window))
        {
            Log.Info("原来的窗口已经不在。");
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(window, out var pid);
        if (pid == (uint)Environment.ProcessId)
        {
            Log.Info("粘贴目标仍是本程序，改为手动粘贴。");
            return false;
        }

        NativeMethods.AllowSetForegroundWindow(-1);

        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundThread = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var targetThread = NativeMethods.GetWindowThreadProcessId(window, out _);
        var thisThread = NativeMethods.GetCurrentThreadId();
        var attachedForeground = false;
        var attachedTarget = false;
        if (foregroundThread != 0 && foregroundThread != thisThread)
        {
            attachedForeground = NativeMethods.AttachThreadInput(thisThread, foregroundThread, true);
        }

        if (targetThread != 0 && targetThread != thisThread && targetThread != foregroundThread)
        {
            attachedTarget = NativeMethods.AttachThreadInput(thisThread, targetThread, true);
        }

        try
        {
            if (NativeMethods.IsIconic(window))
            {
                NativeMethods.ShowWindow(window, NativeMethods.SwRestore);
            }

            NativeMethods.BringWindowToTop(window);
            NativeMethods.SetForegroundWindow(window);
            if (target.Focus != IntPtr.Zero && NativeMethods.IsWindow(target.Focus))
            {
                NativeMethods.SetFocus(target.Focus);
            }

            var restored = false;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                if (IsTargetForeground(window))
                {
                    restored = true;
                    break;
                }

                NativeMethods.SetForegroundWindow(window);
                if (attempt == 2)
                {
                    NativeMethods.keybd_event(0x12, 0, 0, 0);
                    NativeMethods.keybd_event(0x12, 0, 2, 0);
                }

                await Task.Delay(30);
            }

            if (!restored)
            {
                Log.Info("无法回到上一个窗口。");
                return false;
            }
        }
        finally
        {
            if (attachedForeground)
            {
                NativeMethods.AttachThreadInput(thisThread, foregroundThread, false);
            }

            if (attachedTarget)
            {
                NativeMethods.AttachThreadInput(thisThread, targetThread, false);
            }
        }

        await Task.Delay(40);
        if (!IsTargetForeground(window))
        {
            NativeMethods.SetForegroundWindow(window);
            await Task.Delay(30);
        }

        if (!IsTargetForeground(window))
        {
            Log.Info("焦点没有留在目标窗口。");
            return false;
        }

        var sent = SendCtrlV();
        if (!sent)
        {
            Log.Info("发送 Ctrl+V 失败。");
        }

        return sent;
    }

    private static bool IsTargetForeground(IntPtr window)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        return foreground == window || RootOf(foreground) == RootOf(window);
    }

    private static IntPtr RootOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var root = NativeMethods.GetAncestor(hwnd, NativeMethods.GaRoot);
        return root == IntPtr.Zero ? hwnd : root;
    }

    private static bool SendCtrlV()
    {
        var inputs = new NativeMethods.Input[4];
        inputs[0] = Key(0x11, false);
        inputs[1] = Key(0x56, false);
        inputs[2] = Key(0x56, true);
        inputs[3] = Key(0x11, true);
        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.Input>());
        return sent == inputs.Length;
    }

    private static NativeMethods.Input Key(ushort virtualKey, bool up)
    {
        return new NativeMethods.Input
        {
            Type = 1,
            U = new NativeMethods.InputUnion
            {
                Ki = new NativeMethods.KeybdInput
                {
                    WVk = virtualKey,
                    WScan = (ushort)NativeMethods.MapVirtualKey(virtualKey, 0),
                    DwFlags = up ? 2u : 0u,
                },
            },
        };
    }
}
