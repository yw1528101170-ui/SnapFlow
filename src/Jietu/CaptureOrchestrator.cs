using Microsoft.UI.Dispatching;

namespace Jietu;

/// <summary>
/// 截图全流程编排：倒计时/取消 → OSD 与可见表面避让 → 抓屏 → 框选/长截图 → 后处理管线。
/// 从 AppHost 拆出（其单方法曾达 180 行、携带三份可变状态，是「上帝类」的主要成分）。
/// 本类属于 UI 协作层：持有窗口对象是职责所在；对「哪些表面需要避让」只通过两个委托感知，
/// 不再反向探入窗口内部可见性（原 AppHost 直拼三个窗口的 AppWindow.IsVisible）。
/// </summary>
internal sealed class CaptureOrchestrator
{
    private readonly DispatcherQueue _queue;
    private readonly OverlayWindow _overlay;
    private readonly ToastWindow _toast;
    private readonly OsdWindow _osd;
    private readonly PostCapturePipeline _pipeline;
    private readonly HistoryStore _store;
    private readonly ClipIngestService _ingest;
    private readonly ClipboardService _clipboard;
    private readonly Func<AppSettings> _settings;
    private readonly Func<bool> _anySurfaceVisible;
    private readonly Action _dismissSurfaces;
    private readonly Action<ClipRecord> _notifyInserted;
    private readonly Action<string, byte[]> _rememberImage;
    private readonly Action<string?> _pinFile;
    private readonly Action<string?> _pinText;
    private readonly Action<byte[], string?> _openAiVision;
    private readonly CancellationToken _lifetime;

    private CancellationTokenSource? _captureCts;
    private int _captureGeneration;
    private bool _counting;

    public CaptureOrchestrator(
        DispatcherQueue queue,
        OverlayWindow overlay,
        ToastWindow toast,
        OsdWindow osd,
        PostCapturePipeline pipeline,
        HistoryStore store,
        ClipIngestService ingest,
        ClipboardService clipboard,
        Func<AppSettings> settings,
        Func<bool> anySurfaceVisible,
        Action dismissSurfaces,
        Action<ClipRecord> notifyInserted,
        Action<string, byte[]> rememberImage,
        Action<string?> pinFile,
        Action<string?> pinText,
        Action<byte[], string?> openAiVision,
        CancellationToken lifetime)
    {
        _queue = queue;
        _overlay = overlay;
        _toast = toast;
        _osd = osd;
        _pipeline = pipeline;
        _store = store;
        _ingest = ingest;
        _clipboard = clipboard;
        _settings = settings;
        _anySurfaceVisible = anySurfaceVisible;
        _dismissSurfaces = dismissSurfaces;
        _notifyInserted = notifyInserted;
        _rememberImage = rememberImage;
        _pinFile = pinFile;
        _pinText = pinText;
        _openAiVision = openAiVision;
        _lifetime = lifetime;
    }

    /// <summary>退出时取消在途截图（倒计时/长截图滚动）并释放关联的 CTS。</summary>
    public void CancelActive()
    {
        _captureCts?.Cancel();
        _captureCts?.Dispose();
        _captureCts = null;
    }

    public async Task CaptureAsync(PasteTarget pasteBack, int delaySeconds)
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
        var captureCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
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
        var settings = _settings();
        var osdHide = settings.OsdHideOnCapture && _osd.IsVisible;
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

        var settle = _anySurfaceVisible() || osdHide;
        _overlay.CancelPick();
        _dismissSurfaces();
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
        var monitor = ScreenUtil.ShotMonitor(settings.MultiMonitorCaptureMode);
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
                settings,
                _store,
                _ingest,
                _clipboard,
                _toast,
                path => _pinFile(path),
                text => _pinText(text),
                _notifyInserted,
                (id, bytes) => _rememberImage(id, bytes),
                (png, prompt) => _openAiVision(png, prompt));
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
}
