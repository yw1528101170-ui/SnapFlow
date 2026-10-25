using Microsoft.UI.Dispatching;

namespace Jietu;

/// <summary>
/// 托盘交互流：左键唤出/恢复、右键菜单的构建与命令分发。
/// 从 AppHost 拆出（约 150 行分发逻辑曾是组合根里最后一块大段流程代码）。
/// 命令执行通过注入的能力委托回调 AppHost，避免反向持有宿主。
/// </summary>
internal sealed class TrayController
{
    private readonly HistoryWindow _history;
    private readonly PinManager _pins;
    private readonly TrayIcon _tray;
    private readonly CaptureOrchestrator _capture;
    private readonly SettingsApplier _settingsApplier;
    private readonly Func<AppSettings> _settings;
    private readonly Action<Task> _track;
    private readonly Func<SettingsWindow> _openSettings;
    private readonly Action _restart;
    private readonly Action _shutdown;
    private readonly Action _clearHistory;
    private readonly IToastSink _toast;
    private readonly IntPtr _ownerWindow;
    private readonly Func<bool> _vaultUnlocked;
    private readonly Action _showVault;
    private readonly Action _lockVault;
    private long _lastTrayClick;

    public TrayController(
        HistoryWindow history,
        PinManager pins,
        TrayIcon tray,
        CaptureOrchestrator capture,
        SettingsApplier settingsApplier,
        Func<AppSettings> settings,
        Action<Task> track,
        Func<SettingsWindow> openSettings,
        Action restart,
        Action shutdown,
        Action clearHistory,
        IToastSink toast,
        IntPtr ownerWindow,
        Func<bool> vaultUnlocked,
        Action showVault,
        Action lockVault)
    {
        _history = history;
        _pins = pins;
        _tray = tray;
        _capture = capture;
        _settingsApplier = settingsApplier;
        _settings = settings;
        _track = track;
        _openSettings = openSettings;
        _restart = restart;
        _shutdown = shutdown;
        _clearHistory = clearHistory;
        _toast = toast;
        _ownerWindow = ownerWindow;
        _vaultUnlocked = vaultUnlocked;
        _showVault = showVault;
        _lockVault = lockVault;
    }

    public void Handle(uint message, PasteTarget foreground)
    {
        if (message is NativeMethods.WmLButtonUp or NativeMethods.WmLButtonDblClk or NativeMethods.NinSelect)
        {
            var now = Environment.TickCount64;
            if (now - _lastTrayClick < 250)
            {
                return;
            }

            _lastTrayClick = now;
            if (_pins.AnyGhost)
            {
                _pins.RestoreGhosts();
                return;
            }
            if (_history.IsOpen)
            {
                _history.Dismiss();
            }
            else
            {
                _history.Present(foreground);
            }

            return;
        }

        if (message is not (NativeMethods.WmRButtonUp or NativeMethods.WmContextMenu))
        {
            return;
        }

        var settings = _settings();
        var ctx = new TrayMenuContext(
            settings.Paused,
            settings.RunAtStartup,
            settings.RegionHotkey,
            settings.HistoryHotkey,
            _pins.Count,
            _pins.AnyGhost,
            settings.ShowOsd,
            settings.OsdPosition,
            settings.OsdDisplay,
            settings.VaultHotkey,
            _vaultUnlocked());

        switch (_tray.ShowMenu(ctx))
        {
            case TrayCommand.CaptureNow:
                _track(_capture.CaptureAsync(foreground, 0));
                break;
            case TrayCommand.CaptureDelay1:
                _track(_capture.CaptureAsync(foreground, 1));
                break;
            case TrayCommand.CaptureDelay2:
                _track(_capture.CaptureAsync(foreground, 2));
                break;
            case TrayCommand.CaptureDelay3:
                _track(_capture.CaptureAsync(foreground, 3));
                break;
            case TrayCommand.CaptureDelay5:
                _track(_capture.CaptureAsync(foreground, 5));
                break;
            case TrayCommand.CaptureDelay10:
                _track(_capture.CaptureAsync(foreground, 10));
                break;
            case TrayCommand.ShowHistory:
                _history.Present(foreground);
                break;
            case TrayCommand.ShowVault:
                _showVault();
                break;
            case TrayCommand.LockVault:
                _lockVault();
                break;
            case TrayCommand.ClearHistory:
                PromptClearHistory();
                break;
            case TrayCommand.OpenFolder:
                OpenScreenshotFolder();
                break;
            case TrayCommand.ClosePins:
                _pins.CloseAll();
                break;
            case TrayCommand.RestoreGhostPins:
                _pins.RestoreGhosts();
                break;
            case TrayCommand.ToggleOsd:
                _settingsApplier.ToggleOsd();
                break;
            case TrayCommand.OsdPositionTopRight:
                _settingsApplier.SetOsdPosition("TopRight");
                break;
            case TrayCommand.OsdPositionTopLeft:
                _settingsApplier.SetOsdPosition("TopLeft");
                break;
            case TrayCommand.OsdDisplayPrimary:
                _settingsApplier.SetOsdDisplay("Primary");
                break;
            case TrayCommand.OsdDisplayFollowCursor:
                _settingsApplier.SetOsdDisplay("FollowCursor");
                break;
            case TrayCommand.TogglePause:
                _settingsApplier.SetPaused(!settings.Paused);
                break;
            case TrayCommand.ToggleStartup:
                _settingsApplier.ToggleStartup();
                break;
            case TrayCommand.ShowSettings:
                _openSettings().Show(null);
                break;
            case TrayCommand.Restart:
                _restart();
                break;
            case TrayCommand.Exit:
                _shutdown();
                break;
        }
    }

    private void PromptClearHistory()
    {
        var res = NativeMethods.MessageBox(
            _ownerWindow,
            "确定要清空所有未固定的剪贴板历史记录吗？",
            "SnapFlow - 清空历史记录",
            NativeMethods.MbYesNo | NativeMethods.MbIconQuestion | NativeMethods.MbDefButton2);
        if (res == NativeMethods.IdYes)
        {
            _clearHistory();
            _toast.Show("已清空剪贴板历史");
        }
    }

    private void OpenScreenshotFolder()
    {
        var folder = _settingsApplier.EffectiveSaveFolder();
        try
        {
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            _toast.Show("无法打开文件夹：" + ex.Message);
        }
    }
}
