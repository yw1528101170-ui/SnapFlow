namespace Jietu;

/// <summary>
/// 桌面贴图窗口的生命周期管理：创建/接线、注销、批量关闭、幽灵恢复与文件占用查询。
/// 从 AppHost 拆出；窗口事件接线（复制走后台、AI 识图、便签复制）只在这里出现一次。
/// </summary>
internal sealed class PinManager
{
    private readonly List<PinWindow> _pins = [];
    private readonly ClipboardService _clipboard;
    private readonly ToastWindow _toast;
    private readonly Action<Task> _track;
    private readonly Action<byte[], string?> _openAiVision;

    public PinManager(
        ClipboardService clipboard,
        ToastWindow toast,
        Action<Task> track,
        Action<byte[], string?> openAiVision)
    {
        _clipboard = clipboard;
        _toast = toast;
        _track = track;
        _openAiVision = openAiVision;
    }

    /// <summary>当前贴图数量（线程安全快照，托盘菜单用）。</summary>
    public int Count
    {
        get
        {
            lock (_pins)
            {
                return _pins.Count;
            }
        }
    }

    /// <summary>是否存在被「防误触幽灵化」的贴图（托盘菜单用）。</summary>
    public bool AnyGhost
    {
        get
        {
            lock (_pins)
            {
                return _pins.Any(p => p.IsGhost);
            }
        }
    }

    /// <summary>历史清理前的文件占用判断：贴图正在展示的文件不能被当孤儿删掉。</summary>
    public bool UsesFile(string path)
    {
        lock (_pins)
        {
            return _pins.Any(pin => pin.UsesFile(path));
        }
    }

    /// <summary>
    /// 贴图窗口（文字/图片两种形态）共用的生命周期接线：关闭即注销、复制走后台、
    /// AI 识图开主窗口。
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
        window.CopyRequested += pinned => _track(CopyAsync(pinned));
        window.AiVisionRequested += bytes => _openAiVision(bytes, null);
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

    public void CloseAll()
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

    /// <summary>退出时批量关闭：与 CloseAll 不同，不打扰用户（退出路径不再弹 Toast）。</summary>
    public void CloseAllOnShutdown()
    {
        PinWindow[] pins;
        lock (_pins)
        {
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
                Log.Info("退出清理：" + ex.Message);
            }
        }
    }

    public void RestoreGhosts()
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

    private async Task CopyAsync(string path)
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
}
