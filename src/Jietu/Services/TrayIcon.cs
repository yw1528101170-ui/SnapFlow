namespace Jietu;

internal enum TrayCommand : uint
{
    None = 0,
    CaptureNow = 1,
    CaptureDelay1 = 11,
    CaptureDelay2 = 12,
    CaptureDelay3 = 13,
    CaptureDelay5 = 14,
    CaptureDelay10 = 15,
    ShowHistory = 20,
    ClearHistory = 21,
    OpenFolder = 30,
    ClosePins = 31,
    RestoreGhostPins = 32,
    ToggleOsd = 35,
    OsdPositionTopRight = 36,
    OsdPositionTopLeft = 37,
    OsdDisplayPrimary = 38,
    OsdDisplayFollowCursor = 39,
    TogglePause = 40,
    ToggleStartup = 41,
    ShowSettings = 42,
    Restart = 50,
    Exit = 51,
}

internal readonly record struct TrayMenuContext(
    bool Paused,
    bool RunAtStartup,
    string RegionHotkey,
    string HistoryHotkey,
    int PinCount,
    bool HasGhostPins,
    bool ShowOsd,
    string OsdPosition,
    string OsdDisplay);

internal sealed class TrayIcon : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly IntPtr _icon;
    private bool _added;
    // 最近一次 UpdateTip 记录的暂停状态，ReAdd 时用它恢复对应的提示文案。
    private bool _paused;

    public TrayIcon(IntPtr hwnd, bool paused = false)
    {
        _hwnd = hwnd;
        _paused = paused;
        _icon = AppIcon.LoadTray();
        if (_icon == IntPtr.Zero)
        {
            _icon = CreateFallback();
        }
        _added = AddWithRetry();
        if (!_added)
        {
            Log.Error("托盘图标添加失败，错误 " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        }
        else
        {
            Log.Info("托盘图标已就绪");
        }
    }

    /// <summary>
    /// Explorer 崩溃/重启后重新添加托盘图标（必须在 UI 线程调用）。
    /// 任务栏重建时系统不给具体图标发通知，只能先 NimDelete 再重新 NimAdd。
    /// </summary>
    public void ReAdd()
    {
        if (_added)
        {
            var stale = CreateData(_paused);
            NativeMethods.Shell_NotifyIcon(NativeMethods.NimDelete, ref stale);
            _added = false;
        }

        _added = AddWithRetry();
        if (_added)
        {
            Log.Info("托盘图标已重新添加");
        }
        else
        {
            Log.Error("托盘图标重新添加失败，错误 " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        }
    }

    public void UpdateTip(bool paused)
    {
        _paused = paused;
        if (!_added)
        {
            return;
        }

        var data = new NativeMethods.NotifyIconData
        {
            CbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.NotifyIconData>(),
            HWnd = _hwnd,
            UId = 1,
            UFlags = NativeMethods.NifTip,
            SzTip = paused ? "SnapFlow 瞬流 (已暂停记录)" : "SnapFlow 瞬流",
        };
        NativeMethods.Shell_NotifyIcon(NativeMethods.NimModify, ref data);
    }

    public TrayCommand ShowMenu(in TrayMenuContext ctx)
    {
        var menu = NativeMethods.CreatePopupMenu();
        var delaySubmenu = NativeMethods.CreatePopupMenu();
        var osdSubmenu = NativeMethods.CreatePopupMenu();
        var delayAttached = false;
        var osdAttached = false;
        try
        {
            // 1. 截图（设为加粗默认项，显示快捷键）
            var captureText = string.IsNullOrWhiteSpace(ctx.RegionHotkey) ? "截图" : $"截图\t{ctx.RegionHotkey}";
            Add(menu, (nuint)TrayCommand.CaptureNow, captureText);
            NativeMethods.SetMenuDefaultItem(menu, (uint)TrayCommand.CaptureNow, 0);

            // 2. 延时截图（二级子菜单收纳各个延时选项）
            Add(delaySubmenu, (nuint)TrayCommand.CaptureDelay1, "1 秒");
            Add(delaySubmenu, (nuint)TrayCommand.CaptureDelay2, "2 秒");
            Add(delaySubmenu, (nuint)TrayCommand.CaptureDelay3, "3 秒");
            Add(delaySubmenu, (nuint)TrayCommand.CaptureDelay5, "5 秒");
            Add(delaySubmenu, (nuint)TrayCommand.CaptureDelay10, "10 秒");
            NativeMethods.AppendMenu(menu, NativeMethods.MfPopup, (nuint)delaySubmenu, "延时截图");
            delayAttached = true;

            NativeMethods.AppendMenu(menu, NativeMethods.MfSeparator, 0, null);

            // 3. 剪贴板历史与清理
            var historyText = string.IsNullOrWhiteSpace(ctx.HistoryHotkey) ? "剪贴板历史" : $"剪贴板历史\t{ctx.HistoryHotkey}";
            Add(menu, (nuint)TrayCommand.ShowHistory, historyText);
            Add(menu, (nuint)TrayCommand.ClearHistory, "清空历史记录...");

            NativeMethods.AppendMenu(menu, NativeMethods.MfSeparator, 0, null);

            // 4. 便捷工具
            Add(menu, (nuint)TrayCommand.OpenFolder, "打开截图文件夹");
            if (ctx.PinCount > 0)
            {
                Add(menu, (nuint)TrayCommand.ClosePins, $"关闭所有贴图 ({ctx.PinCount})");
            }
            else
            {
                Add(menu, (nuint)TrayCommand.ClosePins, "关闭所有贴图", disabled: true);
            }
            if (ctx.HasGhostPins)
            {
                Add(menu, (nuint)TrayCommand.RestoreGhostPins, "恢复贴图鼠标交互 (退出幽灵模式)");
            }

            // 5. 性能悬浮窗 (OSD)
            Add(osdSubmenu, (nuint)TrayCommand.ToggleOsd, "显示悬浮窗", check: ctx.ShowOsd);
            NativeMethods.AppendMenu(osdSubmenu, NativeMethods.MfSeparator, 0, null);
            Add(osdSubmenu, (nuint)TrayCommand.OsdPositionTopRight, "停靠在右上角", check: string.Equals(ctx.OsdPosition, "TopRight", StringComparison.OrdinalIgnoreCase));
            Add(osdSubmenu, (nuint)TrayCommand.OsdPositionTopLeft, "停靠在左上角", check: string.Equals(ctx.OsdPosition, "TopLeft", StringComparison.OrdinalIgnoreCase));
            NativeMethods.AppendMenu(osdSubmenu, NativeMethods.MfSeparator, 0, null);
            Add(osdSubmenu, (nuint)TrayCommand.OsdDisplayPrimary, "停靠在主显示器", check: !string.Equals(ctx.OsdDisplay, "FollowCursor", StringComparison.OrdinalIgnoreCase));
            Add(osdSubmenu, (nuint)TrayCommand.OsdDisplayFollowCursor, "跟随鼠标所在屏幕", check: string.Equals(ctx.OsdDisplay, "FollowCursor", StringComparison.OrdinalIgnoreCase));
            NativeMethods.AppendMenu(menu, NativeMethods.MfPopup, (nuint)osdSubmenu, "性能悬浮窗 (OSD)");
            osdAttached = true;

            NativeMethods.AppendMenu(menu, NativeMethods.MfSeparator, 0, null);

            // 6. 状态与设置
            Add(menu, (nuint)TrayCommand.TogglePause, "暂停记录", check: ctx.Paused);
            Add(menu, (nuint)TrayCommand.ToggleStartup, "开机自启动", check: ctx.RunAtStartup);
            Add(menu, (nuint)TrayCommand.ShowSettings, "设置...");

            NativeMethods.AppendMenu(menu, NativeMethods.MfSeparator, 0, null);

            // 7. 重启与退出
            Add(menu, (nuint)TrayCommand.Restart, "重启应用");
            Add(menu, (nuint)TrayCommand.Exit, "退出");

            NativeMethods.GetCursorPos(out var point);
            NativeMethods.SetForegroundWindow(_hwnd);
            var command = NativeMethods.TrackPopupMenu(
                menu,
                NativeMethods.TpmReturnCmd | NativeMethods.TpmRightButton | NativeMethods.TpmNoNotify,
                point.X,
                point.Y,
                0,
                _hwnd,
                IntPtr.Zero);
            NativeMethods.PostMessage(_hwnd, NativeMethods.WmNull, IntPtr.Zero, IntPtr.Zero);
            return (TrayCommand)command;
        }
        finally
        {
            if (!delayAttached && delaySubmenu != IntPtr.Zero)
            {
                NativeMethods.DestroyMenu(delaySubmenu);
            }
            if (!osdAttached && osdSubmenu != IntPtr.Zero)
            {
                NativeMethods.DestroyMenu(osdSubmenu);
            }
            NativeMethods.DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = CreateData();
            NativeMethods.Shell_NotifyIcon(NativeMethods.NimDelete, ref data);
            _added = false;
        }

        if (_icon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_icon);
        }
    }

    private NativeMethods.NotifyIconData CreateData(bool paused = false)
    {
        return new NativeMethods.NotifyIconData
        {
            CbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.NotifyIconData>(),
            HWnd = _hwnd,
            UId = 1,
            UFlags = NativeMethods.NifMessage | NativeMethods.NifIcon | NativeMethods.NifTip,
            UCallbackMessage = NativeMethods.TrayCallback,
            HIcon = _icon,
            SzTip = paused ? "SnapFlow 瞬流 (已暂停记录)" : "SnapFlow 瞬流",
        };
    }

    // 沿用构造期逻辑：最多 5 轮，失败时 NimDelete 后重试一次（Explorer 重启后首批 NimAdd 常失败）。
    private bool AddWithRetry()
    {
        var data = CreateData(_paused);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (NativeMethods.Shell_NotifyIcon(NativeMethods.NimAdd, ref data))
            {
                return true;
            }

            NativeMethods.Shell_NotifyIcon(NativeMethods.NimDelete, ref data);
            Thread.Sleep(60);
            if (NativeMethods.Shell_NotifyIcon(NativeMethods.NimAdd, ref data))
            {
                return true;
            }
        }

        return false;
    }

    private static void Add(IntPtr menu, nuint id, string text, bool check = false, bool disabled = false)
    {
        var flags = NativeMethods.MfString;
        if (check)
        {
            flags |= NativeMethods.MfChecked;
        }
        if (disabled)
        {
            flags |= NativeMethods.MfDisabled | NativeMethods.MfGrayed;
        }

        NativeMethods.AppendMenu(menu, flags, id, text);
    }

    private static IntPtr CreateFallback()
    {
        const int size = 32;
        var info = new NativeMethods.BitmapInfo
        {
            Header = new NativeMethods.BitmapInfoHeader
            {
                BiSize = 40,
                BiWidth = size,
                BiHeight = -size,
                BiPlanes = 1,
                BiBitCount = 32,
                BiCompression = NativeMethods.BiRgb,
            },
        };
        var screen = NativeMethods.GetDC(IntPtr.Zero);
        var color = NativeMethods.CreateDIBSection(screen, ref info, 0, out var bits, IntPtr.Zero, 0);
        NativeMethods.ReleaseDC(IntPtr.Zero, screen);
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var i = (y * size + x) * 4;
                var dx = Math.Abs(x - 15.5) / 14.0;
                var dy = Math.Abs(y - 15.5) / 14.0;
                var dist = Math.Pow(dx * dx * dx * dx + dy * dy * dy * dy, 0.25);
                if (dist > 1.0)
                {
                    pixels[i + 3] = 0;
                    continue;
                }

                var t = (x + y) / 62.0;
                pixels[i] = (byte)(74 + (115 - 74) * (1 - t));
                pixels[i + 1] = (byte)(160 + (220 - 160) * (1 - t));
                pixels[i + 2] = 0;
                pixels[i + 3] = 255;

                var rx = Math.Abs(x - 15.5);
                var ry = Math.Abs(y - 15.5);
                var inArm = (rx >= 4.5 && rx <= 8.5 && ry >= 6.5 && ry <= 8.5) ||
                             (ry >= 4.5 && ry <= 8.5 && rx >= 6.5 && rx <= 8.5);
                var inDot = rx <= 1.5 && ry <= 1.5;

                if (inArm || inDot)
                {
                    pixels[i] = 255;
                    pixels[i + 1] = 255;
                    pixels[i + 2] = 255;
                }
            }
        }

        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bits, pixels.Length);
        var mask = NativeMethods.CreateBitmap(size, size, 1, 1, IntPtr.Zero);
        var iconInfo = new NativeMethods.IconInfo
        {
            FIcon = true,
            HbmColor = color,
            HbmMask = mask,
        };
        var icon = NativeMethods.CreateIconIndirect(ref iconInfo);
        NativeMethods.DeleteObject(color);
        NativeMethods.DeleteObject(mask);
        return icon;
    }
}
