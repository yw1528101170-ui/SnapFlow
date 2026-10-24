using System.Collections.Concurrent;
using Microsoft.UI.Dispatching;
using Windows.Graphics.Imaging;

namespace Jietu;

public sealed class AppHost
{
    private readonly DispatcherQueue _queue;
    private readonly HistoryWindow _history;
    // 惰性创建：XAML 控件树构建开销不小，首次真正需要（托盘“设置”、启动出错提示）时才建。
    private SettingsWindow? _settingsWindow;
    private readonly OverlayWindow _overlay;
    private readonly ToastWindow _toast;
    private readonly MessageWindow _message;
    private readonly ClipboardService _clipboard;
    private readonly TrayIcon _tray;
    private readonly HistoryStore _store;
    private readonly OsdWindow _osd;
    private readonly HardwareMonitorService _hardwareMonitor;
    private AppSettings _settings;
    private readonly string? _settingsLoadError;
    private readonly string? _storeRecoveryMessage;
    private readonly ConcurrentDictionary<Task, byte> _background = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ClipIngestService _ingest;
    private CancellationTokenSource? _captureCts;
    private readonly List<PinWindow> _pins = [];
    private int _captureGeneration;
    private bool _counting;
    private int _retentionDays;
    private long _lastTrayClick;
    private bool _shuttingDown;
    private Task? _purgeLoop;
    private readonly PostCapturePipeline _pipeline;
    private AiVisionWindow? _aiVisionWindow;

