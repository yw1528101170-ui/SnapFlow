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

    // 已展开别名的排除集合：配置里只有 wechat.exe 时，微信 4.x 的 weixin.exe 也必须命中。
    private static HashSet<string> _ignored = new(StringComparer.OrdinalIgnoreCase);
    private static bool _recordTerminals;

    // 密码库主窗口句柄：窗口激活时注册，激活丢失时清除。是自身进程的窗口，
    // 不能按进程名识别（会把 SnapFlow 自己的所有窗口都挡掉），必须按句柄精确挡。
    private static volatile IntPtr _vaultWindow;

    public static void SetVaultWindow(IntPtr hwnd) => _vaultWindow = hwnd;

    // 本类的 ProcessNames 字段是内置密码管理器名单，归一化工具类要走 Jietu.ProcessNames 全名。
    public static void SetIgnored(IReadOnlyList<string> names) => _ignored = Jietu.ProcessNames.ExpandSet(names);

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

        return _ignored.Contains(fileName);
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
    /// <summary>排除列表上限：设置页与存储层共用这一个数字，加满后不再静默截断。</summary>
    public const int MaxEntries = 40;

    /// <summary>
    /// 同一款软件可能换过进程名、或自带多个宿主进程。只排一个名字等于「看着加了却不生效」：
    /// 微信 3.x 是 WeChat.exe，4.x 起改叫 Weixin.exe；Windows Terminal 另有 wt / OpenConsole 两个宿主。
    /// </summary>
    private static readonly Dictionary<string, string[]> AliasTable = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wechat.exe"] = ["weixin.exe"],
        ["weixin.exe"] = ["wechat.exe"],
        ["windowsterminal.exe"] = ["wt.exe", "openconsole.exe"],
        ["wt.exe"] = ["windowsterminal.exe", "openconsole.exe"],
        ["openconsole.exe"] = ["windowsterminal.exe", "wt.exe"],
    };

    public static List<string> Normalize(IEnumerable<string>? names)
    {
        var list = new List<string>();
        if (names == null)
        {
            return list;
        }

        // 别名同源的写法算同一条：先排 wechat.exe 再填 weixin.exe 不该变成两条。
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in names)
        {
            var name = NormalizeOne(raw);
            if (name.Length == 0 || !covered.Add(name))
            {
                continue;
            }

            foreach (var alias in AliasesOf(name))
            {
                covered.Add(alias);
            }

            list.Add(name);
            if (list.Count >= MaxEntries)
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

    /// <summary>
    /// 单条归一化：去引号、去掉粘贴进来的完整路径（C:\Program Files\Foo\foo.exe 也能直接用）、
    /// 缺后缀补 .exe，并挡掉非法字符。返回空串表示这条输入无效。
    /// </summary>
    public static string NormalizeOne(string? raw)
    {
        var text = (raw ?? "").Trim().Trim('"');
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

    /// <summary>该进程名同源的其它写法，供「已排除」状态判断与匹配扩展共用。</summary>
    public static IReadOnlyList<string> AliasesOf(string? name)
    {
        var key = (name ?? "").Trim();
        return key.Length > 0 && AliasTable.TryGetValue(key, out var aliases) ? aliases : [];
    }

    /// <summary>把名单展开成「名字 + 别名」的集合，剪贴板拦截时一次判定即可。</summary>
    public static HashSet<string> ExpandSet(IEnumerable<string>? names)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (names == null)
        {
            return set;
        }

        foreach (var name in names)
        {
            var key = (name ?? "").Trim();
            if (key.Length == 0)
            {
                continue;
            }

            set.Add(key);
            foreach (var alias in AliasesOf(key))
            {
                set.Add(alias);
            }
        }

        return set;
    }

    /// <summary>名单里是否已包含这个程序（别名同源也算），设置页据此点亮/置灰按钮。</summary>
    public static bool CoveredBy(IEnumerable<string>? names, string? proc)
    {
        var key = (proc ?? "").Trim();
        return key.Length > 0 && ExpandSet(names).Contains(key);
    }
}
