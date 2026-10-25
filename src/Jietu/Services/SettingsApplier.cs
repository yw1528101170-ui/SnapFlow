namespace Jietu;

/// <summary>
/// 设置的验证、持久化与运行时应用：此前这套逻辑长在 AppHost（TryApply + 热键注册 +
/// OSD/暂停/自启开关 + 保留期清理），是「上帝类」的第二大成分。当前设置实例的唯一持有者，
/// AppHost 经 <see cref="Current"/> 读取（TryApply 换新实例后所有读取点自动生效）。
/// </summary>
internal sealed class SettingsApplier
{
    private readonly IntPtr _hotkeyWindow;
    private readonly HistoryStore _store;
    private readonly OsdWindow _osd;
    private readonly HardwareMonitorService _hardwareMonitor;
    private readonly ClipboardService _clipboard;
    private readonly HistoryWindow _history;
    private readonly TrayIcon _tray;
    private readonly IToastSink _toast;
    private readonly Action<Task> _track;
    private AppSettings _current;

    public SettingsApplier(
        AppSettings initial,
        IntPtr hotkeyWindow,
        HistoryStore store,
        OsdWindow osd,
        HardwareMonitorService hardwareMonitor,
        ClipboardService clipboard,
        HistoryWindow history,
        TrayIcon tray,
        IToastSink toast,
        Action<Task> track)
    {
        _current = initial;
        _hotkeyWindow = hotkeyWindow;
        _store = store;
        _osd = osd;
        _hardwareMonitor = hardwareMonitor;
        _clipboard = clipboard;
        _history = history;
        _tray = tray;
        _toast = toast;
        _track = track;
    }

    /// <summary>当前生效的设置。TryApply 成功后整体替换为新实例。</summary>
    public AppSettings Current => _current;

    /// <summary>保存设置；写盘失败时必须让用户看见，而不是静默停在“内存已改、磁盘没写”的状态。</summary>
    public void Save()
    {
        if (!SettingsService.Save(AppPaths.SettingsFile, _current))
        {
            _toast.Show(SettingsService.LastSaveError ?? "设置无法写入磁盘。");
        }
    }

    public string? TryApply(AppSettings next)
    {
        if (!TryParseAll(next, out var shot, out var history, out var vault, out var error))
        {
            return error;
        }

        next.RetentionDays = Math.Clamp(next.RetentionDays, AppSettings.MinRetentionDays, AppSettings.MaxRetentionDays);
        next.IgnoredProcesses = ProcessNames.Normalize(next.IgnoredProcesses);
        next.SaveFolder = (next.SaveFolder ?? "").Trim();
        if (next.SaveScreenshots)
        {
            if (string.IsNullOrWhiteSpace(next.SaveFolder))
            {
                next.SaveFolder = ScreenshotFiles.DefaultFolder;
            }

            try
            {
                Directory.CreateDirectory(next.SaveFolder);
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                return "保存位置无法使用，请换一个文件夹。";
            }
        }

        error = Register(shot, history, vault);
        if (error != null)
        {
            RegisterCurrent();
            return error;
        }

        // 先落盘、再改运行时：写盘失败时回滚热键并保持旧设置，避免「界面看起来改了、重启后回退」。
        if (!SettingsService.Save(AppPaths.SettingsFile, next))
        {
            RegisterCurrent();
            return SettingsService.LastSaveError ?? "设置无法写入磁盘。";
        }

        var retentionChanged = next.RetentionDays != _current.RetentionDays;
        _current = next;
        _osd.SetEnabled(next.ShowOsd);
        _hardwareMonitor.Enabled = next.ShowOsd;
        _osd.SetPosition(next.OsdPosition);
        _osd.SetTargetDisplay(next.OsdDisplay);
        SensitiveForeground.SetIgnored(next.IgnoredProcesses);
        SensitiveForeground.SetRecordTerminals(next.RecordTerminals);
        _clipboard.Paused = next.Paused;
        StartupService.Apply(next.RunAtStartup);
        if (retentionChanged)
        {
            // 只有保留天数真的变了才清理，避免「改个开关就触发全库 Purge」。
            _track(Task.Run(() => PurgeAll(next.RetentionDays)));
        }
        _history.SetPaused(next.Paused);
        _tray.UpdateTip(next.Paused);
        return null;
    }

    public string? RegisterCurrent()
    {
        if (!TryParseAll(_current, out var shot, out var history, out var vault, out var error))
        {
            return error;
        }

        return Register(shot, history, vault);
    }

