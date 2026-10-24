namespace Jietu;

internal readonly record struct WindowBounds(IntPtr Hwnd, int X, int Y, int Width, int Height);

internal readonly record struct ElementStack(IntPtr Root, WindowBounds[] Levels);

internal static class WindowPicker
{
    private static readonly object Gate = new();
    private static List<WindowBounds> _windows = [];
    private static long _updated;
    private static int _refreshing;
    private static IntPtr _cachedParentHwnd;
    private static long _cachedChildrenTime;
    private static List<WindowBounds> _cachedChildren = [];
    private static readonly object ChildGate = new();

    public static void Refresh()
    {
        var list = Collect();
        lock (Gate)
        {
            _windows = list;
            _updated = Environment.TickCount64;
        }

        lock (ChildGate)
        {
            _cachedParentHwnd = IntPtr.Zero;
            _cachedChildren = [];
        }
    }

    public static WindowBounds? Find(int screenX, int screenY)
    {
        List<WindowBounds> snapshot;
        long updated;
        lock (Gate)
        {
            snapshot = _windows;
            updated = _updated;
        }

        if (updated == 0 || Environment.TickCount64 - updated > 150)
        {
            Kick();
        }

        foreach (var rect in snapshot)
        {
            if (screenX >= rect.X && screenY >= rect.Y && screenX < rect.X + rect.Width && screenY < rect.Y + rect.Height)
            {
                return rect;
            }
        }

        return null;
    }

    public static ElementStack Chain(int screenX, int screenY)
    {
        WindowBounds? top = null;
        List<WindowBounds> snapshot;
        lock (Gate)
        {
            snapshot = _windows;
        }

        foreach (var rect in snapshot)
        {
            if (screenX >= rect.X && screenY >= rect.Y && screenX < rect.X + rect.Width && screenY < rect.Y + rect.Height)
            {
                top = rect;
                break;
            }
        }

        if (top == null)
        {
            return new ElementStack(IntPtr.Zero, []);
        }

        var parent = top.Value;
        var levels = new List<WindowBounds>();
        long now = Environment.TickCount64;
        List<WindowBounds> children;
        lock (ChildGate)
        {
            if (_cachedParentHwnd == parent.Hwnd && now - _cachedChildrenTime < 500)
            {
                children = _cachedChildren;
            }
            else
            {
                children = QueryAllChildren(parent.Hwnd);
                _cachedParentHwnd = parent.Hwnd;
                _cachedChildren = children;
                _cachedChildrenTime = now;
            }
        }

        foreach (var bounds in children)
        {
            if (screenX >= bounds.X && screenY >= bounds.Y && screenX < bounds.X + bounds.Width && screenY < bounds.Y + bounds.Height)
            {
                levels.Add(bounds);
            }
        }

        levels.Sort(static (left, right) => (left.Width * left.Height).CompareTo(right.Width * right.Height));
        if (!levels.Exists(item => Near(item, parent)))
        {
            levels.Add(parent);
        }

        return new ElementStack(parent.Hwnd, levels.ToArray());
    }

    private static List<WindowBounds> QueryAllChildren(IntPtr parent)
    {
        var result = new List<WindowBounds>();
        NativeMethods.EnumChildWindows(parent, (hwnd, _) =>
        {
            if (hwnd == parent || !NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
            {
                return true;
            }

            var cloaked = NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DwmwaCloaked, out int hidden, sizeof(int));
            if (cloaked == 0 && hidden != 0)
            {
                return true;
            }

            if (!NativeMethods.GetWindowRect(hwnd, out var rect) || rect.Width < 8 || rect.Height < 8)
            {
                return true;
            }

            var bounds = new WindowBounds(hwnd, rect.Left, rect.Top, rect.Width, rect.Height);
            if (!result.Exists(item => Near(item, bounds)))
            {
                result.Add(bounds);
            }

            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static bool Near(WindowBounds left, WindowBounds right)
    {
        return Math.Abs(left.X - right.X) < 6
            && Math.Abs(left.Y - right.Y) < 6
            && Math.Abs(left.Width - right.Width) < 6
            && Math.Abs(left.Height - right.Height) < 6;
    }

    private static void Kick()
    {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                Refresh();
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }
        });
    }

    private static List<WindowBounds> Collect()
    {
        var handles = new List<IntPtr>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            handles.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        var windows = new List<WindowBounds>(handles.Count);
        var ourPid = (uint)Environment.ProcessId;
        foreach (var hwnd in handles)
        {
            if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
            {
                continue;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || pid == ourPid)
            {
                continue;
            }

            if (IsCloaked(hwnd) || !TryGetBounds(hwnd, out var rect))
            {
                continue;
            }

            if (rect.Width < 8 || rect.Height < 8)
            {
                continue;
            }

            windows.Add(new WindowBounds(hwnd, rect.Left, rect.Top, rect.Width, rect.Height));
        }

        return windows;
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        var hr = NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DwmwaCloaked, out int cloaked, sizeof(int));
        return hr == 0 && cloaked != 0;
    }

    private static bool TryGetBounds(IntPtr hwnd, out NativeMethods.Rect rect)
    {
        var hr = NativeMethods.DwmGetWindowAttribute(
            hwnd,
            NativeMethods.DwmwaExtendedFrameBounds,
            out rect,
            System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.Rect>());
        if (hr == 0 && rect.Width > 0 && rect.Height > 0)
        {
            return true;
        }

        return NativeMethods.GetWindowRect(hwnd, out rect) && rect.Width > 0 && rect.Height > 0;
    }
}
