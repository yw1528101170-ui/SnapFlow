using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Jietu;

internal readonly record struct DisplayMonitor(IntPtr Handle, int X, int Y, int Width, int Height);

internal static class ScreenUtil
{
    public static DisplayMonitor FromCursor()
    {
        NativeMethods.GetCursorPos(out var point);
        var area = DisplayArea.GetFromPoint(new PointInt32(point.X, point.Y), DisplayAreaFallback.Nearest);
        var handle = Win32Interop.GetMonitorFromDisplayId(area.DisplayId);
        var bounds = area.OuterBounds;
        return new DisplayMonitor(handle, bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    public static IReadOnlyList<DisplayMonitor> Monitors()
    {
        var list = new List<DisplayMonitor>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, ref rect, _) =>
        {
            var info = new NativeMethods.MonitorInfo { CbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>() };
            if (NativeMethods.GetMonitorInfo(monitor, ref info) && info.RcMonitor.Width > 0 && info.RcMonitor.Height > 0)
            {
                list.Add(new DisplayMonitor(monitor, info.RcMonitor.Left, info.RcMonitor.Top, info.RcMonitor.Width, info.RcMonitor.Height));
            }

            return true;
        }, IntPtr.Zero);
        return list.Count > 0 ? list : [FromCursor()];
    }

    public static DisplayMonitor Desktop()
    {
        var monitors = Monitors();
        var cursor = FromCursor();
        if (monitors.Count <= 1)
        {
            return cursor;
        }

        var left = monitors.Min(item => item.X);
        var top = monitors.Min(item => item.Y);
        var right = monitors.Max(item => item.X + item.Width);
        var bottom = monitors.Max(item => item.Y + item.Height);
        var width = right - left;
        var height = bottom - top;
        if (width <= 0 || height <= 0 || (long)width * height > 80_000_000)
        {
            return cursor;
        }

        return new DisplayMonitor(cursor.Handle, left, top, width, height);
    }

    public static DisplayMonitor ShotMonitor(string mode = "Auto")
    {
        if (string.Equals(mode, "ActiveMonitor", StringComparison.OrdinalIgnoreCase))
        {
            return FromCursor();
        }

        if (string.Equals(mode, "AllMonitors", StringComparison.OrdinalIgnoreCase))
        {
            return Desktop();
        }

        var monitors = Monitors();
        if (monitors.Count <= 1 || SameDpi(monitors))
        {
            return Desktop();
        }

        return FromCursor();
    }

    private static bool SameDpi(IReadOnlyList<DisplayMonitor> monitors)
    {
        uint? dpi = null;
        foreach (var monitor in monitors)
        {
            if (NativeMethods.GetDpiForMonitor(monitor.Handle, 0, out var dpiX, out _) != 0 || dpiX < 96)
            {
                return false;
            }

            if (dpi == null)
            {
                dpi = dpiX;
            }
            else if (dpi.Value != dpiX)
            {
                return false;
            }
        }

        return dpi != null;
    }

    public static RectInt32 PlaceNearCursor(IntPtr hwnd, int dipWidth, int dipHeight)
    {
        NativeMethods.GetCursorPos(out var point);
        return FitAt(hwnd, point.X, point.Y, dipWidth, dipHeight);
    }

    /// <summary>
    /// 以某个屏幕坐标（通常是窗口左上角）为锚点换尺寸：DIP → 物理像素，并夹进该显示器工作区。
    /// 用于锁定/解锁这类「同一窗口换布局」的场景——只改大小，不跟着鼠标跑。
    /// </summary>
    public static RectInt32 FitAt(IntPtr hwnd, int anchorX, int anchorY, int dipWidth, int dipHeight)
    {
        var area = DisplayArea.GetFromPoint(new PointInt32(anchorX, anchorY), DisplayAreaFallback.Nearest);
        var scale = MonitorScale(Win32Interop.GetMonitorFromDisplayId(area.DisplayId), hwnd);
        var work = area.WorkArea;
        var width = Math.Clamp((int)Math.Round(dipWidth * scale), 1, Math.Max(1, work.Width));
        var height = Math.Clamp((int)Math.Round(dipHeight * scale), 1, Math.Max(1, work.Height));

        var x = anchorX;
        var y = anchorY;
        if (x + width > work.X + work.Width)
        {
            x = work.X + work.Width - width;
        }

        if (y + height > work.Y + work.Height)
        {
            y = work.Y + work.Height - height;
        }

        if (x < work.X)
        {
            x = work.X;
        }

        if (y < work.Y)
        {
            y = work.Y;
        }

        return new RectInt32(x, y, width, height);
    }

    private static double MonitorScale(IntPtr monitor, IntPtr window)
    {
        if (monitor != IntPtr.Zero
            && NativeMethods.GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0
            && dpiX >= 96)
        {
            return dpiX / 96d;
        }

        var dpi = window == IntPtr.Zero ? 0 : NativeMethods.GetDpiForWindow(window);
        return dpi >= 96 ? dpi / 96d : 1;
    }

    public static RectInt32 CursorWorkArea()
    {
        NativeMethods.GetCursorPos(out var point);
        return DisplayArea.GetFromPoint(new PointInt32(point.X, point.Y), DisplayAreaFallback.Nearest).WorkArea;
    }
}
