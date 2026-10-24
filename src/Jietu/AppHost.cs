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
    private readonly SettingsApplier _settingsApplier;
    private readonly string? _settingsLoadError;
    private readonly string? _storeRecoveryMessage;
    private readonly ConcurrentDictionary<Task, byte> _background = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ClipIngestService _ingest;
    private readonly CaptureOrchestrator _capture;
    private readonly PinManager _pins;
    private long _lastTrayClick;
    private bool _shuttingDown;
    private Task? _purgeLoop;
    private readonly PostCapturePipeline _pipeline;
    private AiVisionWindow? _aiVisionWindow;

    // 设置实例由 SettingsApplier 持有；这里保持只读视图，TryApply 换新实例后所有读取点自动生效。
    private AppSettings _settings => _settingsApplier.Current;

    public AppHost(DispatcherQueue queue)
    {
        _queue = queue;
        AppPaths.Ensure();
        ScreenshotFiles.CleanupTemp();
        var settings = SettingsService.Load(AppPaths.SettingsFile);
        _settingsLoadError = SettingsService.LastLoadError;
        // 基础窗口与剪贴板先建：PinManager 依赖它们，而 HistoryStore 的文件占用回调
        // 又依赖 PinManager——必须保证首个后台清理任务启动时三者都已就位。
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
        _clipboard = new ClipboardService(_message.Handle) { Paused = settings.Paused };
        _pins = new PinManager(_clipboard, _toast, Track, OpenAiVision);
        _store = new HistoryStore(
            AppPaths.Database,
            AppPaths.Images,
            path => _pins.UsesFile(path));
        _storeRecoveryMessage = HistoryStore.LastRecoveryMessage;
        _ingest = new ClipIngestService(_store);
        // 识别在后台线程完成，经 UI 队列推给历史窗口原地刷新
        _ingest.Recognized += (id, text) => _queue.TryEnqueue(() => _history.NotifyRecognized(id, text));
        _history = new HistoryWindow(this);
        _clipboard.Captured += clip => Track(StoreClipAsync(clip));
        _tray = new TrayIcon(_message.Handle, settings.Paused);
        // Explorer 崩溃/重启后托盘图标会被系统清空，必须收到 TaskbarCreated 广播后重新添加，
        // 否则本应用没有主窗口，用户将再也无法打开托盘菜单（设置/退出都不可达）。
        _message.TaskbarCreated += () => _queue.TryEnqueue(() => _tray.ReAdd());
        _osd = new OsdWindow();
        _osd.SetPosition(settings.OsdPosition);
        _osd.SetTargetDisplay(settings.OsdDisplay);
        _osd.SetEnabled(settings.ShowOsd);
        _hardwareMonitor = new HardwareMonitorService();
        // 冷启动提速：Enabled=true 会同步触发 NVML/PDH 初始化（加载 nvml.dll、枚举适配器、
        // 建 PDH 查询），是启动路径上的重活。延迟几百毫秒再开始采样，OSD 头一两秒显示空指标
        // 完全可接受。DispatcherQueueTimer 回调在 UI 线程执行，与 ToggleOsd/SetOsdEnabled
        // 里的 Enabled 赋值天然串行，不存在并发初始化；Enabled setter 对相同值幂等。
        if (settings.ShowOsd)
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
        _capture = new CaptureOrchestrator(
            _queue,
            _overlay,
            _toast,
            _osd,
            _pipeline,
            _store,
            _ingest,
            _clipboard,
            () => _settings,
            () => _history.AppWindow.IsVisible || _settingsWindow?.AppWindow.IsVisible == true || _toast.AppWindow.IsVisible,
            () =>
            {
                _history.Dismiss();
                _settingsWindow?.Dismiss();
            },
            record => _history.NotifyInserted(record),
            RememberImage,
            PinFile,
            PinText,
            OpenAiVision,
            _lifetime.Token);
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
        // 设置的应用器最后创建（依赖上面全部就位）；启动清理任务从此挂到它上面
        _settingsApplier = new SettingsApplier(
            settings,
            _message.Handle,
            _store,
            _osd,
            _hardwareMonitor,
            _clipboard,
            _history,
            _tray,
            _toast,
            Track);
        Track(Task.Run(() => _settingsApplier.PurgeAll(settings.RetentionDays)));
    }

    public AppSettings Settings => _settings;
    public bool IsPaused => _settings.Paused;

    /// <summary>最近一次硬件采样（OSD 关闭时可能为 null），供设置页仿真预览使用。</summary>
    internal HardwareMetrics? CurrentMetrics => _osd.LastMetrics;
    internal ClipboardService Clipboard => _clipboard;
    internal ToastWindow Toast => _toast;

    public void Start()
    {
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
            Log.Info("已采纳注册表里的开机自启项。");
            _settingsApplier.Save();
        }

        if (_settingsLoadError != null)
        {
            // 设置文件损坏时 SettingsService 已把坏文件改名备份并回退默认值；这里补一条可见提示，
            // 并把默认配置写回磁盘，避免每次启动都重新报错。
            Log.Info("设置已恢复默认：" + _settingsLoadError);
            _settingsApplier.Save();
            _queue.TryEnqueue(() => _toast.Show("设置文件损坏，已备份并恢复默认"));
        }
        _purgeLoop = PurgeLoopAsync(_lifetime.Token);
        if (_storeRecoveryMessage != null)
        {
            // 历史库损坏时 HistoryStore 已把坏文件改名备份并重建；这里让用户知道旧记录被保留成了备份文件。
            Log.Info("历史库已恢复：" + _storeRecoveryMessage);
            _queue.TryEnqueue(() => _toast.Show(_storeRecoveryMessage));
        }
        var error = _settingsApplier.RegisterCurrent();
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
        Safe(_capture.CancelActive);
        Safe(() => _lifetime.Cancel());
        try
        {
            _purgeLoop?.Wait(1500);
        }
        catch (Exception ex)
        {
            Log.Info("停止清理：" + ex.Message);
        }

        Safe(_settingsApplier.UnregisterHotkeys);
        Safe(_clipboard.Unlisten);
        Safe(_tray.Dispose);
        Safe(_message.Destroy);
        Safe(SingleInstance.Stop);

        Safe(_pins.CloseAllOnShutdown);
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

    public void SetPaused(bool paused) => _settingsApplier.SetPaused(paused);

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

    public void CloseAllPins() => _pins.CloseAll();

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
                _settingsApplier.UnregisterHotkeys();
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

    public string? TryApply(AppSettings next) => _settingsApplier.TryApply(next);

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
                Track(_capture.CaptureAsync(foreground, 0));
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

        if (message is NativeMethods.WmRButtonUp or NativeMethods.WmContextMenu)
        {
            var pinCount = _pins.Count;
            var anyGhostPins = _pins.AnyGhost;

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
                    Track(_capture.CaptureAsync(foreground, 0));
                    break;
                case TrayCommand.CaptureDelay1:
                    Track(_capture.CaptureAsync(foreground, 1));
                    break;
                case TrayCommand.CaptureDelay2:
                    Track(_capture.CaptureAsync(foreground, 2));
                    break;
                case TrayCommand.CaptureDelay3:
                    Track(_capture.CaptureAsync(foreground, 3));
                    break;
                case TrayCommand.CaptureDelay5:
                    Track(_capture.CaptureAsync(foreground, 5));
                    break;
                case TrayCommand.CaptureDelay10:
                    Track(_capture.CaptureAsync(foreground, 10));
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
                    SetPaused(!_settings.Paused);
                    break;
                case TrayCommand.ToggleStartup:
                    _settingsApplier.ToggleStartup();
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

    public void PinFile(string? path) => _pins.PinFile(path);

    public void PinText(string? text) => _pins.PinText(text);

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

    public void RestoreGhostPins() => _pins.RestoreGhosts();

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
                    removed = _settingsApplier.PurgeAll(_settingsApplier.Current.RetentionDays);
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
}
