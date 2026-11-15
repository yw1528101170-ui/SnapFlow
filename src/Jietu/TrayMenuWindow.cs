using System.Runtime.InteropServices;

namespace Jietu;

/// <summary>
/// 托盘右键菜单：一块自绘的液态玻璃浮层，替掉系统 TrackPopupMenu。
/// 系统菜单的字体、行高、分组、勾选全由 Windows 决定，改不动；这块浮层沿用 OSD 那套玻璃引擎
/// （抓背景 → 磨砂 + 边缘折射 + 亮棱），行高、留白、字号、开关、行内分段都自己排。
/// 交互是同步的：Show 内部跑一段局部消息循环，直到选了某一项或点走，再把命令返回给调用方，
/// 和原来 ShowMenu 的「返回一个 TrayCommand」语义一致。
/// 窗口只负责生命周期与输入，像素由 <see cref="TrayMenuRenderer"/> 画。
/// </summary>
internal sealed class TrayMenuWindow : IDisposable
{
    #region Win32 常量与补充 P/Invoke

    private const int WmActivate = 0x0006;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmMouseMove = 0x0200;
    private const int WmMouseLeave = 0x02A3;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;
    private const int WmNull = 0x0000;
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;
    private const int WaInactive = 0;

    private const int VkEscape = 0x1B;
    private const int VkUp = 0x26;
    private const int VkDown = 0x28;
    private const int VkReturn = 0x0D;

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr HWnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PtX;
        public int PtY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouseEventData
    {
        public int CbSize;
        public uint Flags;
        public IntPtr HWndTrack;
        public uint HoverTime;
    }

