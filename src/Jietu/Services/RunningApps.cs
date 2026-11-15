using System.Text;

namespace Jietu;

internal readonly record struct RunningApp(string Title, string ExeName)
{
    /// <summary>胶囊/列表上显示的名字：优先窗口标题（人认得的「微信」「记事本」），没有标题就退回 exe。</summary>
    public string Display => Title.Length > 0 ? Title : ExeName;
}

/// <summary>
/// 枚举当前带可见窗口的程序。给「排除不记录的程序」提供点选列表：
/// 用户只需要认窗口，不必知道 WeChat.exe 还是 Weixin.exe。
/// </summary>
internal static class RunningApps
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    public static List<RunningApp> List(int limit = 120)
    {
        var found = new List<RunningApp>();
        var ourPid = (uint)Environment.ProcessId;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (found.Count >= limit)
            {
                return false;
            }

            if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd) || IsCloaked(hwnd))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || pid == ourPid)
            {
                return true;
            }

            // 没有标题的基本都是工具窗/宿主窗，列出来只会让人更困惑，直接跳过。
            var title = TitleOf(hwnd);
            if (title.Length == 0)
            {
                return true;
            }

            var exe = ExeOf(pid);
            if (exe == null)
            {
                return true;
            }

            found.Add(new RunningApp(title, exe));
            return true;
        }, IntPtr.Zero);

        // 同一程序开了多个窗口（多个 Chrome 窗口、多个资源管理器）只留一条。
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var apps = new List<RunningApp>(found.Count);
        foreach (var app in found)
        {
            if (seen.Add(app.ExeName))
            {
                apps.Add(app);
            }
        }

        apps.Sort(static (left, right) => string.Compare(left.Display, right.Display, StringComparison.CurrentCulture));
        return apps;
    }

    private static string TitleOf(IntPtr hwnd)
    {
        var buffer = new StringBuilder(512);
        var length = NativeMethods.GetWindowText(hwnd, buffer, buffer.Capacity);
        var title = length > 0 ? buffer.ToString(0, Math.Min(length, buffer.Length)) : "";
        return title.Trim();
    }

    private static string? ExeOf(uint pid)
    {
        var handle = NativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size)
                ? Path.GetFileName(buffer.ToString(0, Math.Min(size, buffer.Length)))
                : null;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        // UWP 应用会留一堆「幽灵窗口」（可见但已被 DWM 隐去），不排掉列表里全是空壳。
        var hr = NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DwmwaCloaked, out int cloaked, sizeof(int));
        return hr == 0 && cloaked != 0;
    }
}
