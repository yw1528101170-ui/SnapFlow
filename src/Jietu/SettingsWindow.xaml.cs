using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Globalization;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;

namespace Jietu;

public sealed partial class SettingsWindow : Window
{
    private readonly AppHost _host;
    private bool _allowClose;
    private bool _hiding;
    private bool _loading;

    private string _regionHotkey = "Alt+A";
    private string _historyHotkey = "Alt+V";
    private readonly List<string> _ignoredProcesses = [];
    private Button? _recordingButton;

    // 保留天数防抖：NumberBox 每敲一位都会回调，攒到停顿后再落盘，避免连续写盘。
    // DispatcherQueueTimer 与 Windows.System 里的同名类型冲突，字段必须写全名。
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _retentionTimer;
    private bool _restoringRetention;

    // OSD 仿真预览：由真实渲染器出图，切片式刷新（签名不变就不重绘）
    private DispatcherTimer? _osdPreviewTimer;
    private bool _osdPreviewRendering;
    private string _osdPreviewSignature = string.Empty;
    private ScrollViewer? _activePanel;

    /// <summary>标题栏分区标题：与侧栏导航项一一对应。</summary>
    private static readonly Dictionary<string, (string Title, string Subtitle)> SectionCaptions = new()
    {
        ["Hotkeys"] = ("快捷按键", "全局呼出与操作手势"),
        ["General"] = ("常规与自启", "启动、存储与显示器策略"),
        ["Clipboard"] = ("剪贴板历史", "保留期限与隐私过滤"),
        ["Osd"] = ("性能监视器", "屏幕悬浮挂件 OSD"),
        ["Ai"] = ("AI 视觉配置", "多模态模型接入"),
        ["About"] = ("关于 SnapFlow", "版本信息与开源许可"),
    };

