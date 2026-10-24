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
        var area = DisplayArea.GetFromPoint(new PointInt32(point.X, point.Y), DisplayAreaFallback.Nearest);
        var scale = MonitorScale(Win32Interop.GetMonitorFromDisplayId(area.DisplayId), hwnd);
        var work = area.WorkArea;
        var width = Math.Max(1, (int)Math.Round(dipWidth * scale));
        var height = Math.Max(1, (int)Math.Round(dipHeight * scale));
        if (width > work.Width)
        {
            width = Math.Max(1, work.Width);
        }

        if (height > work.Height)
        {
            height = Math.Max(1, work.Height);
        }

        var x = point.X;
        var y = point.Y;
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

    public static RectInt32 ToastBounds(int contentWidth = 0, int contentHeight = 0)
    {
        NativeMethods.GetCursorPos(out var point);
        var area = DisplayArea.GetFromPoint(new PointInt32(point.X, point.Y), DisplayAreaFallback.Nearest);
        var scale = MonitorScale(Win32Interop.GetMonitorFromDisplayId(area.DisplayId), IntPtr.Zero);
        var work = area.WorkArea;
        var width = contentWidth > 0 ? (int)Math.Round(contentWidth * scale) : Math.Max(1, (int)Math.Round(220 * scale));
        var height = contentHeight > 0 ? (int)Math.Round(contentHeight * scale) : Math.Max(1, (int)Math.Round(46 * scale));
        var margin = Math.Max(1, (int)Math.Round(24 * scale));
        if (width > work.Width)
        {
            width = Math.Max(1, work.Width);
        }

        if (height > work.Height)
        {
            height = Math.Max(1, work.Height);
        }

        var x = work.X + work.Width - width - margin;
        var y = work.Y + work.Height - height - margin;
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