    public AppHost(DispatcherQueue queue)
    {
        _queue = queue;
        AppPaths.Ensure();
        ScreenshotFiles.CleanupTemp();
        _settings = SettingsService.Load(AppPaths.SettingsFile);
        _settingsLoadError = SettingsService.LastLoadError;
        _store = new HistoryStore(
            AppPaths.Database,
            AppPaths.Images,
            path =>
            {
                lock (_pins)
                {
                    return _pins.Any(pin => pin.UsesFile(path));
                }
            });
        _storeRecoveryMessage = HistoryStore.LastRecoveryMessage;
        _ingest = new ClipIngestService(_store);
        // 识别在后台线程完成，经 UI 队列推给历史窗口原地刷新
        _ingest.Recognized += (id, text) => _queue.TryEnqueue(() => _history.NotifyRecognized(id, text));
        Track(Task.Run(() => PurgeAll(_settings.RetentionDays)));
        _history = new HistoryWindow(this);
        _overlay = new OverlayWindow();
        _toast = new ToastWindow();
        _pipeline = new PostCapturePipeline([
            new AiVisionCaptureHandler(),
            new TranslateCaptureHandler(),
            new ExtractTextCaptureHandler(),
            new ExtractCodeCaptureHandler(),
            new ShareCaptureHandler(),
            new BitmapCaptureHandler(),
        ]);
        _message = new MessageWindow(
            id =>
            {
                var target = PasteTarget.Capture();
                NativeMethods.AllowSetForegroundWindow(Environment.ProcessId);
                _queue.TryEnqueue(() => OnHotkey(id, target));
            },
            () => _queue.TryEnqueue(NotifyClipboard),
            message =>
            {
                var target = PasteTarget.Capture();
                NativeMethods.AllowSetForegroundWindow(Environment.ProcessId);
                _queue.TryEnqueue(() => OnTray(message, target));
            });
        _clipboard = new ClipboardService(_message.Handle) { Paused = _settings.Paused };
        _clipboard.Captured += clip => Track(StoreClipAsync(clip));
        _tray = new TrayIcon(_message.Handle, _settings.Paused);
        // Explorer 崩溃/重启后托盘图标会被系统清空，必须收到 TaskbarCreated 广播后重新添加，
        // 否则本应用没有主窗口，用户将再也无法打开托盘菜单（设置/退出都不可达）。
        _message.TaskbarCreated += () => _queue.TryEnqueue(() => _tray.ReAdd());
        _osd = new OsdWindow();
        _osd.SetPosition(_settings.OsdPosition);
        _osd.SetTargetDisplay(_settings.OsdDisplay);
        _osd.SetEnabled(_settings.ShowOsd);
        _hardwareMonitor = new HardwareMonitorService();
        // 冷启动提速：Enabled=true 会同步触发 NVML/PDH 初始化（加载 nvml.dll、枚举适配器、
        // 建 PDH 查询），是启动路径上的重活。延迟几百毫秒再开始采样，OSD 头一两秒显示空指标
        // 完全可接受。DispatcherQueueTimer 回调在 UI 线程执行，与 ToggleOsd/SetOsdEnabled
        // 里的 Enabled 赋值天然串行，不存在并发初始化；Enabled setter 对相同值幂等。
        if (_settings.ShowOsd)
        {
            var delayedSampling = _queue.CreateTimer();
            delayedSampling.Interval = TimeSpan.FromMilliseconds(600);
            delayedSampling.IsRepeating = false;
            delayedSampling.Tick += (_, _) =>
            {
                delayedSampling.Stop();
                // 读取当前设置而不是启动时的快照：延迟窗口内用户可能已把 OSD 关掉。
                if (_settings.ShowOsd && !_shuttingDown)
                {
                    _hardwareMonitor.Enabled = true;
                }
            };
            delayedSampling.Start();
        }

        _hardwareMonitor.MetricsUpdated += metrics =>
        {
            _queue.TryEnqueue(() => _osd.UpdateMetrics(metrics));
        };
        _overlay.CopyTextRequested += text =>
        {
            try
            {
                _clipboard.SetText(text);
                _toast.Show("已复制颜色：" + text);
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        };
    }

    public AppSettings Settings => _settings;
    public bool IsPaused => _settings.Paused;

    /// <summary>最近一次硬件采样（OSD 关闭时可能为 null），供设置页仿真预览使用。</summary>
    internal HardwareMetrics? CurrentMetrics => _osd.LastMetrics;
    internal ClipboardService Clipboard => _clipboard;
    internal ToastWindow Toast => _toast;

    public void Start()
    {
        _retentionDays = _settings.RetentionDays;
        SensitiveForeground.SetIgnored(_settings.IgnoredProcesses);
        SensitiveForeground.SetRecordTerminals(_settings.RecordTerminals);
        _clipboard.Listen();
        // 安装向导勾选的自启项只写得到注册表：仅在首次初始化（设置文件尚不存在、还没有用户偏好）时采纳，
        // 回写设置文件后设置页 / 托盘菜单才会显示为已开启；之后严格以设置为准，
        // 不再因注册表残留值而复活，避免用户关掉的自启每次启动又被写回。
        var startup = StartupService.Reconcile(_settings.RunAtStartup, SettingsService.IsFirstRun);
        if (startup != _settings.RunAtStartup)
        {
            _settings.RunAtStartup = startup;
            Log.Info("已采纳注册表里的开机自启动项。");
            SaveSettings();
        }

        if (_settingsLoadError != null)
        {
            // 设置文件损坏时 SettingsService 已把坏文件改名备份并回退默认值；这里补一条可见提示，
            // 并把默认配置写回磁盘，避免每次启动都重新报错。
            Log.Info("设置已恢复默认：" + _settingsLoadError);
            SaveSettings();
            _queue.TryEnqueue(() => _toast.Show("设置文件损坏，已备份并恢复默认"));
        }
        _purgeLoop = PurgeLoopAsync(_lifetime.Token);
        if (_storeRecoveryMessage != null)
        {
            // 历史库损坏时 HistoryStore 已把坏文件改名备份并重建；这里让用户知道旧记录被保留成了备份文件。
            Log.Info("历史库已恢复：" + _storeRecoveryMessage);
            _queue.TryEnqueue(() => _toast.Show(_storeRecoveryMessage));
        }
        var error = RegisterCurrent();
        if (error != null)
        {
            Log.Error(error);
        }

        SingleInstance.Listen(() => _queue.TryEnqueue(() =>
        {
            _toast.Show($"SnapFlow 已在运行中 ({_settings.RegionHotkey} 截图, {_settings.HistoryHotkey} 历史)");
            _history.Present(PasteTarget.Capture());
        }));
        Track(Task.Run(CaptureService.Warmup));
        Track(Task.Run(() => _ingest.RefreshLegacyAsync(_lifetime.Token)));
        Log.Info($"SnapFlow ready hwnd={_message.Handle} {_settings.RegionHotkey} {_settings.HistoryHotkey}");
        if (error != null)
        {
            EnsureSettingsWindow().Show(error);
        }

    }

    public void Shutdown()
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;

        // 任何一步清理失败都不能阻断后面的步骤：必须保证进程最终能退出，
        // 否则会留下「无托盘、无热键、却占着 exe/history.db」的僵尸进程。
        Safe(() =>
        {
            _captureCts?.Cancel();
            _captureCts?.Dispose();
            _captureCts = null;
        });
        Safe(() => _lifetime.Cancel());
        try
        {
            _purgeLoop?.Wait(1500);
        }
        catch (Exception ex)
        {
            Log.Info("停止清理：" + ex.Message);
        }

        Safe(UnregisterHotkeys);
        Safe(_clipboard.Unlisten);
        Safe(_tray.Dispose);
        Safe(_message.Destroy);
        Safe(SingleInstance.Stop);

        PinWindow[] pins;
        lock (_pins)
        {
            pins = _pins.ToArray();
            _pins.Clear();
        }

        foreach (var pin in pins)
        {
            Safe(pin.Close);
        }

        Safe(_history.AllowClose);
        Safe(() => _settingsWindow?.AllowClose());
        Safe(() =>
        {
            _aiVisionWindow?.AllowClose();
            _aiVisionWindow?.Close();
        });
        Safe(_overlay.AllowClose);
        Safe(_toast.AllowClose);
        Safe(_toast.Dispose);

        // 排空后台写者（剪贴板入库 / 后台 OCR / 清理），再释放 SQLite 连接。
        // 否则在途任务可能撞上已释放的 SqliteConnection（退出时崩溃或丢最后一条记录）。
        try
        {
            var pending = _background.Keys.ToArray();
            if (pending.Length > 0)
            {
                Task.WaitAll(pending, 1500);
            }
        }
        catch (Exception ex)
        {
            Log.Info("等待后台任务退出：" + ex.Message);
        }

        Safe(_osd.Dispose);
        Safe(_hardwareMonitor.Dispose);
        Safe(_store.Dispose);
        try
        {
            Microsoft.UI.Xaml.Application.Current.Exit();
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    }

    /// <summary>退出清理专用：单步失败只记录日志，绝不中断后续释放。</summary>
    private static void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Info("退出清理：" + ex.Message);
        }
    }

    public IReadOnlyList<ClipRecord> Search(string? text, string filter, int limit, HistoryCursor? cursor)
        => _store.QueryPage(text, filter, limit, cursor);

    public void Delete(ClipRow row) => _store.Delete(row.Id);

    public void SetPinned(string id, bool pinned) => _store.SetPinned(id, pinned);

    public void ClearHistory()
    {
        _store.Clear();
        ClipRow.ClearThumbnails();
        _history.Reload();
    }

    public void SetPaused(bool paused)
    {
        _settings.Paused = paused;
        _clipboard.Paused = paused;
        SaveSettings();
        _history.SetPaused(paused);
        _tray.UpdateTip(paused);
        _toast.Show(paused ? "已暂停剪贴板记录" : "已恢复剪贴板记录");
    }

    /// <summary>记录后台任务，退出时最多等 1.5s 再释放 SQLite 连接；异常在这里观察掉，避免未观察异常。</summary>
    private void Track(Task task)
    {
        if (task.IsCompleted)
        {
            if (task.IsFaulted)
            {
                // 已完成即故障的任务不会走到下面的 ContinueWith，不补记就完全无人观察。
                Log.Info("后台任务失败：" + task.Exception?.GetBaseException().Message);
            }

            return;
        }

        _background[task] = 0;
        _ = task.ContinueWith(
            completed =>
            {
                if (completed.IsFaulted)
                {
                    Log.Info("后台任务失败：" + completed.Exception?.GetBaseException().Message);
                }

                _background.TryRemove(completed, out _);
            },
            TaskScheduler.Default);
    }

    /// <summary>保存设置；写盘失败时必须让用户看见，而不是静默停在“内存已改、磁盘没写”的状态。</summary>
    private void SaveSettings()
    {
        if (!SettingsService.Save(AppPaths.SettingsFile, _settings))
        {
            _toast.Show(SettingsService.LastSaveError ?? "设置无法写入磁盘。");
        }
    }

    public void CloseAllPins()
    {
        PinWindow[] pins;
        lock (_pins)
        {
            if (_pins.Count == 0)
            {
                return;
            }

            pins = _pins.ToArray();
            _pins.Clear();
        }

        foreach (var pin in pins)
        {
            try
            {
                pin.Close();
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }

        _toast.Show($"已关闭 {pins.Length} 个桌面贴图");
    }

    private void ToggleStartup()
    {
        _settings.RunAtStartup = !_settings.RunAtStartup;
        StartupService.Apply(_settings.RunAtStartup);
        SaveSettings();
        _toast.Show(_settings.RunAtStartup ? "已开启开机自启动" : "已关闭开机自启动");
    }

    /// <summary>保存目录生效值：未自定义时落在「图片\SnapFlow」。</summary>
    private string EffectiveSaveFolder()
        => string.IsNullOrWhiteSpace(_settings.SaveFolder) ? ScreenshotFiles.DefaultFolder : _settings.SaveFolder;

    /// <summary>
    /// 历史库与「保存到文件夹」的截图共用同一套保留期：历史图片随库清理，
    /// 保存目录里的 SnapFlow-*.png 副本若不跟着清理，会无限累积、敏感截图长期留存。
    /// </summary>
    private int PurgeAll(int retentionDays)
    {
        var removed = _store.Purge(retentionDays);
        removed += ScreenshotFiles.PurgeSaved(EffectiveSaveFolder(), retentionDays);
        return removed;
    }

    private void OpenScreenshotFolder()
    {
        var folder = EffectiveSaveFolder();
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

    private void PromptClearHistory()
    {
        var res = NativeMethods.MessageBox(
            _message.Handle,
            "确定要清空所有未固定的剪贴板历史记录吗？",
            "SnapFlow - 清空历史记录",
            NativeMethods.MbYesNo | NativeMethods.MbIconQuestion | NativeMethods.MbDefButton2);
        if (res == NativeMethods.IdYes)
        {
            ClearHistory();
            _toast.Show("已清空剪贴板历史");
        }
    }

    public void Restart()
    {
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe))
        {
            try
            {
                // 先放开热键与单实例互斥体，让新进程能立刻注册成功；随后再由 Shutdown 做完整清理。
                UnregisterHotkeys();
                SingleInstance.Stop();
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }

        Shutdown();
    }

    public string? TryApply(AppSettings next)
    {
        if (!TryParseAll(next, out var shot, out var history, out var error))
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

        error = Register(shot, history);
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

        var retentionChanged = next.RetentionDays != _settings.RetentionDays;
        _settings = next;
        _osd.SetEnabled(next.ShowOsd);
        _hardwareMonitor.Enabled = next.ShowOsd;
        _osd.SetPosition(next.OsdPosition);
        _osd.SetTargetDisplay(next.OsdDisplay);
        _retentionDays = next.RetentionDays;
        SensitiveForeground.SetIgnored(next.IgnoredProcesses);
        SensitiveForeground.SetRecordTerminals(next.RecordTerminals);
        _clipboard.Paused = next.Paused;
        StartupService.Apply(next.RunAtStartup);
        if (retentionChanged)
        {
            // 只有保留天数真的变了才清理，避免「改个开关就触发全库 Purge」。
            Track(Task.Run(() => PurgeAll(next.RetentionDays)));
        }
        _history.SetPaused(next.Paused);
        _tray.UpdateTip(next.Paused);
        return null;
    }

    public string? ReadText(string id) => _store.GetText(id);

    public Task PasteAsync(ClipRow row) => ApplyAsync(row, paste: true, pathOnly: false, plain: false);

    public Task PastePlainAsync(ClipRow row) => ApplyAsync(row, paste: true, pathOnly: false, plain: true);

    public Task CopyAsync(ClipRow row) => ApplyAsync(row, paste: false, pathOnly: false, plain: false);

    public Task CopyPathAsync(ClipRow row) => ApplyAsync(row, paste: false, pathOnly: true, plain: false);

        public void CopyText(string text)
    {
        _clipboard.SetText(text);
        _toast.Show("已复制文字");
    }

    public void CopyCodeMarkdown(string markdown, string language)
    {
        _clipboard.SetText(markdown);
        _toast.Show($"已提取代码并复制为 Markdown ({language})");
    }

    public Task CopyRecognizedTextAsync(ClipRow row)
    {
        var text = row.RecognizedText ?? ReadText(row.Id);
        if (!string.IsNullOrWhiteSpace(text))
        {
            CopyText(text);
        }

        return Task.CompletedTask;
    }

    private async Task ApplyAsync(ClipRow row, bool paste, bool pathOnly, bool plain)
    {
        var target = _history.PasteTarget;
        NativeMethods.AllowSetForegroundWindow(Environment.ProcessId);
        try
        {
            if (pathOnly)
            {
                var paths = ClipFiles.Paths(_store.GetText(row.Id));
                if (paths.Count == 0)
                {
                    _toast.Show("没有可复制的路径");
                    return;
                }

                _clipboard.SetText(string.Join(Environment.NewLine, paths));
            }
            else if (plain)
            {
                var text = _store.GetText(row.Id);
                if (string.IsNullOrEmpty(text))
                {
                    _toast.Show("这条没有纯文本");
                    return;
                }

                _clipboard.SetText(text);
            }
            else if (row.IsImage)
            {
                if (string.IsNullOrEmpty(row.ImagePath) || !File.Exists(row.ImagePath))
                {
                    _toast.Show("这条图片已经找不到了");
                    return;
                }

                var png = await File.ReadAllBytesAsync(row.ImagePath);
                using var bitmap = await ImageBytes.FromPngAsync(png);
                _clipboard.SetImage(png, ImageBytes.ReadBgra(bitmap), bitmap.PixelWidth, bitmap.PixelHeight);
            }
            else if (row.Kind == ClipKind.File)
            {
                var existing = ClipFiles.Paths(_store.GetText(row.Id)).Where(path => File.Exists(path) || Directory.Exists(path)).ToList();
                if (existing.Count == 0)
                {
                    _toast.Show("这些文件已经找不到了");
                    return;
                }

                _clipboard.SetFiles(existing);
            }
            else if (row.Kind == ClipKind.Rich)
            {
                var payload = _store.GetRich(row.Id);
                _clipboard.SetRich(_store.GetText(row.Id) ?? "", payload.Html, payload.Rtf);
            }
            else
            {
                var text = _store.GetText(row.Id);
                if (text == null)
                {
                    _toast.Show("这条内容已经找不到了");
                    return;
                }

                _clipboard.SetText(text);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            _toast.Show("写入剪贴板失败");
            return;
        }

        _history.Dismiss();
        if (!paste)
        {
            _toast.Show(pathOnly ? "已复制路径" : "已复制，可手动粘贴");
            return;
        }

        await Task.Delay(60);
        if (!await PasteService.TryPasteAsync(target))
        {
            _toast.Show("已复制到剪贴板，请手动粘贴");
        }
    }

    private void OnHotkey(int id, PasteTarget foreground)
    {
        switch (id)
        {
            case 1:
                Track(CaptureAsync(foreground, 0));
                break;
            case 2:
                if (_history.IsOpen)
                {
                    _history.Dismiss();
                }
                else
                {
                    _history.Present(foreground);
                }
                break;
        }
    }

    private void OnTray(uint message, PasteTarget foreground)
    {
        if (message is NativeMethods.WmLButtonUp or NativeMethods.WmLButtonDblClk or NativeMethods.NinSelect)
        {
            var now = Environment.TickCount64;
            if (now - _lastTrayClick < 250)
            {
                return;
            }

            _lastTrayClick = now;
            bool anyGhost;
            lock (_pins)
            {
                anyGhost = _pins.Any(p => p.IsGhost);
            }

            if (anyGhost)
            {
                RestoreGhostPins();
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

        if (message is NativeMethods.WmRButtonUp or NativeMethods.WmContextMenu)
        {
            int pinCount;
            bool anyGhostPins;
            lock (_pins)
            {
                pinCount = _pins.Count;
                anyGhostPins = _pins.Any(p => p.IsGhost);
            }

            var ctx = new TrayMenuContext(
                _settings.Paused,
                _settings.RunAtStartup,
                _settings.RegionHotkey,
                _settings.HistoryHotkey,
                pinCount,
                anyGhostPins,
                _settings.ShowOsd,
                _settings.OsdPosition,
                _settings.OsdDisplay);

            switch (_tray.ShowMenu(ctx))
            {
                case TrayCommand.CaptureNow:
                    Track(CaptureAsync(foreground, 0));
                    break;
                case TrayCommand.CaptureDelay1:
                    Track(CaptureAsync(foreground, 1));
                    break;
                case TrayCommand.CaptureDelay2:
                    Track(CaptureAsync(foreground, 2));
                    break;
                case TrayCommand.CaptureDelay3:
                    Track(CaptureAsync(foreground, 3));
                    break;
                case TrayCommand.CaptureDelay5:
                    Track(CaptureAsync(foreground, 5));
                    break;
                case TrayCommand.CaptureDelay10:
                    Track(CaptureAsync(foreground, 10));
                    break;
                case TrayCommand.ShowHistory:
                    _history.Present(foreground);
                    break;
                case TrayCommand.ClearHistory:
                    PromptClearHistory();
                    break;
                case TrayCommand.OpenFolder:
                    OpenScreenshotFolder();
                    break;
                case TrayCommand.ClosePins:
                    CloseAllPins();
                    break;
                case TrayCommand.RestoreGhostPins:
                    RestoreGhostPins();
                    break;
                case TrayCommand.ToggleOsd:
                    ToggleOsd();
                    break;
                case TrayCommand.OsdPositionTopRight:
                    SetOsdPosition("TopRight");
                    break;
                case TrayCommand.OsdPositionTopLeft:
                    SetOsdPosition("TopLeft");
                    break;
                case TrayCommand.OsdDisplayPrimary:
                    SetOsdDisplay("Primary");
                    break;
                case TrayCommand.OsdDisplayFollowCursor:
                    SetOsdDisplay("FollowCursor");
                    break;
                case TrayCommand.TogglePause:
                    SetPaused(!_settings.Paused);
                    break;
                case TrayCommand.ToggleStartup:
                    ToggleStartup();
                    break;
                case TrayCommand.ShowSettings:
                    EnsureSettingsWindow().Show(null);
                    break;
                case TrayCommand.Restart:
                    Restart();
                    break;
                case TrayCommand.Exit:
                    Shutdown();
                    break;
            }
        }
    }

    private void NotifyClipboard() => _clipboard.NotifyChanged();

    private async Task StoreClipAsync(CapturedClip clip)
    {
        try
        {
            var added = await _ingest.AddCapturedAsync(clip);
            if (added == null)
            {
                return;
            }

            if (added.Kind == ClipKind.Image && clip.Png != null)
            {
                var recognizedId = added.Id;
                var recognized = clip.Png;
                _queue.TryEnqueue(() => RememberImage(recognizedId, recognized));
            }

            _queue.TryEnqueue(() => _history.NotifyInserted(added));
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    }

    /// <summary>
    /// 贴图窗口（文字/图片两种形态）共用的生命周期接线：关闭即注销、复制走后台、
    /// AI 识图开主窗口。此前这段接线在 PinFile/PinText 里逐行重复，改一处漏一处。
    /// </summary>
    private PinWindow CreatePinnedWindow()
    {
        var window = new PinWindow();
        window.Dismissed += pin =>
        {
            lock (_pins)
            {
                _pins.Remove(pin);
            }
        };
        window.CopyRequested += pinned => Track(CopyPinnedAsync(pinned));
        window.AiVisionRequested += bytes => OpenAiVision(bytes, null);
        window.CopyTextRequested += copied =>
        {
            _clipboard.SetText(copied);
            _toast.Show("已复制便签文字");
        };
        return window;
    }

    public void PinFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            _toast.Show("这张图片已经找不到了");
            return;
        }

        var window = CreatePinnedWindow();
        try
        {
            window.ShowFile(path);
        }
        catch (Exception ex)
        {
            // 先加载成功再登记：避免加载失败时窗口永久留在 _pins 里，把文件“锁”在历史清理保护中。
            Log.Error(ex);
            window.Close();
            _toast.Show("这张图片无法加载，可能已被删除或占用");
            return;
        }

        lock (_pins)
        {
            _pins.Add(window);
        }
    }

    public void PinText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var window = CreatePinnedWindow();
        window.ShowText(text);
        lock (_pins)
        {
            _pins.Add(window);
        }
    }

    public void OpenAiVision(byte[] png, string? prompt = null)
    {
        _aiVisionWindow ??= new AiVisionWindow(this);
        _aiVisionWindow.Analyze(png, prompt);
    }

    public void ShowSettings(string? error = null)
    {
        EnsureSettingsWindow().Show(error);
    }

    /// <summary>
    /// 首次访问设置页时才构建窗口。所有调用点（托盘菜单、启动错误提示、ShowSettings、
    /// 截图前的收窗逻辑）都在 UI 线程上执行，不存在并发创建；窗口关闭走 Hide（Dismiss），
    /// 创建一次后常驻复用，与原行为一致。
    /// </summary>
    private SettingsWindow EnsureSettingsWindow()
    {
        return _settingsWindow ??= new SettingsWindow(this);
    }

    public void RestoreGhostPins()
    {
        PinWindow[] snapshot;
        lock (_pins)
        {
            snapshot = _pins.ToArray();
        }

        var restored = 0;
        foreach (var pin in snapshot)
        {
            if (pin.IsGhost)
            {
                pin.SetGhost(false);
                restored++;
            }
        }

        if (restored > 0)
        {
            _toast.Show($"已恢复 {restored} 个贴图的鼠标交互");
        }
    }

    private async Task CaptureAsync(PasteTarget pasteBack, int delaySeconds)
    {
        if (_counting && delaySeconds == 0)
        {
            _captureGeneration++;
            _captureCts?.Cancel();
            _counting = false;
            _toast.Show("已取消截图");
            return;
        }

        var generation = ++_captureGeneration;
        _captureCts?.Cancel();
        _captureCts?.Dispose();
        var captureCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _captureCts = captureCts;
        if (delaySeconds > 0)
        {
            _counting = true;
            try
            {
                for (var left = delaySeconds; left > 0; left--)
                {
                    if (generation != _captureGeneration)
                    {
                        return;
                    }

                    _toast.ShowSticky(left + " 秒后截图，再按一次截图热键会取消");
                    await Task.Delay(1000);
                }
            }
            finally
            {
                if (generation == _captureGeneration)
                {
                    _counting = false;
                }
            }

            if (generation != _captureGeneration)
            {
                return;
            }
        }

        Log.Info("开始截图");
        var osdHide = _settings.OsdHideOnCapture && _osd.IsVisible;
        if (osdHide)
        {
            // 先让胶囊缩回屏幕边缘（看得见的「主动让开」），再抓屏
            await _osd.RetractAsync();
            if (generation != _captureGeneration)
            {
                // 退场途中这次截图已被取消：立刻把胶囊放回视野，别让它一直消失
                _osd.OnCaptureEnded();
                return;
            }
        }
        var settle = _history.AppWindow.IsVisible || _settingsWindow?.AppWindow.IsVisible == true || _toast.AppWindow.IsVisible || osdHide;
        _overlay.CancelPick();
        _history.Dismiss();
        _settingsWindow?.Dismiss();
        // Toast 的隐藏是 300~500ms 的退场动画；必须等它真正隐藏，否则会被合成进截图。
        // ToastWindow 同时设置了 WDA_EXCLUDEFROMCAPTURE 作为双保险。
        await _toast.HideAsync();
        if (settle)
        {
            await Task.Delay(32);
            await Task.Run(NativeMethods.DwmFlush);
        }
        if (generation != _captureGeneration)
        {
            if (osdHide)
            {
                _osd.OnCaptureEnded();
            }

            return;
        }

        var windowTask = Task.Run(WindowPicker.Refresh);
        var monitor = ScreenUtil.ShotMonitor(_settings.MultiMonitorCaptureMode);
        CapturedFrame? frame = null;
        try
        {
            frame = await CaptureService.CaptureDesktopAsync(monitor);
            if (generation != _captureGeneration)
            {
                return;
            }

            if (frame == null)
            {
                _toast.Show("截图失败：画面可能处于锁屏、UAC 或受保护内容");
                return;
            }

            try
            {
                await windowTask;
            }
            catch (Exception ex)
            {
                Log.Info("预先刷新窗口树失败：" + ex.Message);
            }

            var captured = frame.Value;
            frame = null;
            var image = await _overlay.PickAsync(captured.Bitmap, captured.Pixels, monitor);
            if (generation != _captureGeneration || image == null)
            {
                return;
            }

                        if (image.LongCapture)
            {
                _toast.ShowSticky("正在自动滚动拼接长截图...");
                var grown = await Task.Run(() => LongShot.Capture(
                    image.ScreenX,
                    image.ScreenY,
                    image.Width,
                    image.Height,
                    (curStep, curHeight) =>
                    {
                        _queue.TryEnqueue(() => _toast.ShowSticky($"正在拼接长截图... 已捕获第 {curStep} 屏 ({curHeight}px)"));
                    },
                    captureCts.Token));

                if (generation != _captureGeneration)
                {
                    return;
                }

                if (grown is { } shot && shot.Height > image.Height)
                {
                    image = new PickedShot
                    {
                        Width = shot.Width,
                        Height = shot.Height,
                        Pixels = shot.Pixels,
                        Pin = image.Pin,
                    };
                    _toast.Show($"长截图拼接成功！(高度 {shot.Height}px)");
                }
                else
                {
                    _toast.Show("页面未发生滚动，按当前范围保存");
                }
            }

            var postContext = new PostCaptureContext(
                image,
                pasteBack,
                _settings,
                _store,
                _ingest,
                _clipboard,
                _toast,
                path => PinFile(path),
                text => PinText(text),
                record => _history.NotifyInserted(record),
                (id, bytes) => RememberImage(id, bytes),
                (png, prompt) => OpenAiVision(png, prompt));
            await _pipeline.ExecuteAsync(postContext);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            if (generation == _captureGeneration)
            {
                _toast.Show("截图失败");
            }
        }
        finally
        {
            frame?.Bitmap.Dispose();
            if (osdHide)
            {
                _osd.OnCaptureEnded();
            }
        }
    }

    private async Task CopyPinnedAsync(string path)
    {
        try
        {
            var png = await File.ReadAllBytesAsync(path);
            using var bitmap = await ImageBytes.FromPngAsync(png);
            _clipboard.SetImage(png, ImageBytes.ReadBgra(bitmap), bitmap.PixelWidth, bitmap.PixelHeight);
            _toast.Show("已复制");
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            _toast.Show("复制失败");
        }
    }

    private string? RegisterCurrent()
    {
        if (!TryParseAll(_settings, out var shot, out var history, out var error))
        {
            return error;
        }

        return Register(shot, history);
    }

    private string? Register(HotkeyGesture shot, HotkeyGesture history)
    {
        UnregisterHotkeys();
        var items = new (int Id, HotkeyGesture Gesture)[] { (1, shot), (2, history) };
        var failed = new List<string>();
        foreach (var item in items)
        {
            if (!NativeMethods.RegisterHotKey(_message.Handle, item.Id, item.Gesture.Modifiers | NativeMethods.ModNoRepeat, item.Gesture.VirtualKey))
            {
                var code = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                failed.Add($"{item.Gesture.Display}（错误 {code}）");
            }
        }

        return failed.Count == 0 ? null : "这些热键已被其他软件占用：" + string.Join("、", failed) + "。请换一个。";
    }

    private void UnregisterHotkeys()
    {
        // Register 只注册 1（截屏）和 2（历史），反注册范围保持一致即可
        for (var id = 1; id <= 2; id++)
        {
            NativeMethods.UnregisterHotKey(_message.Handle, id);
        }
    }

    private static bool TryParseAll(
        AppSettings settings,
        out HotkeyGesture shot,
        out HotkeyGesture history,
        out string? error)
    {
        shot = history = default;
        if (!HotkeyGesture.TryParse(settings.RegionHotkey, out shot, out error)) return false;
        if (!HotkeyGesture.TryParse(settings.HistoryHotkey, out history, out error)) return false;
        error = HotkeyGesture.DuplicateMessage([shot, history]);
        return error == null;
    }

    public void RecognizeImage(string id, byte[] png, bool force = false)
        => Track(Task.Run(() => _ingest.RecognizeAsync(id, png, force, _lifetime.Token)));

    private void RememberImage(string id, byte[] png)
        => Track(Task.Run(() => _ingest.RecognizeAsync(id, png, false, _lifetime.Token)));

    private async Task PurgeLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            // WaitForNextTickAsync 的取消续体同样不能回 UI 线程：Shutdown 在 UI 线程上
            // Wait(1500) 本任务，续体被阻塞就永远等不到自然结束（详见 RecognizeAsync 注释）。
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                int removed;
                try
                {
                    removed = PurgeAll(_retentionDays);
                }
                catch (Exception ex)
                {
                    Log.Error(ex);
                    continue;
                }

                if (removed > 0 && !_shuttingDown)
                {
                    _queue.TryEnqueue(() =>
                    {
                        if (!_shuttingDown)
                        {
                            _history.NotifyPurged();
                        }
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void ToggleOsd()
    {
        _settings.ShowOsd = !_settings.ShowOsd;
        _osd.SetEnabled(_settings.ShowOsd);
        _hardwareMonitor.Enabled = _settings.ShowOsd;
        SaveSettings();
        _toast.Show(_settings.ShowOsd ? "已开启性能悬浮窗" : "已关闭性能悬浮窗");
    }

    public void SetOsdEnabled(bool enabled)
    {
        if (_settings.ShowOsd == enabled) return;
        _settings.ShowOsd = enabled;
        _osd.SetEnabled(enabled);
        _hardwareMonitor.Enabled = enabled;
        SaveSettings();
    }

    public void SetOsdPosition(string position)
    {
        _settings.OsdPosition = position;
        _osd.SetPosition(position);
        SaveSettings();
        _toast.Show(string.Equals(position, "TopLeft", StringComparison.OrdinalIgnoreCase) ? "悬浮窗已移至左上角" : "悬浮窗已移至右上角");
    }

    public void SetOsdDisplay(string display)
    {
        _settings.OsdDisplay = display;
        _osd.SetTargetDisplay(display);
        SaveSettings();
        _toast.Show(string.Equals(display, "FollowCursor", StringComparison.OrdinalIgnoreCase) ? "悬浮窗将跟随鼠标所在屏幕" : "悬浮窗已固定在主显示器");
    }

    public void SetOsdHideOnCapture(bool hide)
    {
        _settings.OsdHideOnCapture = hide;
        SaveSettings();
    }
}