    private string? Register(HotkeyGesture shot, HotkeyGesture history, HotkeyGesture vault)
    {
        UnregisterHotkeys();
        var items = new (int Id, HotkeyGesture Gesture)[] { (1, shot), (2, history), (3, vault) };
        var failed = new List<string>();
        foreach (var item in items)
        {
            if (!NativeMethods.RegisterHotKey(_hotkeyWindow, item.Id, item.Gesture.Modifiers | NativeMethods.ModNoRepeat, item.Gesture.VirtualKey))
            {
                var code = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                failed.Add($"{item.Gesture.Display}（错误 {code}）");
            }
        }

        return failed.Count == 0 ? null : "这些热键已被其他软件占用：" + string.Join("、", failed) + "。请换一个。";
    }

    public void UnregisterHotkeys()
    {
        // Register 只注册 1（截屏）、2（历史）和 3（密码库），反注册范围保持一致即可
        for (var id = 1; id <= 3; id++)
        {
            NativeMethods.UnregisterHotKey(_hotkeyWindow, id);
        }
    }

    private static bool TryParseAll(
        AppSettings settings,
        out HotkeyGesture shot,
        out HotkeyGesture history,
        out HotkeyGesture vault,
        out string? error)
    {
        shot = history = vault = default;
        if (!HotkeyGesture.TryParse(settings.RegionHotkey, out shot, out error)) return false;
        if (!HotkeyGesture.TryParse(settings.HistoryHotkey, out history, out error)) return false;
        if (!HotkeyGesture.TryParse(settings.VaultHotkey, out vault, out error)) return false;
        error = HotkeyGesture.DuplicateMessage([shot, history, vault]);
        return error == null;
    }

    public void SetPaused(bool paused)
    {
        _current.Paused = paused;
        _clipboard.Paused = paused;
        Save();
        _history.SetPaused(paused);
        _tray.UpdateTip(paused);
        _toast.Show(paused ? "已暂停剪贴板记录" : "已恢复剪贴板记录");
    }

    public void ToggleStartup()
    {
        _current.RunAtStartup = !_current.RunAtStartup;
        StartupService.Apply(_current.RunAtStartup);
        Save();
        _toast.Show(_current.RunAtStartup ? "已开启开机自启动" : "已关闭开机自启动");
    }

    public void ToggleOsd()
    {
        _current.ShowOsd = !_current.ShowOsd;
        _osd.SetEnabled(_current.ShowOsd);
        _hardwareMonitor.Enabled = _current.ShowOsd;
        Save();
        _toast.Show(_current.ShowOsd ? "已开启性能悬浮窗" : "已关闭性能悬浮窗");
    }

    public void SetOsdEnabled(bool enabled)
    {
        if (_current.ShowOsd == enabled) return;
        _current.ShowOsd = enabled;
        _osd.SetEnabled(enabled);
        _hardwareMonitor.Enabled = enabled;
        Save();
    }

    public void SetOsdPosition(string position)
    {
        _current.OsdPosition = position;
        _osd.SetPosition(position);
        Save();
        _toast.Show(string.Equals(position, "TopLeft", StringComparison.OrdinalIgnoreCase) ? "悬浮窗已移至左上角" : "悬浮窗已移至右上角");
    }

    public void SetOsdDisplay(string display)
    {
        _current.OsdDisplay = display;
        _osd.SetTargetDisplay(display);
        Save();
        _toast.Show(string.Equals(display, "FollowCursor", StringComparison.OrdinalIgnoreCase) ? "悬浮窗将跟随鼠标所在屏幕" : "悬浮窗已固定在主显示器");
    }

    public void SetOsdHideOnCapture(bool hide)
    {
        _current.OsdHideOnCapture = hide;
        Save();
    }

    /// <summary>保存目录生效值：未自定义时落在「图片\SnapFlow」。</summary>
    public string EffectiveSaveFolder()
        => string.IsNullOrWhiteSpace(_current.SaveFolder) ? ScreenshotFiles.DefaultFolder : _current.SaveFolder;

    /// <summary>
    /// 历史库与「保存到文件夹」的截图共用同一套保留期：历史图片随库清理，
    /// 保存目录里的 SnapFlow-*.png 副本若不跟着清理，会无限累积、敏感截图长期留存。
    /// </summary>
    public int PurgeAll(int retentionDays)
    {
        var removed = _store.Purge(retentionDays);
        removed += ScreenshotFiles.PurgeSaved(EffectiveSaveFolder(), retentionDays);
        return removed;
    }
}
