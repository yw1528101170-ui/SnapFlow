using System.Runtime.InteropServices;
using System.Text;

namespace Jietu;

internal static class SensitiveForeground
{
    private static readonly HashSet<string> ProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "KeePass.exe",
        "KeePassXC.exe",
        "1Password.exe",
        "Bitwarden.exe",
        "LastPass.exe",
        "Dashlane.exe",
        "Enpass.exe",
        "NordPass.exe",
        "Keeper.exe",
        "pwsafe.exe",
    };

    private static readonly HashSet<string> TerminalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal.exe",
        "wt.exe",
        "cmd.exe",
        "powershell.exe",
        "pwsh.exe",
        "conhost.exe",
        "mintty.exe",
        "putty.exe",
        "MobaXterm.exe",
        "SecureCRT.exe",
    };

    private static string[] _ignored = [];
    private static bool _recordTerminals;

    // 密码库主窗口句柄：窗口激活时注册，激活丢失时清除。是自身进程的窗口，
    // 不能按进程名识别（会把 SnapFlow 自己的所有窗口都挡掉），必须按句柄精确挡。
    private static volatile IntPtr _vaultWindow;

    public static void SetVaultWindow(IntPtr hwnd) => _vaultWindow = hwnd;

    public static void SetIgnored(IReadOnlyList<string> names)
    {
        _ignored = names.Count == 0 ? [] : names.ToArray();
    }

    public static void SetRecordTerminals(bool record) => _recordTerminals = record;

    public static bool IsBlocked()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd != IntPtr.Zero && hwnd == _vaultWindow)
        {
            return true;
        }

        var fileName = ForegroundFileName();
        return BlocksProcess(fileName) || IsPasswordFocus();
    }

    internal static bool BlocksProcess(string? fileName)
    {
        return IsSensitiveProcess(fileName) || IsUserIgnored(fileName) || (IsTerminal(fileName) && !_recordTerminals);
    }

    internal static bool IsUserIgnored(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        var ignored = _ignored;
        foreach (var name in ignored)
        {
            if (name.Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsTerminal(string? fileName)
    {
        return !string.IsNullOrEmpty(fileName) && TerminalNames.Contains(fileName);
    }

    internal static bool IsSensitiveProcess(string? fileName)
    {
        return !string.IsNullOrEmpty(fileName) && ProcessNames.Contains(fileName);
    }

    private static bool IsPasswordFocus()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var thread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        var info = new NativeMethods.GuiThreadInfo
        {
            CbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.GuiThreadInfo>(),
        };
        if (thread == 0 || !NativeMethods.GetGUIThreadInfo(thread, ref info) || info.HwndFocus == IntPtr.Zero)
        {
            return false;
        }

        var className = new StringBuilder(64);
        if (NativeMethods.GetClassName(info.HwndFocus, className, className.Capacity) == 0)
        {
            return false;
        }

        if (!className.ToString().Equals("Edit", StringComparison.OrdinalIgnoreCase))
        {
            return IsProtectedField(info);
        }

        const int esPassword = 0x20;
        var style = NativeMethods.GetWindowLongPtr(info.HwndFocus, -16);
        return (style & esPassword) != 0 || IsProtectedField(info);
    }

    private static bool IsProtectedField(NativeMethods.GuiThreadInfo info)
    {
        if (info.RcCaret.Width <= 0 || info.RcCaret.Height <= 0)
        {
            return false;
        }

        var x = info.RcCaret.Left + (info.RcCaret.Width / 2);
        var y = info.RcCaret.Top + (info.RcCaret.Height / 2);
        try
        {
            var point = new NativeMethods.Point { X = x, Y = y };
            var hr = AccessibleObjectFromPoint(point, out var acc, out var child);
            if (hr != 0 || acc == null)
            {
                return false;
            }

            var state = Convert.ToInt64(acc.accState(child ?? 0));
            return (state & 0x20000000) != 0;
        }
        catch (Exception ex)
        {
            Log.Info("密码框检查失败：" + ex.Message);
            return false;
        }
    }

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromPoint(
        NativeMethods.Point pt,
        [MarshalAs(UnmanagedType.Interface)] out IAccessible acc,
        [MarshalAs(UnmanagedType.Struct)] out object varChild);

    [ComImport]
    [Guid("618736E0-3C3D-11CF-810C-00AA00389B71")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IAccessible
    {
        [DispId(-5007)]
        object accState([MarshalAs(UnmanagedType.Struct)] object varChild);
    }

    private static string? ForegroundFileName()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0)
        {
            return null;
        }

        var handle = NativeMethods.OpenProcess(0x1000, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            if (!NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size))
            {
                return null;
            }

            return Path.GetFileName(buffer.ToString());
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }
}

internal static class ProcessNames
{
    public static List<string> Normalize(IEnumerable<string>? names)
    {
        var list = new List<string>();
        if (names == null)
        {
            return list;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in names)
        {
            var name = NormalizeOne(raw);
            if (name.Length == 0 || !seen.Add(name))
            {
                continue;
            }

            list.Add(name);
            if (list.Count >= 40)
            {
                break;
            }
        }

        return list;
    }

    public static List<string> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return Normalize(text.Split(['\r', '\n', ',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string NormalizeOne(string raw)
    {
        var text = raw.Trim().Trim('"');
        if (text.Length == 0 || text.Length > 260)
        {
            return "";
        }

        text = Path.GetFileName(text.Replace('/', '\\'));
        if (text.Length == 0 || text.Length > 80 || text.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return "";
        }

        if (!text.Contains('.'))
        {
            text += ".exe";
        }

        return text;
    }
}