    private static class MenuNative
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref Msg msg);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr DispatchMessage(ref Msg msg);

        [DllImport("user32.dll")]
        public static extern void PostQuitMessage(int code);

        [DllImport("user32.dll")]
        public static extern bool TrackMouseEvent(ref TrackMouseEventData data);

        [DllImport("user32.dll")]
        public static extern IntPtr SetCursor(IntPtr cursor);

        [DllImport("user32.dll")]
        public static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForSystem();

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelHook proc, IntPtr module, uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const uint TmeLeave = 0x00000002;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private static readonly IntPtr IdiArrow = new(32512);

    // 不激活显示 + 低层钩子：钩子把键鼠事件转成窗口消息，窗口照旧处理，
    // 这样既不用抢前台（不会把「更多任务栏」顶掉），Esc / 方向键 / 点外面关闭也还在。
    private const int SwShowNoActivate = 4;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int HcAction = 0;
    private const int WmAppKey = 0x8000 + 0x0101;
    private const int WmAppOutside = 0x8000 + 0x0102;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMButtonDown = 0x0207;
    private const int WmXButtonDown = 0x020B;

    private delegate IntPtr LowLevelHook(int code, IntPtr wParam, IntPtr lParam);

    private static readonly LowLevelHook KeyboardHookProc = OnKeyboardHook;
    private static readonly LowLevelHook MouseHookProc = OnMouseHook;
    private static IntPtr _keyboardHook;
    private static IntPtr _mouseHook;

    #endregion

    private readonly IntPtr _hwnd;
    private readonly LayeredCanvas _canvas = new();
    private readonly string _className = "SnapFlow.TrayMenuWindow";
    private static TrayMenuWindow? _active;
    private static bool _classRegistered;
    private static NativeMethods.WndProc? _wndProcKeepAlive;

    private TrayMenuLayout? _layout;

    private double _scale = 1.0;
    private int _x;
    private int _y;
    private int _hoverRow = -1;
    private int _hoverChip = -1;
    private int _selectRow = -1;
    private bool _trackingLeave;
    private bool _closed;
    private bool _running;
    private TrayCommand _result = TrayCommand.None;

    private TrayMenuWindow()
    {
        _active = this;

        if (!_classRegistered)
        {
            var wndProc = new NativeMethods.WndProc(StaticWndProc);
            _wndProcKeepAlive = wndProc;
            var classInst = NativeMethods.GetModuleHandle(null);
            var wc = new NativeMethods.WndClass
            {
                Style = 0,
                LpfnWndProc = wndProc,
                HInstance = classInst,
                LpszClassName = _className,
            };
            NativeMethods.RegisterClass(ref wc);
            _classRegistered = true;

            var input = OverlayGdi.GdiplusStartupInput.Default;
            OverlayGdi.GdiplusStartup(out _, ref input, IntPtr.Zero);
        }

        var hInstance = NativeMethods.GetModuleHandle(null);
        _hwnd = NativeMethods.CreateWindowEx(
            NativeMethods.WsExLayered | NativeMethods.WsExTopmost | NativeMethods.WsExToolWindow,
            _className,
            "SnapFlowTrayMenu",
            NativeMethods.WsPopup,
            0, 0, 0, 0,
            IntPtr.Zero,
            IntPtr.Zero,
            hInstance,
            IntPtr.Zero);
    }

    /// <summary>
    /// 弹出菜单并等一个结果。返回 null 表示这块浮层没能建起来（极少见），调用方可以退回系统菜单。
    /// </summary>
    public static TrayCommand? Show(List<TrayRow> rows, IntPtr owner)
    {
        if (_active == null)
        {
            try
            {
                _ = new TrayMenuWindow();
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                return null;
            }
        }

        return _active is null ? null : _active.Run(rows, owner);
    }

    #region 弹出与消息循环

    private TrayCommand? Run(List<TrayRow> rows, IntPtr owner)
    {
        // 局部消息循环会取走发给消息窗的托盘回调，用户在菜单开着时再点一次图标就会重入，
        // 变成两块菜单互相覆盖：这里直接忽略，等当前这块走完。
        if (_running)
        {
            return TrayCommand.None;
        }

        _running = true;
        try
        {
            return RunCore(rows, owner);
        }
        finally
        {
            _running = false;
        }
    }

    private TrayCommand? RunCore(List<TrayRow> rows, IntPtr owner)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return null;
        }

        _closed = false;
        _result = TrayCommand.None;
        _hoverRow = -1;
        _hoverChip = -1;
        _selectRow = -1;
        _trackingLeave = false;

        NativeMethods.GetCursorPos(out var cursor);
        _scale = ResolveScale(owner);

        var layout = TrayMenuRenderer.Measure(rows, (float)_scale);
        if (!_canvas.Ensure(layout.Width, layout.Height))
        {
            return null;
        }

        TrayMenuRenderer.MeasureChips(layout, _canvas.Graphics);
        _layout = layout;

        // 菜单贴在光标左上方，右下角对准光标；越界就整体推回工作区
        var work = WorkAreaFor(cursor);
        _x = Math.Clamp(cursor.X + 8 - layout.Width, work.Left + 4, Math.Max(work.Left + 4, work.Right - layout.Width - 4));
        _y = Math.Clamp(cursor.Y - 4 - layout.Height, work.Top + 4, Math.Max(work.Top + 4, work.Bottom - layout.Height - 4));

        Paint();

        if (!_canvas.Present(_hwnd, _x, _y))
        {
            return null;
        }

        // 不抢前台：任务栏的「更多任务栏」（隐藏图标的弹出面板）一失去激活就会自己收起来，
        // 用 SetForegroundWindow 弹菜单等于把它顶掉。改成 NOACTIVATE 显示 + 低层键鼠钩子接输入，
        // 面板就能一直摊开着，用户选完再走。
        NativeMethods.ShowWindow(_hwnd, SwShowNoActivate);
        MenuNative.SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
        MenuNative.SetCursor(MenuNative.LoadCursor(IntPtr.Zero, IdiArrow));

        _keyboardHook = MenuNative.SetWindowsHookEx(WhKeyboardLl, KeyboardHookProc, IntPtr.Zero, 0);
        _mouseHook = MenuNative.SetWindowsHookEx(WhMouseLl, MouseHookProc, IntPtr.Zero, 0);
        try
        {
            while (!_closed)
            {
                int ret = MenuNative.GetMessage(out var msg, IntPtr.Zero, 0, 0);
                if (ret <= 0)
                {
                    // GetMessage 取到 WM_QUIT 时返回值就是 0：退出标志已被这次调用消费掉，
                    // 必须补投回去，否则外层消息循环再也等不到退出、应用关不掉。
                    // -1 是 GetMessage 出错，直接退局部循环即可。
                    if (ret == 0)
                    {
                        MenuNative.PostQuitMessage((int)msg.WParam.ToInt64());
                    }

                    break;
                }

                MenuNative.TranslateMessage(ref msg);
                MenuNative.DispatchMessage(ref msg);
            }
        }
        finally
        {
            Unhook(ref _keyboardHook);
            Unhook(ref _mouseHook);
        }

        NativeMethods.ShowWindow(_hwnd, NativeMethods.SwHide);
        var result = _result;
        _result = TrayCommand.None;
        return result;
    }

    private double ResolveScale(IntPtr owner)
    {
        var dpi = owner != IntPtr.Zero ? NativeMethods.GetDpiForWindow(owner) : 0u;
        if (dpi == 0)
        {
            dpi = MenuNative.GetDpiForSystem();
        }

        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    private static NativeMethods.Rect WorkAreaFor(in NativeMethods.Point cursor)
    {
        var monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero)
        {
            var info = new NativeMethods.MonitorInfo { CbSize = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
            if (NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                return info.RcWork;
            }
        }

        return new NativeMethods.Rect
        {
            Left = 0,
            Top = 0,
            Right = MenuNative.GetSystemMetrics(SmCxScreen),
            Bottom = MenuNative.GetSystemMetrics(SmCyScreen),
        };
    }

    private void Close(TrayCommand command)
    {
        // 先到先得：选中某项之后，紧随其后的失焦/鼠标离开可能再叫一次 Close(None)，
        // 不能让它把已经选好的命令覆盖成「什么都没选」。
        if (_closed)
        {
            return;
        }

        _result = command;
        _closed = true;
        NativeMethods.PostMessage(_hwnd, (uint)WmNull, IntPtr.Zero, IntPtr.Zero);
    }

    #endregion

    #region 玻璃与重绘

    private void Paint()
    {
        if (_layout == null)
        {
            return;
        }

        OverlayGdi.Clear(_canvas.Graphics);
        // 玻璃底已退场：抓屏+CPU 模糊在弹菜单的关键路径上（每次唤出都要抓一遍），
        // 只保留 OSD 一处自研玻璃。Renderer 收到零图就走自己的白板兜底，菜单永远可读。
        TrayMenuRenderer.Paint(_canvas.Graphics, _layout, IntPtr.Zero,
            _hoverRow, _hoverChip, _selectRow);
    }

    private void Repaint()
    {
        Paint();
        _canvas.Present(_hwnd, _x, _y);
    }

    #endregion

    #region 命中与输入

    private void UpdateHover(int x, int y)
    {
        var row = HitRow(x, y);
        var chip = -1;
        if (row >= 0 && _layout != null && _layout.Rows[row].Kind == TrayRowKind.Chips)
        {
            chip = HitChip(row, x, y);
        }

        if (row == _hoverRow && chip == _hoverChip)
        {
            return;
        }

        _hoverRow = row;
        _hoverChip = chip;
        _selectRow = -1;
        Repaint();
    }

    private int HitRow(int x, int y)
    {
        if (_layout == null || x < 0 || x >= _layout.Width)
        {
            return -1;
        }

        for (var i = 0; i < _layout.Rows.Count; i++)
        {
            var top = _layout.Top[i];
            if (y >= top && y < top + _layout.RowHeight[i])
            {
                var kind = _layout.Rows[i].Kind;
                return kind is TrayRowKind.Divider or TrayRowKind.Header ? -1 : i;
            }
        }

        return -1;
    }

    private int HitChip(int row, int x, int y)
    {
        if (_layout == null)
        {
            return -1;
        }

        var scale = _layout.Scale;
        var chipH = 22f * scale;
        var top = _layout.Top[row] + (_layout.RowHeight[row] - chipH) * 0.5f;
        if (y < top || y >= top + chipH)
        {
            return -1;
        }

        foreach (var (boxX, boxW, index, _) in _layout.Chips[row])
        {
            if (x >= boxX && x < boxX + boxW)
            {
                return index;
            }
        }

        return -1;
    }

    private void Activate(int row, int x, int y)
    {
        if (_layout == null)
        {
            return;
        }

        var item = _layout.Rows[row];
        if (!item.Enabled)
        {
            return;
        }

        if (item.Kind == TrayRowKind.Chips)
        {
            var chip = HitChip(row, x, y);
            if (chip >= 0 && chip < item.Chips.Count)
            {
                Close(item.Chips[chip].Command);
            }

            return;
        }

        if (item.Command != TrayCommand.None)
        {
            Close(item.Command);
        }
    }

    private void MoveSelection(int delta)
    {
        if (_layout == null)
        {
            return;
        }

        var selectable = new List<int>();
        for (var i = 0; i < _layout.Rows.Count; i++)
        {
            var kind = _layout.Rows[i].Kind;
            if (kind is TrayRowKind.Action or TrayRowKind.Toggle && _layout.Rows[i].Enabled)
            {
                selectable.Add(i);
            }
        }

        if (selectable.Count == 0)
        {
            return;
        }

        var at = selectable.IndexOf(_selectRow);
        at = at < 0 ? (delta > 0 ? 0 : selectable.Count - 1) : (at + delta + selectable.Count) % selectable.Count;
        _selectRow = selectable[at];
        _hoverRow = _selectRow;
        _hoverChip = -1;
        Repaint();
    }

    #endregion

    #region 窗口过程

    private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        var inst = _active;
        return inst != null ? inst.Handle(hwnd, msg, w, l) : NativeMethods.DefWindowProc(hwnd, msg, w, l);
    }

    private IntPtr Handle(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WmActivate:
                if ((wParam.ToInt32() & 0xFFFF) == WaInactive)
                {
                    Close(TrayCommand.None);
                }

                return IntPtr.Zero;

            case WmMouseActivate:
                // 告诉系统「别激活我，但把这次点击照常发给我」：窗口一旦被激活，
                // 第一下点击就只是激活、不会落到行上；而且激活会把「更多任务栏」面板顶掉。
                return (IntPtr)MaNoActivate;

            case WmMouseMove:
            {
                if (!_trackingLeave)
                {
                    var track = new TrackMouseEventData
                    {
                        CbSize = Marshal.SizeOf<TrackMouseEventData>(),
                        Flags = TmeLeave,
                        HWndTrack = hwnd,
                    };
                    MenuNative.TrackMouseEvent(ref track);
                    _trackingLeave = true;
                }

                UpdateHover(PointX(lParam), PointY(lParam));
                return IntPtr.Zero;
            }

            case WmMouseLeave:
            {
                _trackingLeave = false;
                if (_hoverRow != -1)
                {
                    _hoverRow = -1;
                    _hoverChip = -1;
                    Repaint();
                }

                return IntPtr.Zero;
            }

            case WmLButtonUp:
            {
                var x = PointX(lParam);
                var y = PointY(lParam);
                var row = HitRow(x, y);
                if (row >= 0)
                {
                    Activate(row, x, y);
                }
                else
                {
                    Close(TrayCommand.None);
                }

                return IntPtr.Zero;
            }

            case WmRButtonUp:
                // 和系统菜单一致：右键只负责收起，不执行任何命令
                Close(TrayCommand.None);
                return IntPtr.Zero;

            case WmKeyDown:
            case WmSysKeyDown:
                HandleKey(wParam.ToInt32());
                return IntPtr.Zero;

            case WmAppKey:
                // 低层键盘钩子转过来的（窗口不激活，收不到普通 WM_KEYDOWN）
                HandleKey(wParam.ToInt32());
                return IntPtr.Zero;

            case WmAppOutside:
                // 低层鼠标钩子发现点在菜单外面：和 TrackPopupMenu 一样直接收起
                Close(TrayCommand.None);
                return IntPtr.Zero;

            default:
                return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    private void HandleKey(int vk)
    {
        switch (vk)
        {
            case VkEscape:
                Close(TrayCommand.None);
                break;
            case VkUp:
                MoveSelection(-1);
                break;
            case VkDown:
                MoveSelection(1);
                break;
            case VkReturn:
                if (_layout != null && _selectRow >= 0 && _selectRow < _layout.Rows.Count)
                {
                    var item = _layout.Rows[_selectRow];
                    if (item.Enabled && item.Command != TrayCommand.None)
                    {
                        Close(item.Command);
                    }
                }

                break;
            default:
                break;
        }
    }

    private static IntPtr OnKeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code == HcAction)
        {
            var inst = _active;
            var down = (int)wParam == WmKeyDown || (int)wParam == WmSysKeyDown;
            if (down && inst != null && !inst._closed && inst._hwnd != IntPtr.Zero)
            {
                var vk = Marshal.ReadInt32(lParam, 0);
                // 括号别省：&& 的优先级高于 ||，漏了括号 Esc 这条就永远不投递
                if ((vk == VkEscape || vk == VkUp || vk == VkDown || vk == VkReturn)
                    && MenuNative.PostMessage(inst._hwnd, WmAppKey, (IntPtr)vk, IntPtr.Zero))
                {
                    // 吞掉：Esc 不该再传给下层窗口（托盘图标之类）
                    return (IntPtr)1;
                }
            }
        }

        return MenuNative.CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private static IntPtr OnMouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code == HcAction)
        {
            var msg = (int)wParam;
            if (msg == WmLButtonDown || msg == WmRButtonDown || msg == WmMButtonDown || msg == WmXButtonDown)
            {
                var inst = _active;
                if (inst != null && !inst._closed && inst._hwnd != IntPtr.Zero)
                {
                    var x = Marshal.ReadInt32(lParam, 0);
                    var y = Marshal.ReadInt32(lParam, 4);
                    if (!IsScreenPointInside(inst._hwnd, x, y))
                    {
                        MenuNative.PostMessage(inst._hwnd, WmAppOutside, IntPtr.Zero, IntPtr.Zero);
                    }
                }
            }
        }

        return MenuNative.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private static bool IsScreenPointInside(IntPtr hwnd, int x, int y)
    {
        if (!MenuNative.GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        return x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;
    }

    private static void Unhook(ref IntPtr hook)
    {
        if (hook != IntPtr.Zero)
        {
            MenuNative.UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }
    }

    private static int PointX(IntPtr lParam) => (short)(lParam.ToInt64() & 0xFFFF);

    private static int PointY(IntPtr lParam) => (short)((lParam.ToInt64() >> 16) & 0xFFFF);

    #endregion

    public void Dispose()
    {
        _canvas.Dispose();
        _active = null;
    }
}