    public SettingsWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        WindowChrome.Apply(this, TitleBar, Root);
        AppWindow.Resize(new SizeInt32(824, 648));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 760;
            presenter.PreferredMinimumHeight = 560;
        }

        AppWindow.Closing += (_, e) =>
        {
            if (_allowClose || _hiding)
            {
                return;
            }

            e.Cancel = true;
            Dismiss();
        };

        OsdPreviewStage.SizeChanged += (_, _) => RefreshOsdPreview();
        SettingsNav.SelectedItem = SettingsNav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault();

        // 关于页与侧栏版本号跟随程序集（csproj 的 <Version>），升版时无需改这里。
        var ver = GetType().Assembly.GetName().Version;
        var verText = ver is null ? "?" : ver.ToString(3);
        AboutVersionText.Text = $"版本 {verText} · Windows App SDK & .NET 10";
        SidebarVersionText.Text = $"v{verText} · 设置中枢";
    }

    public void AllowClose() => _allowClose = true;

    public void Show(string? error)
    {
        _loading = true;
        try
        {
            _recordingButton = null;
            var settings = _host.Settings;

            _regionHotkey = settings.RegionHotkey;
            _historyHotkey = settings.HistoryHotkey;
            RenderKeycaps(RegionKeycapsHost, _regionHotkey);
            RenderKeycaps(HistoryKeycapsHost, _historyHotkey);

            RetentionBox.Value = settings.RetentionDays;
            StartupSwitch.IsOn = settings.RunAtStartup;
            SaveSwitch.IsOn = settings.SaveScreenshots;
            TerminalSwitch.IsOn = settings.RecordTerminals;
            OsdSwitch.IsOn = settings.ShowOsd;
            OsdHideOnCaptureSwitch.IsOn = settings.OsdHideOnCapture;
            OsdPositionBox.SelectedIndex = string.Equals(settings.OsdPosition, "TopLeft", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            UpdateOsdPreviewPosition();
            OsdDisplayBox.SelectedIndex = string.Equals(settings.OsdDisplay, "FollowCursor", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            MultiMonitorModeBox.SelectedIndex = settings.MultiMonitorCaptureMode switch
            {
                "AllMonitors" => 1,
                "ActiveMonitor" => 2,
                _ => 0
            };
            FolderBox.Text = string.IsNullOrWhiteSpace(settings.SaveFolder) ? ScreenshotFiles.DefaultFolder : settings.SaveFolder;
            AiUrlBox.Text = string.IsNullOrWhiteSpace(settings.AiApiUrl) ? "https://api.openai.com/v1" : settings.AiApiUrl;
            AiKeyBox.Password = settings.AiApiKey ?? "";
            AiModelBox.Text = string.IsNullOrWhiteSpace(settings.AiModel) ? "gpt-4o-mini" : settings.AiModel;
            AiPromptBox.Text = string.IsNullOrWhiteSpace(settings.AiPrompt)
                ? "请分析并总结这张截图的内容：若是代码请解释核心逻辑与排查建议，若是表格或图文请提取关键信息。"
                : settings.AiPrompt;

            _ignoredProcesses.Clear();
            _ignoredProcesses.AddRange(settings.IgnoredProcesses);
            RenderIgnoreChips();

            UpdateFolderEnabled();
            ShowError(error);
        }
        finally
        {
            // 中途任何一步抛异常都要把 _loading 收回来，否则设置页会永久失联（改什么都不保存）。
            _loading = false;
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindow.MoveAndResize(ScreenUtil.PlaceNearCursor(hwnd, 824, 648));
        Motion.WindowIn(Root);
        Activate();
    }

    public void Dismiss()
    {
        StopOsdPreview();
        // 防抖中的保留天数改动先落盘再隐窗，避免刚敲完数字就关窗导致改动被吞掉。
        if (_retentionTimer is { IsRunning: true })
        {
            _retentionTimer.Stop();
            ApplyAndSave();
        }

        _hiding = true;
        _recordingButton = null;
        AppWindow.Hide();
        _hiding = false;
    }

    private void OnRegionHotkeyClick(object sender, RoutedEventArgs e)
    {
        StartRecording(RegionHotkeyBtn, RegionKeycapsHost);
    }

    private void OnHistoryHotkeyClick(object sender, RoutedEventArgs e)
    {
        StartRecording(HistoryHotkeyBtn, HistoryKeycapsHost);
    }

    private void StartRecording(Button btn, StackPanel host)
    {
        _recordingButton = btn;
        host.Children.Clear();
        var recordingText = new TextBlock
        {
            Text = "按下新快捷键...",
            FontSize = 12.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["SnapFlowAccentBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        host.Children.Add(recordingText);
    }

    private void OnHotkeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_recordingButton == null)
        {
            return;
        }

        e.Handled = true;
        if (e.Key == VirtualKey.Escape)
        {
            // 取消录制
            _recordingButton = null;
            RenderKeycaps(RegionKeycapsHost, _regionHotkey);
            RenderKeycaps(HistoryKeycapsHost, _historyHotkey);
            ShowError(null);
            return;
        }

        if (IsModifier(e.Key))
        {
            return;
        }

        var mods = CurrentModifiers();
        if ((mods & (HotkeyGesture.ModAlt | HotkeyGesture.ModControl | HotkeyGesture.ModWin)) == 0)
        {
            ShowError("快捷键请至少包含 Alt、Ctrl 或 Win。");
            return;
        }

        var formatted = HotkeyGesture.Format(mods, (uint)e.Key);
        // 录制结果必须能被 TryParse 解析，否则写进配置会让整页保存永久失败。
        if (!HotkeyGesture.TryParse(formatted, out _, out var hotkeyError))
        {
            _recordingButton = null;
            RenderKeycaps(RegionKeycapsHost, _regionHotkey);
            RenderKeycaps(HistoryKeycapsHost, _historyHotkey);
            ShowError(string.IsNullOrWhiteSpace(hotkeyError) ? $"不支持把 {formatted} 设为快捷键。" : hotkeyError);
            return;
        }

        if (_recordingButton == RegionHotkeyBtn)
        {
            _regionHotkey = formatted;
        }
        else if (_recordingButton == HistoryHotkeyBtn)
        {
            _historyHotkey = formatted;
        }

        _recordingButton = null;
        RenderKeycaps(RegionKeycapsHost, _regionHotkey);
        RenderKeycaps(HistoryKeycapsHost, _historyHotkey);
        ApplyAndSave();
    }

    private void RenderKeycaps(StackPanel host, string hotkey)
    {
        host.Children.Clear();
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            host.Children.Add(new TextBlock { Text = "未设置", FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
            return;
        }

        var parts = hotkey.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                host.Children.Add(new TextBlock
                {
                    Text = "+",
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(1, 0, 1, 0),
                });
            }

            var keyBorder = new Border
            {
                Style = (Style)Application.Current.Resources["HotkeyKeycapStyle"],
                Child = new TextBlock
                {
                    Text = parts[i],
                    FontSize = 11.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            host.Children.Add(keyBorder);
        }
    }

    private void OnSettingToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ApplyAndSave();
    }

    private void OnSaveToggled(object sender, RoutedEventArgs e)
    {
        UpdateFolderEnabled();
        if (_loading) return;
        ApplyAndSave();
    }

    private void OnRetentionChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || _restoringRetention) return;

        if (double.IsNaN(args.NewValue) || double.IsInfinity(args.NewValue))
        {
            // 清空输入框时 NumberBox 会给出 NaN：提示后恢复成已保存的值，绝不把脏值提交出去。
            _retentionTimer?.Stop();
            _restoringRetention = true;
            try
            {
                RetentionBox.Value = Math.Clamp(_host.Settings.RetentionDays, 1, 3650);
            }
            finally
            {
                _restoringRetention = false;
            }

            ShowError("保留天数必须是 1~3650 的整数");
            return;
        }

        _retentionTimer ??= CreateRetentionTimer();
        _retentionTimer.Stop();
        _retentionTimer.Start();
    }

    /// <summary>保留天数的 600ms 防抖定时器：每次改动重置，停顿后才 ApplyAndSave。</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateRetentionTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(600);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => ApplyAndSave();
        return timer;
    }

    private void UpdateFolderEnabled()
    {
        var enabled = SaveSwitch.IsOn;
        FolderBox.IsEnabled = enabled;
        BrowseButton.IsEnabled = enabled;
        OpenFolderButton.IsEnabled = enabled;
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var path = FolderBox.Text;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = ScreenshotFiles.DefaultFolder;
        }

        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Info("打开文件夹失败：" + ex.Message);
        }
    }

    private async void OnBrowse(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder == null)
            {
                return;
            }

            FolderBox.Text = folder.Path;
            SaveSwitch.IsOn = true;
            UpdateFolderEnabled();
            ApplyAndSave();
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    }

    private void OnNewIgnoreKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            AddIgnoreProcess();
            e.Handled = true;
        }
    }

    private void OnAddIgnoreClick(object sender, RoutedEventArgs e)
    {
        AddIgnoreProcess();
    }

    private void AddIgnoreProcess()
    {
        var text = NewIgnoreInput.Text.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (!text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            text += ".exe";
        }

        if (!_ignoredProcesses.Contains(text, StringComparer.OrdinalIgnoreCase))
        {
            _ignoredProcesses.Add(text);
            RenderIgnoreChips();
            NewIgnoreInput.Text = "";
            ApplyAndSave();
        }
        else
        {
            NewIgnoreInput.Text = "";
        }
    }

    private void RenderIgnoreChips()
    {
        IgnoreChipsContainer.Children.Clear();
        IgnoreChipsScrollViewer.Visibility = _ignoredProcesses.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in _ignoredProcesses)
        {
            var proc = item;
            var chip = new Border
            {
                Style = (Style)Application.Current.Resources["TagChipItemStyle"],
            };

            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock
            {
                Text = proc,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var removeBtn = new Button
            {
                Width = 18,
                Height = 18,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Content = new FontIcon { Glyph = "\uE711", FontSize = 9, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] },
            };
            ToolTipService.SetToolTip(removeBtn, "移除此程序");
            removeBtn.Click += (_, _) =>
            {
                _ignoredProcesses.Remove(proc);
                RenderIgnoreChips();
                ApplyAndSave();
            };
            sp.Children.Add(removeBtn);
            chip.Child = sp;
            IgnoreChipsContainer.Children.Add(chip);
        }
    }

    private void ApplyAndSave()
    {
        if (_loading) return;

        var retention = RetentionBox.Value;
        if (double.IsNaN(retention) || double.IsInfinity(retention))
        {
            // 输入框被清空时给 NaN，(int)NaN 会变成脏值，这里用当前生效值兜底。
            retention = _host.Settings.RetentionDays;
        }

        var next = new AppSettings
        {
            RegionHotkey = _regionHotkey.Trim(),
            HistoryHotkey = _historyHotkey.Trim(),
            RetentionDays = (int)Math.Clamp(Math.Round(retention), 1, 3650),
            Paused = _host.IsPaused,
            RunAtStartup = StartupSwitch.IsOn,
            SaveScreenshots = SaveSwitch.IsOn,
            RecordTerminals = TerminalSwitch.IsOn,
            SaveFolder = FolderBox.Text.Trim(),
            IgnoredProcesses = _ignoredProcesses.ToList(),
                        ShowOsd = OsdSwitch.IsOn,
            OsdPosition = (OsdPositionBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "TopRight",
            OsdDisplay = (OsdDisplayBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Primary",
            OsdHideOnCapture = OsdHideOnCaptureSwitch.IsOn,
            MultiMonitorCaptureMode = (MultiMonitorModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Auto",
            AiApiUrl = string.IsNullOrWhiteSpace(AiUrlBox.Text) ? "https://api.openai.com/v1" : AiUrlBox.Text.Trim(),
            AiApiKey = AiKeyBox.Password.Trim(),
            AiModel = string.IsNullOrWhiteSpace(AiModelBox.Text) ? "gpt-4o-mini" : AiModelBox.Text.Trim(),
            AiPrompt = string.IsNullOrWhiteSpace(AiPromptBox.Text)
                ? "请分析并总结这张截图的内容：若是代码请解释核心逻辑与排查建议，若是表格或图文请提取关键信息。"
                : AiPromptBox.Text.Trim(),
        };

        var error = _host.TryApply(next);
        if (error != null)
        {
            // 热键非法会让整页保存失败；回滚到已生效的值，避免一次误录把后面所有保存都锁死。
            _regionHotkey = _host.Settings.RegionHotkey;
            _historyHotkey = _host.Settings.HistoryHotkey;
            RenderKeycaps(RegionKeycapsHost, _regionHotkey);
            RenderKeycaps(HistoryKeycapsHost, _historyHotkey);
        }

        ShowError(error);
    }

    private void OnAiSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ApplyAndSave();
    }

        private void OnOsdPositionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateOsdPreviewPosition();
        if (!_loading)
        {
            ApplyAndSave();
        }
    }

    private void UpdateOsdPreviewPosition()
    {
        if (OsdPreviewHost == null)
        {
            return;
        }

        var isTopRight = OsdPositionBox.SelectedIndex != 1;
        // 贴屏那一侧不留白（真实 OSD 就是贴边的），另一侧留出桌面空气，自由端曲线才看得清
        OsdPreviewHost.HorizontalAlignment = isTopRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        OsdPreviewHost.Margin = isTopRight ? new Thickness(26, 0, 6, 0) : new Thickness(6, 0, 26, 0);
        // 左/右停靠的端部几何是镜像的，位置一变必须重新出图
        _osdPreviewSignature = string.Empty;
        RefreshOsdPreview();
    }

    /// <summary>预览只在 OSD 面板可见时按秒刷新，离开面板即停表，不做无意义重绘。</summary>
    private void StartOsdPreview()
    {
        RefreshOsdPreview();
        _osdPreviewTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
        _osdPreviewTimer.Tick -= OnOsdPreviewTick;
        _osdPreviewTimer.Tick += OnOsdPreviewTick;
        _osdPreviewTimer.Start();
    }

    private void StopOsdPreview() => _osdPreviewTimer?.Stop();

    private void OnOsdPreviewTick(object? sender, object e) => RefreshOsdPreview();

    /// <summary>
    /// 用桌面悬浮窗真正在用的渲染器出图：先按预览区宽度反推合适缩放，
    /// 再渲染稳定态位图并解码给 Image。数值签名不变时直接跳过，避免每秒空转。
    /// </summary>
    private async void RefreshOsdPreview()
    {
        if (_osdPreviewRendering || OsdPreviewStage == null || OsdPreviewImage == null)
        {
            return;
        }

        double availableDip = OsdPreviewStage.ActualWidth - OsdPreviewHost.Margin.Left - OsdPreviewHost.Margin.Right;
        if (availableDip < 80 || OsdPreviewStage.XamlRoot is not { } root)
        {
            return;
        }

        var metrics = _host.CurrentMetrics is { } live ? OsdRenderer.Metrics.From(live) : OsdPreviewRenderer.Demo;
        bool topRight = OsdPositionBox.SelectedIndex != 1;
        double raster = root.RasterizationScale > 0 ? root.RasterizationScale : 1.0;

        float natural = OsdPreviewRenderer.NaturalWidth(topRight, metrics);
        if (natural <= 1f)
        {
            return;
        }

        // 预览最多按 1:1 呈现（再放大就不是「实际大小」了）
        float scale = Math.Clamp((float)(availableDip * raster / natural), 0.35f, 1f);
        string signature = string.Create(CultureInfo.InvariantCulture,
            $"{metrics.Gpu:0}|{metrics.Cpu:0}|{metrics.Ram:0}|{metrics.Temp:0}|{metrics.VramGb:0.0}|" +
            $"{metrics.Ghz:0.00}|{metrics.Down:0}|{metrics.Up:0}|{metrics.Fps:0}|{metrics.FpsMeasured}|{topRight}|{scale:0.000}");
        if (string.Equals(signature, _osdPreviewSignature, StringComparison.Ordinal))
        {
            return;
        }

        _osdPreviewRendering = true;
        try
        {
            var bitmap = OsdPreviewRenderer.Render(scale, topRight, metrics);
            if (bitmap is not { } bmp)
            {
                return;
            }

            var decoded = new BitmapImage();
            using (var stream = new InMemoryRandomAccessStream())
            {
                using (var writer = new DataWriter(stream))
                {
                    writer.WriteBytes(bmp.Png);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                    writer.DetachStream();
                }

                stream.Seek(0);
                await decoded.SetSourceAsync(stream);
            }

            OsdPreviewImage.Source = decoded;
            OsdPreviewImage.Width = bmp.Width / raster;
            OsdPreviewImage.Height = bmp.Height / raster;

            // 舞台高度跟着图片走（含阴影留白），不留下与内容无关的大片空黑
            double stageHeight = Math.Round(bmp.Height / raster) + 4;
            if (Math.Abs(OsdPreviewStage.Height - stageHeight) > 0.5)
            {
                OsdPreviewStage.Height = stageHeight;
            }

            _osdPreviewSignature = signature;

            OsdPreviewHint.Text = _host.CurrentMetrics is null
                ? "当前未开启屏幕性能悬浮窗，预览使用示例数据"
                : metrics.FpsMeasured
                    ? "预览由桌面悬浮窗的真实渲染器生成，数值与实机同步"
                    : string.Create(CultureInfo.InvariantCulture,
                        $"预览由桌面悬浮窗的真实渲染器生成；当前环境读不到合成器帧计数，帧率格显示刷新率 {metrics.RefreshHz:0}Hz");
        }
        catch (Exception ex)
        {
            Log.Info("OSD preview failed: " + ex.Message);
        }
        finally
        {
            _osdPreviewRendering = false;
        }
    }

    private void OnOsdDisplayChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
        {
            ApplyAndSave();
        }
    }

    private void OnMultiMonitorModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading)
        {
            ApplyAndSave();
        }
    }

    private void ShowError(string? error)
    {
        ErrorBar.Message = error ?? "";
        ErrorBar.IsOpen = !string.IsNullOrEmpty(error);
    }

    private static bool IsModifier(VirtualKey key)
    {
        return key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
            or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
            or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
            or VirtualKey.LeftWindows or VirtualKey.RightWindows;
    }

    private static uint CurrentModifiers()
    {
        uint mods = 0;
        if (Down(VirtualKey.Control) || Down(VirtualKey.LeftControl) || Down(VirtualKey.RightControl))
        {
            mods |= HotkeyGesture.ModControl;
        }

        if (Down(VirtualKey.Menu) || Down(VirtualKey.LeftMenu) || Down(VirtualKey.RightMenu))
        {
            mods |= HotkeyGesture.ModAlt;
        }

        if (Down(VirtualKey.Shift) || Down(VirtualKey.LeftShift) || Down(VirtualKey.RightShift))
        {
            mods |= HotkeyGesture.ModShift;
        }

        if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows))
        {
            mods |= HotkeyGesture.ModWin;
        }

        return mods;
    }

    private static bool Down(VirtualKey key)
    {
        return InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
        {
            return;
        }

        var panels = new (string Tag, ScrollViewer Panel)[]
        {
            ("Hotkeys", PanelHotkeys),
            ("General", PanelGeneral),
            ("Clipboard", PanelClipboard),
            ("Osd", PanelOsd),
            ("Ai", PanelAi),
            ("About", PanelAbout),
        };

        ScrollViewer? revealed = null;
        ScrollViewer? leaving = null;
        int from = -1;
        int to = -1;
        for (int i = 0; i < panels.Length; i++)
        {
            var (panelTag, panel) = panels[i];
            if (panel.Visibility == Visibility.Visible)
            {
                leaving = panel;
                from = i;
            }

            if (panelTag == tag)
            {
                revealed = panel;
                to = i;
            }
        }

        // 切换方向决定滑动方向：往下切内容从下方来，往上切则相反
        int direction = to >= from ? 1 : -1;
        _activePanel = revealed;

        if (leaving != null && leaving != revealed)
        {
            AnimatePanelOut(leaving, direction);
        }

        UpdateSectionCaption(tag);
        if (tag == "Osd")
        {
            StartOsdPreview();
        }
        else
        {
            StopOsdPreview();
        }

        if (revealed != null)
        {
            AnimatePanelIn(revealed, direction);
        }
    }

    /// <summary>
    /// 离场：先淡出并朝切换方向轻推 6px，结束后才折叠，
    /// 这样两个面板才是「交接」而不是「硬切」。
    /// </summary>
    private void AnimatePanelOut(ScrollViewer panel, int direction)
    {
        var transform = Motion.Prep(panel);
        double startY = transform.TranslateY;
        Motion.Fade(panel, 1, 0, 120, Motion.EaseIn, completed: () =>
        {
            // 快速来回切时它可能已被重新选中，此时不要把它折叠掉
            if (!ReferenceEquals(_activePanel, panel))
            {
                panel.Visibility = Visibility.Collapsed;
                transform.TranslateY = 0;
                Motion.Snap(panel, 1);
            }
        });
        Motion.MoveY(panel, transform, startY, -6 * direction, 130, Motion.EaseIn);
    }

    /// <summary>入场：面板整体从切换方向滑入，组卡片再按序错峰浮现。</summary>
    private void AnimatePanelIn(ScrollViewer panel, int direction)
    {
        panel.Visibility = Visibility.Visible;
        panel.ChangeView(null, 0, null, true);
        var transform = Motion.Prep(panel);
        transform.TranslateY = 10 * direction;
        Motion.Fade(panel, 0, 1, 120, Motion.Ease);
        Motion.MoveY(panel, transform, 10 * direction, 0, 330, Motion.Spring(0.1));
        RevealPanel(panel);
    }

    /// <summary>标题栏分区标题：淡入 + 轻微上浮，与内容切换保持同一节奏。</summary>
    private void UpdateSectionCaption(string tag)
    {
        if (!SectionCaptions.TryGetValue(tag, out var caption))
        {
            return;
        }

        SectionTitleBlock.Text = caption.Title;
        SectionSubtitleBlock.Text = caption.Subtitle;
        var transform = Motion.Prep(SectionTitleHost);
        transform.TranslateY = 7;
        Motion.Fade(SectionTitleHost, 0, 1, 210, Motion.Ease);
        Motion.MoveY(SectionTitleHost, transform, 7, 0, 340, Motion.Spring(0.16));
    }

    /// <summary>分组卡片依次错峰浮现，复刻 Apple 设置页的切页手感。</summary>
    private void RevealPanel(ScrollViewer panel)
    {
        if (panel.Content is not Panel host)
        {
            return;
        }

        for (var i = 0; i < host.Children.Count; i++)
        {
            if (host.Children[i] is FrameworkElement group)
            {
                Motion.RowIn(group, i);
            }
        }
    }

    private void OnQuickIgnoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string proc && !string.IsNullOrWhiteSpace(proc))
        {
            if (!_ignoredProcesses.Contains(proc, StringComparer.OrdinalIgnoreCase))
            {
                _ignoredProcesses.Add(proc);
                RenderIgnoreChips();
                ApplyAndSave();
            }
        }
    }

    private async void OnTestAiConnection(object sender, RoutedEventArgs e)
    {
        var url = string.IsNullOrWhiteSpace(AiUrlBox.Text) ? "https://api.openai.com/v1" : AiUrlBox.Text.Trim();
        var key = AiKeyBox.Password.Trim();
        var model = string.IsNullOrWhiteSpace(AiModelBox.Text) ? "gpt-4o-mini" : AiModelBox.Text.Trim();

        AiTestButton.IsEnabled = false;
        AiTestProgress.Visibility = Visibility.Visible;
        AiTestProgress.IsActive = true;
        AiTestStatusText.Text = "正在连通握手...";
        AiTestStatusText.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

        try
        {
            // 与 AiVisionService 同一规则：http 明文端点会把 API Key 明文出网，仅放行本机回环。
            if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUri)
                || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
            {
                AiTestStatusText.Text = "✕ 地址无效：请填写以 http:// 或 https:// 开头的完整接口地址";
                AiTestStatusText.Foreground = (Brush)Application.Current.Resources["SnapFlowDangerBrush"];
                return;
            }

            if (baseUri.Scheme == Uri.UriSchemeHttp && !AiVisionService.IsLoopbackHost(baseUri.Host))
            {
                AiTestStatusText.Text = "✕ 不支持 http:// 明文端点（API Key 会明文传输），请改用 https://；本地服务可用 http://127.0.0.1";
                AiTestStatusText.Foreground = (Brush)Application.Current.Resources["SnapFlowDangerBrush"];
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            if (!string.IsNullOrEmpty(key))
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            }
            var endpoint = url.TrimEnd('/') + "/models";
            var resp = await client.GetAsync(endpoint);
            sw.Stop();
            if (resp.IsSuccessStatusCode)
            {
                AiTestStatusText.Text = $"✓ 连通正常 ({sw.ElapsedMilliseconds}ms，端点握手成功)";
                AiTestStatusText.Foreground = (Brush)Application.Current.Resources["SnapFlowSuccessBrush"];
            }
            else
            {
                AiTestStatusText.Text = $"✕ 响应异常: {(int)resp.StatusCode} {resp.ReasonPhrase}";
                AiTestStatusText.Foreground = (Brush)Application.Current.Resources["SnapFlowDangerBrush"];
            }
        }
        catch (Exception ex)
        {
            AiTestStatusText.Text = $"✕ 连接失败: {ex.Message}";
            AiTestStatusText.Foreground = (Brush)Application.Current.Resources["SnapFlowDangerBrush"];
        }
        finally
        {
            AiTestButton.IsEnabled = true;
            AiTestProgress.IsActive = false;
            AiTestProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void OnOpenAppDir(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = AppContext.BaseDirectory;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Info("打开应用目录失败：" + ex.Message);
        }
    }
}