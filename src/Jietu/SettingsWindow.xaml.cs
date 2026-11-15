using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Globalization;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace Jietu;

public sealed partial class SettingsWindow : Window
{
    private readonly AppHost _host;
    private bool _allowClose;
    private bool _hiding;
    private bool _loading;

    private string _regionHotkey = AppSettings.DefaultRegionHotkey;
    private string _historyHotkey = AppSettings.DefaultHistoryHotkey;
    private string _vaultHotkey = AppSettings.DefaultVaultHotkey;
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
        ["Vault"] = ("密码库", "本地加密凭据与自动填充"),
        ["About"] = ("关于 SnapFlow", "版本信息与开源许可"),
    };

    public SettingsWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        WindowChrome.Apply(this, TitleBar, Root);
        GlassOverlays.Glassify(ErrorBar);
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
            EndRecording();
            var settings = _host.Settings;

            _regionHotkey = settings.RegionHotkey;
            _historyHotkey = settings.HistoryHotkey;
            _vaultHotkey = settings.VaultHotkey;
            RenderHotkeyKeycaps();

            RetentionBox.Value = settings.RetentionDays;
            StartupSwitch.IsOn = settings.RunAtStartup;
            SaveSwitch.IsOn = settings.SaveScreenshots;
            TerminalSwitch.IsOn = settings.RecordTerminals;
            OsdSwitch.IsOn = settings.ShowOsd;
            OsdHideOnCaptureSwitch.IsOn = settings.OsdHideOnCapture;
            UpdateOsdPreviewPosition();
            OsdDisplayBox.SelectedIndex = string.Equals(settings.OsdDisplay, "FollowCursor", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            MultiMonitorModeBox.SelectedIndex = settings.MultiMonitorCaptureMode switch
            {
                "AllMonitors" => 1,
                "ActiveMonitor" => 2,
                _ => 0
            };
            FolderBox.Text = string.IsNullOrWhiteSpace(settings.SaveFolder) ? ScreenshotFiles.DefaultFolder : settings.SaveFolder;
            AiUrlBox.Text = string.IsNullOrWhiteSpace(settings.AiApiUrl) ? AppSettings.DefaultAiApiUrl : settings.AiApiUrl;
            AiKeyBox.Password = settings.AiApiKey ?? "";
            AiModelBox.Text = string.IsNullOrWhiteSpace(settings.AiModel) ? AppSettings.DefaultAiModel : settings.AiModel;
            AiPromptBox.Text = string.IsNullOrWhiteSpace(settings.AiPrompt) ? AppSettings.DefaultAiPrompt : settings.AiPrompt;
            AiEconomySwitch.IsOn = settings.AiDetailLow;
            UpdateAiBadges();
            RefreshVaultSection();

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
        EndRecording();
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

    private void OnVaultHotkeyClick(object sender, RoutedEventArgs e)
    {
        StartRecording(VaultHotkeyBtn, VaultKeycapsHost);
    }

    private void StartRecording(Button btn, StackPanel host)
    {
        EndRecording();
        _recordingButton = btn;
        // 模板里的自定义录制态：品牌色描边 + 呼吸光晕；其余按钮进入禁用态避免同时录制。
        VisualStateManager.GoToState(btn, "Recording", true);
        foreach (var other in HotkeyButtons)
        {
            other.IsEnabled = other != btn;
        }
        host.Children.Clear();
        var recordingText = new TextBlock
        {
            Text = "按下新快捷键，Esc 取消",
            FontSize = 12.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["SnapFlowAccentBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        host.Children.Add(recordingText);
    }

    private void EndRecording()
    {
        foreach (var button in HotkeyButtons)
        {
            button.IsEnabled = true;
        }
        if (_recordingButton == null)
        {
            return;
        }

        // RecordingStates 组没有 Normal 态，用模板里约定的空 Idle 态把录制态带回去。
        VisualStateManager.GoToState(_recordingButton, "Idle", true);
        _recordingButton = null;
    }

    /// <summary>三颗可录制的热键按钮（截图 / 历史 / 密码库）：录制期间互斥禁用，结束整组恢复。</summary>
    private Button[] HotkeyButtons => [RegionHotkeyBtn, HistoryHotkeyBtn, VaultHotkeyBtn];

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
            EndRecording();
            RenderHotkeyKeycaps();
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
            EndRecording();
            RenderHotkeyKeycaps();
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
        else if (_recordingButton == VaultHotkeyBtn)
        {
            _vaultHotkey = formatted;
        }

        EndRecording();
        RenderHotkeyKeycaps();
        ApplyAndSave();
    }

    private void OnResetHotkeysClick(object sender, RoutedEventArgs e)
    {
        EndRecording();
        _regionHotkey = AppSettings.DefaultRegionHotkey;
        _historyHotkey = AppSettings.DefaultHistoryHotkey;
        _vaultHotkey = AppSettings.DefaultVaultHotkey;
        RenderHotkeyKeycaps();
        ApplyAndSave();
    }

    /// <summary>三个热键的键帽整组重绘：任一改动都要三行一起刷，避免只更新录到的那一行。</summary>
    private void RenderHotkeyKeycaps()
    {
        RenderKeycaps(RegionKeycapsHost, _regionHotkey);
        RenderKeycaps(HistoryKeycapsHost, _historyHotkey);
        RenderKeycaps(VaultKeycapsHost, _vaultHotkey);
    }

    private void RenderKeycaps(StackPanel host, string hotkey)
    {
        host.Children.Clear();
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            host.Children.Add(new TextBlock { Text = "未设置", FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
            UpdateResetHotkeysState();
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

        UpdateResetHotkeysState();
    }

    /// <summary>已是默认热键时禁用「恢复默认」，避免一个无效按钮悬在组头。</summary>
    private void UpdateResetHotkeysState()
    {
        var changed = !string.Equals(_regionHotkey, AppSettings.DefaultRegionHotkey, StringComparison.OrdinalIgnoreCase)
                      || !string.Equals(_historyHotkey, AppSettings.DefaultHistoryHotkey, StringComparison.OrdinalIgnoreCase)
                      || !string.Equals(_vaultHotkey, AppSettings.DefaultVaultHotkey, StringComparison.OrdinalIgnoreCase);
        ResetHotkeysButton.IsEnabled = changed;
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

    /// <summary>
    /// NumberBox 内层输入框在框架模板里写死了 NumberBoxTextBoxStyle（本版本框架模板内部
    /// 的资源引用不回落到应用资源，覆盖键不生效），聚焦时会被切成框架白底。
    /// 加载后直接在视觉树里找到内层 TextBox 换成玻璃样式，一步到位。
    /// </summary>
    private void OnRetentionBoxLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (FindFirst<TextBox>(RetentionBox) is not { } inner)
            {
                return;
            }

            inner.Style = (Style)Application.Current.Resources["GlassBoxInnerStyle"];
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
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
                RetentionBox.Value = Math.Clamp(_host.Settings.RetentionDays, AppSettings.MinRetentionDays, AppSettings.MaxRetentionDays);
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
        // 除了禁用控件，整行一起降不透明度：让「目录跟着自动保存走」的联动一眼可见
        FolderRow.Opacity = enabled ? 1 : 0.45;
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

    /// <summary>输入框这条路：校验 + 明确反馈，避免「点了没反应」看起来像个假按钮。</summary>
    private void AddIgnoreProcess()
    {
        var raw = NewIgnoreInput.Text;
        if (string.IsNullOrWhiteSpace(raw))
        {
            NewIgnoreInput.Focus(FocusState.Programmatic);
            return;
        }

        if (TryAddIgnore(raw, out var message))
        {
            NewIgnoreInput.Text = "";
            RenderIgnoreChips();
            ApplyAndSave();
        }

        _host.Toast.Show(message);
    }

    /// <summary>
    /// 把一条输入并进排除列表。归一化与匹配都走 ProcessNames，保证「设置页加进去的」和
    /// 「剪贴板拦截时比对的」是同一套规则（粘贴完整路径、少写 .exe、别名换名都不会失效）。
    /// </summary>
    private bool TryAddIgnore(string raw, out string message)
    {
        var name = ProcessNames.NormalizeOne(raw);
        if (name.Length == 0)
        {
            message = "看不懂这个程序名：填文件名即可，例如 notepad.exe，也可以直接粘贴完整路径。";
            return false;
        }

        if (ProcessNames.CoveredBy(_ignoredProcesses, name))
        {
            message = $"{name} 已经在排除列表里了。";
            return false;
        }

        if (_ignoredProcesses.Count >= ProcessNames.MaxEntries)
        {
            message = $"最多排除 {ProcessNames.MaxEntries} 个程序，先移除几个再加。";
            return false;
        }

        _ignoredProcesses.Add(name);
        message = $"已排除 {name}，之后从它复制的内容不再进入历史{DescribeAliases(name)}。";
        return true;
    }

    /// <summary>别名提示：微信这类换过进程名的软件，排一个等于排两个，得让用户知道。</summary>
    private static string DescribeAliases(string name)
    {
        var aliases = ProcessNames.AliasesOf(name);
        return aliases.Count == 0 ? "" : $"（同时覆盖 {string.Join("、", aliases)}）";
    }

    private void RenderIgnoreChips()
    {
        IgnoreChipsContainer.Children.Clear();
        var hasItems = _ignoredProcesses.Count > 0;
        IgnoreChipsContainer.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        IgnoreEmptyHint.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
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
                _host.Toast.Show($"已恢复记录：{proc} 的复制会重新进入历史。");
            };
            sp.Children.Add(removeBtn);
            chip.Child = sp;
            var aliases = ProcessNames.AliasesOf(proc);
            if (aliases.Count > 0)
            {
                ToolTipService.SetToolTip(chip, $"同时覆盖 {string.Join("、", aliases)}");
            }

            IgnoreChipsContainer.Children.Add(chip);
        }

        // 快捷添加胶囊与列表状态同步：已排除的置灰，避免点了没反应的「假按钮」
        SyncQuickChip(WeChatQuickChip, "WeChat.exe");
        SyncQuickChip(TerminalQuickChip, "WindowsTerminal.exe");
        SyncQuickChip(OnePasswordQuickChip, "1Password.exe");
    }

    private void SyncQuickChip(Button chip, string proc)
    {
        // 别名同源也算已排除：微信 3.x/4.x 换过进程名，别让人以为「快捷添加点了没反应」。
        var added = ProcessNames.CoveredBy(_ignoredProcesses, proc);
        chip.IsEnabled = !added;
        ToolTipService.SetToolTip(chip, added ? "已在排除列表中" : $"一键排除 {proc}");
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
            VaultHotkey = _vaultHotkey.Trim(),
            VaultLockMinutes = VaultLockBox.SelectedItem is ComboBoxItem lockItem && int.TryParse(lockItem.Tag?.ToString(), out var lockMinutes) ? lockMinutes : 5,
            VaultBrowserIntegration = _host.Settings.VaultBrowserIntegration,
            RetentionDays = (int)Math.Clamp(Math.Round(retention), AppSettings.MinRetentionDays, AppSettings.MaxRetentionDays),
            Paused = _host.IsPaused,
            RunAtStartup = StartupSwitch.IsOn,
            SaveScreenshots = SaveSwitch.IsOn,
            RecordTerminals = TerminalSwitch.IsOn,
            SaveFolder = FolderBox.Text.Trim(),
            IgnoredProcesses = _ignoredProcesses.ToList(),
            ShowOsd = OsdSwitch.IsOn,
            OsdDisplay = (OsdDisplayBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Primary",
            OsdHideOnCapture = OsdHideOnCaptureSwitch.IsOn,
            MultiMonitorCaptureMode = (MultiMonitorModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Auto",
            AiApiUrl = string.IsNullOrWhiteSpace(AiUrlBox.Text) ? AppSettings.DefaultAiApiUrl : AiUrlBox.Text.Trim(),
            AiApiKey = AiKeyBox.Password.Trim(),
            AiModel = string.IsNullOrWhiteSpace(AiModelBox.Text) ? AppSettings.DefaultAiModel : AiModelBox.Text.Trim(),
            AiPrompt = string.IsNullOrWhiteSpace(AiPromptBox.Text) ? AppSettings.DefaultAiPrompt : AiPromptBox.Text.Trim(),
            AiDetailLow = AiEconomySwitch.IsOn,
        };

        var error = _host.TryApply(next);
        if (error != null)
        {
            // 热键非法会让整页保存失败；回滚到已生效的值，避免一次误录把后面所有保存都锁死。
            _regionHotkey = _host.Settings.RegionHotkey;
            _historyHotkey = _host.Settings.HistoryHotkey;
            _vaultHotkey = _host.Settings.VaultHotkey;
            RenderHotkeyKeycaps();
        }

        ShowError(error);
    }

    private void OnAiSettingChanged(object sender, RoutedEventArgs e)
    {
        UpdateAiBadges();
        if (_loading) return;
        ApplyAndSave();
    }

    // ---------------- 密码库设置 ----------------

    /// <summary>从会话实时取状态（打开/锁定随时可能变化，每次进页都刷新）。</summary>
    private void RefreshVaultSection()
    {
        if (VaultStatusText is null)
        {
            return;
        }

        var vault = _host.Vault;
        var minutes = _host.Settings.VaultLockMinutes;
        VaultLockBox.SelectedIndex = minutes switch
        {
            <= 0 => 5,
            <= 1 => 0,
            <= 5 => 1,
            <= 15 => 2,
            <= 30 => 3,
            <= 60 => 4,
            _ => 5,
        };
        RefreshBrowserIntegrationState();

        if (!vault.Exists)
        {
            SetVaultStatusTile("Amber");
            VaultStatusText.Text = "未创建（打开后按引导设置主密码）";
            VaultOpenButton.Content = "创建密码库";
            VaultHelloButton.IsEnabled = false;
            VaultHelloText.Text = "创建并解锁密码库后可启用";
            return;
        }

        if (vault.IsBlocked)
        {
            // 阻断态（过新/损坏）与"已锁定"必须区分：前者输密码也没用，指路才是正事。
            SetVaultStatusTile("Red");
            VaultStatusText.Text = "密码库不可用（校验失败或版本过新，密码库窗口中有处理指引）";
            VaultOpenButton.Content = "查看处理方式";
            VaultHelloButton.IsEnabled = false;
            VaultHelloText.Text = "请先在密码库窗口中按提示处理这个库文件";
            return;
        }

        if (vault.IsUnlocked)
        {
            SetVaultStatusTile("Green");
            VaultStatusText.Text = $"已解锁 · {vault.Data!.Entries.Count} 个条目";
            VaultOpenButton.Content = "打开密码库";
            VaultHelloButton.IsEnabled = true;
            VaultHelloButton.Content = vault.HasHelloBlob ? "关闭" : "启用";
            VaultHelloText.Text = vault.HasHelloBlob
                ? "已启用：锁定后仍可用指纹 / PIN 快速解锁（修改主密码后需重新启用）"
                : "用指纹/PIN 免输主密码（锁定后也可用）";
        }
        else
        {
            SetVaultStatusTile("Red");
            VaultStatusText.Text = "已锁定";
            VaultOpenButton.Content = "打开密码库";
            // 关闭快速解锁只删 Hello 副本与系统凭据，不需要库密钥 —— 锁定时也放开，
            // 这样「PIN 丢了 / 想立刻收回指纹解锁」不必先解锁。
            VaultHelloButton.IsEnabled = vault.HasHelloBlob;
            VaultHelloButton.Content = vault.HasHelloBlob ? "关闭" : "启用";
            VaultHelloText.Text = vault.HasHelloBlob
                ? "已启用 · 锁定时可用指纹 / PIN 解锁；关闭后只能用主密码"
                : "解锁密码库后即可启用";
        }
    }

    /// <summary>状态釉块按库状态着色：未创建琥珀 / 已锁定红 / 已解锁绿（与语义色板同源）。</summary>
    private void SetVaultStatusTile(string tone)
    {
        VaultStatusTile.Style = (Style)Root.Resources[$"RowTile{tone}Style"];
        VaultStatusIcon.Foreground = (Brush)Root.Resources[$"RowGlyph{tone}Brush"];
    }

    private void OnOpenVault(object sender, RoutedEventArgs e)
    {
        _host.ToggleVault();
        Dismiss();
    }

    private async void OnToggleHello(object sender, RoutedEventArgs e)
    {
        var vault = _host.Vault;
        // 关闭方向不需要库密钥（只是删本地副本与系统凭据），只有启用才必须先解锁。
        var disabling = vault.HasHelloBlob;
        if (!disabling && !vault.IsUnlocked)
        {
            ShowError("请先解锁密码库再启用快速解锁。");
            return;
        }

        VaultHelloButton.IsEnabled = false;
        try
        {
            if (vault.HasHelloBlob)
            {
                await vault.DisableHelloAsync();
                _host.Toast.Show("Windows Hello 快速解锁已关闭");
            }
            else if (!await vault.EnableHelloAsync())
            {
                ShowError("启用快速解锁失败：可能已在系统弹窗中取消。");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            ShowError("快速解锁操作失败：" + ex.Message);
        }
        finally
        {
            RefreshVaultSection();
        }
    }

    private void OnVaultLockChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        ApplyAndSave();
    }

    // ---------------- 浏览器自动填充集成 ----------------

    private void RefreshBrowserIntegrationState()
    {
        if (VaultBrowserStatus is null)
        {
            return;
        }

        var enabled = BrowserIntegration.IsEnabled() && _host.Settings.VaultBrowserIntegration;
        var configuredId = BrowserIntegration.GetConfiguredExtensionId();

        // 回填已关联的 ID：输入框没在编辑中时才覆盖，避免打断正在输入的用户。
        if (VaultExtIdBox.FocusState == FocusState.Unfocused && !string.IsNullOrEmpty(configuredId))
        {
            VaultExtIdBox.Text = configuredId;
        }
        else if (VaultExtIdBox.FocusState == FocusState.Unfocused && string.IsNullOrWhiteSpace(VaultExtIdBox.Text))
        {
            // 还没关联时按扩展文件夹路径预计算 ID：用户在浏览器里加载的就是这个文件夹，
            // ID 一致，免去去 chrome://extensions 复制粘贴这一步。
            var folder = BrowserIntegration.FindExtensionFolder();
            if (folder != null)
            {
                VaultExtIdBox.Text = BrowserIntegration.ComputeUnpackedExtensionId(folder);
            }
        }

        VaultBrowserStatus.Text = (enabled, configuredId) switch
        {
            (true, not null) => "已启用 · 已关联扩展 ID",
            (true, null) => "已启用，但尚未关联扩展 ID —— 浏览器扩展将无法连接，填入 ID 后点“启用”重新写入",
            _ => "未启用",
        };
        // 状态驱动显隐：装好之后这页安静下来，引导收起、禁用提到头部；
        // ID 手填这类兜底路径收进默认折叠的「手动配置」。
        VaultBrowserGuide.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        VaultTopDisableButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        VaultBrowserEnableButton.IsEnabled = !enabled || (!string.IsNullOrWhiteSpace(VaultExtIdBox.Text)
            && !string.Equals(VaultExtIdBox.Text.Trim(), configuredId, StringComparison.OrdinalIgnoreCase));

        // 浏览器检测行：装了哪个浏览器就显示哪一行。
        var browsers = BrowserIntegration.DetectBrowsers();
        VaultChromeRow.Visibility = browsers.Any(b => b.Id == "chrome") ? Visibility.Visible : Visibility.Collapsed;
        VaultEdgeRow.Visibility = browsers.Any(b => b.Id == "edge") ? Visibility.Visible : Visibility.Collapsed;

        // 最近取用（内存审计，只记 host 与结果）：让"扩展刚才在哪些站点取过凭据"可见。
        var audit = _host.VaultBridgeAudit;
        if (enabled && audit.Count > 0)
        {
            VaultAuditText.Visibility = Visibility.Visible;
            VaultAuditText.Text = "最近取用：" + string.Join("；", audit
                .TakeLast(3)
                .Reverse()
                .Select(r => $"{r.AtUtc.ToLocalTime():MM-dd HH:mm} {DescribeAuditRecord(r)}"));
        }
        else
        {
            VaultAuditText.Visibility = Visibility.Collapsed;
        }
    }

    private static string DescribeAuditRecord(VaultAuditRecord record) => record.Result switch
    {
        VaultAuditResult.Ok => record.Count > 0 ? $"{record.Host}（返回 {record.Count} 条）" : record.Host,
        VaultAuditResult.VaultLocked => $"{record.Host}（库已锁定，未返回）",
        VaultAuditResult.RateLimited => "请求过于频繁，已限流",
        VaultAuditResult.IntegrationDisabled => "集成已关闭，已拒绝",
        VaultAuditResult.Blocked => $"{record.Host}（库不可用，已拒绝）",
        VaultAuditResult.NotPaired => $"{record.Host}（未关联，已拒绝）",
        _ => record.Host,
    };

    /// <summary>
    /// 一键安装：写好 NMH 关联（ID 按扩展文件夹路径计算）→ 打开浏览器扩展页 →
    /// 弹出扩展文件夹，用户把文件夹拖进扩展页即完成，全程无需复制 ID。
    /// </summary>
    private async void OnInstallToBrowser(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string browserId })
        {
            return;
        }

        var folder = BrowserIntegration.FindExtensionFolder();
        if (folder == null)
        {
            ShowError("没有找到扩展文件夹（ext/snapflow-vault）。");
            return;
        }

        var id = BrowserIntegration.ComputeUnpackedExtensionId(folder);
        var error = BrowserIntegration.Enable(id);
        if (error != null)
        {
            ShowError(error);
            return;
        }

        _host.Settings.VaultBrowserIntegration = true;
        ApplyAndSave();
        RefreshBrowserIntegrationState();

        var target = browserId switch
        {
            "edge" => BrowserIntegration.DetectBrowsers().FirstOrDefault(b => b.Id == "edge"),
            _ => BrowserIntegration.DetectBrowsers().FirstOrDefault(b => b.Id == "chrome"),
        };
        if (target == null)
        {
            ShowError("没有找到这个浏览器，可能未安装。");
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target.ExePath, target.ExtensionsUrl)
            {
                UseShellExecute = true,
            });
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{folder}\"");
        }
        catch (Exception ex)
        {
            ShowError("无法启动浏览器：" + ex.Message);
            return;
        }

        await ShowSettingsInfoAsync(
            "最后一步：把文件夹拖进扩展页",
            "① 浏览器已打开扩展管理页，资源管理器已弹出并选中 snapflow-vault 文件夹\n"
            + "② 如果提示需要「开发者模式」，点允许即可\n"
            + "③ 把这个文件夹拖进扩展页，安装就完成了\n\n"
            + "装好后从浏览器工具栏的 SnapFlow 图标取用密码：当前站点一键填充、生成强密码；首次连接时桌面端会弹确认。");
    }

    /// <summary>设置页内的提示对话框（与 PasswordWindow 的 ShowAppConfirmAsync 同观感）。</summary>
    private async Task ShowSettingsInfoAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5,
                LineHeight = 20,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            },
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        dialog.Glassify();
        await dialog.ShowAsync();
    }

    /// <summary>手动配置面板的展开/收起（原来由 Expander 承担，现在自己管）。</summary>
    private void OnToggleVaultAdvanced(object sender, RoutedEventArgs e)
    {
        try
        {
            var show = VaultAdvancedPanel.Visibility != Visibility.Visible;
            VaultAdvancedPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            VaultAdvancedChevron.Glyph = show ? "\uE70E" : "\uE70D";
        }
        catch (Exception ex)
        {
            Log.Error($"切换手动配置面板失败：{ex.Message}");
        }
    }

    /// <summary>
    /// ComboBox 的下拉面板是 Popup 里另起的一层，控件本地 Resources 覆盖不到它（实测无效），
    /// 所以在每次展开时把 Popup 的底面板改成玻璃薄面，跟 Flyout 一致。
    /// </summary>
    private void OnComboDropDownOpened(object sender, object e)
    {
        try
        {
            if (sender is not ComboBox box) return;
            var popup = FindFirst<Popup>(box);
            if (popup?.Child is not FrameworkElement child)
            {
                return;
            }

            var panel = child is Border b ? b : FindFirst<Border>(child);
            if (panel is null)
            {
                return;
            }

            var light = box.ActualTheme == ElementTheme.Light;
            panel.Background = new SolidColorBrush(light
                ? Color.FromArgb(0xD9, 0xFB, 0xFC, 0xFF)
                : Color.FromArgb(0xEE, 0x1B, 0x20, 0x27));
            panel.BorderBrush = new SolidColorBrush(light
                ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x2E, 0xDC, 0xE6, 0xF2));
            panel.BorderThickness = new Thickness(1);
            panel.CornerRadius = new CornerRadius(10);

            // 面板里还套着不透明的 ListView / ScrollViewer 底，不清掉玻璃还是看不见
            ClearOpaqueBackdrops(child);
        }
        catch (Exception ex)
        {
            Log.Error($"下拉面板着色失败：{ex.Message}");
        }
    }

    private static void ClearOpaqueBackdrops(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var node = VisualTreeHelper.GetChild(root, i);
            if (node is Panel panel) panel.Background = null;
            if (node is Control control && node is not ComboBoxItem) control.Background = null;
            ClearOpaqueBackdrops(node);
        }
    }

    private static T? FindFirst<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            var nested = FindFirst<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    /// <summary>按扩展文件夹路径算出 ID 并填入，替代手工复制。</summary>
    private void OnVaultDetectExtensionId(object sender, RoutedEventArgs e)
    {
        var folder = BrowserIntegration.FindExtensionFolder();
        if (folder == null)
        {
            ShowError("没有找到扩展文件夹（ext/snapflow-vault）。如果你把扩展复制到了别处加载，请仍从 chrome://extensions 复制 ID 粘进来。");
            return;
        }

        VaultExtIdBox.Text = BrowserIntegration.ComputeUnpackedExtensionId(folder);
        RefreshBrowserIntegrationState();
        _host.Toast.Show("已按扩展文件夹路径算出 ID 并填入");
    }

    /// <summary>在资源管理器中打开扩展文件夹，省去在仓库里翻找。</summary>
    private void OnVaultOpenExtensionFolder(object sender, RoutedEventArgs e)
    {
        var folder = BrowserIntegration.FindExtensionFolder();
        if (folder == null)
        {
            ShowError("没有找到扩展文件夹（ext/snapflow-vault）。");
            return;
        }

        System.Diagnostics.Process.Start("explorer.exe", folder);
    }

    /// <summary>兼容粘贴完整 chrome-extension:// 链接或带空白的 ID。</summary>
    private static string NormalizeExtensionId(string input)
    {
        var id = input.Trim();
        const string prefix = "chrome-extension://";
        if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            id = id[prefix.Length..];
        }

        return id.TrimEnd('/').Trim();
    }

    private void OnVaultBrowserEnable(object sender, RoutedEventArgs e)
    {
        var extensionId = NormalizeExtensionId(VaultExtIdBox.Text);

        // Chromium 扩展 ID 是 32 个小写字母 a–p；空或格式不对都直接拦下，避免悄悄写入连不上的清单。
        if (extensionId.Length == 0)
        {
            ShowError("请先在 chrome://extensions 复制扩展 ID 并填入。");
            return;
        }

        if (extensionId.Length != 32 || !extensionId.All(c => c is >= 'a' and <= 'p'))
        {
            ShowError("扩展 ID 格式不正确：应为 32 位小写字母（a–p），可在 chrome://extensions 的扩展详情中复制。");
            return;
        }

        var error = BrowserIntegration.Enable(extensionId);
        if (error != null)
        {
            ShowError(error);
            return;
        }

        VaultExtIdBox.Text = extensionId;
        _host.Settings.VaultBrowserIntegration = true;
        ApplyAndSave();
        RefreshBrowserIntegrationState();
        _host.Toast.Show("浏览器集成已启用（扩展首次连接时会请求确认）");
    }

    private void OnVaultBrowserDisable(object sender, RoutedEventArgs e)
    {
        BrowserIntegration.Disable();
        _host.Settings.VaultBrowserIntegration = false;
        ApplyAndSave();
        RefreshBrowserIntegrationState();
        _host.Toast.Show("浏览器集成已禁用");
    }

    // 密钥与端点的实时状态徽标：PasswordBox 看不出是否已配置，端点写错也只在点“测试连接”时才暴露。
    private void UpdateAiBadges()
    {
        if (AiKeyBadge is null) return;

        var hasKey = !string.IsNullOrWhiteSpace(AiKeyBox.Password);
        AiKeyBadge.Text = hasKey ? "已配置" : "未配置";
        AiKeyBadge.Foreground = hasKey
            ? (Brush)Application.Current.Resources["SnapFlowSuccessBrush"]
            : (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];

        // 端点留空走默认值（TryResolveApiBase 内部兜底），只有真的填错了才标红。
        if (AiVisionService.TryResolveApiBase(AiUrlBox.Text, out _, out _))
        {
            AiUrlBadge.Visibility = Visibility.Collapsed;
        }
        else
        {
            AiUrlBadge.Text = "地址无效";
            AiUrlBadge.Foreground = (Brush)Application.Current.Resources["SnapFlowDangerBrush"];
            AiUrlBadge.Visibility = Visibility.Visible;
        }
    }

    private void OnResetAiPrompt(object sender, RoutedEventArgs e)
    {
        // 触发 AiPromptBox.TextChanged → OnAiSettingChanged 完成落盘，这里只改值。
        AiPromptBox.Text = AppSettings.DefaultAiPrompt;
    }

    private void UpdateOsdPreviewPosition()
    {
        if (OsdPreviewHost == null)
        {
            return;
        }

        // 缺口形卡片两侧留白对称，摆在预览区正中
        OsdPreviewHost.HorizontalAlignment = HorizontalAlignment.Center;
        OsdPreviewHost.Margin = new Thickness(16, 0, 16, 0);
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
        double raster = root.RasterizationScale > 0 ? root.RasterizationScale : 1.0;

        float natural = OsdPreviewRenderer.NaturalWidth(metrics);
        if (natural <= 1f)
        {
            return;
        }

        // 预览最多按 1:1 呈现（再放大就不是「实际大小」了）
        float scale = Math.Clamp((float)(availableDip * raster / natural), 0.35f, 1f);
        string signature = string.Create(CultureInfo.InvariantCulture,
            $"{metrics.Gpu:0}|{metrics.Cpu:0}|{metrics.Ram:0}|{metrics.Temp:0}|{metrics.VramGb:0.0}|" +
            $"{metrics.Ghz:0.00}|{metrics.Down:0}|{metrics.Up:0}|{metrics.Fps:0}|{metrics.FpsMeasured}|{scale:0.000}");
        if (string.Equals(signature, _osdPreviewSignature, StringComparison.Ordinal))
        {
            return;
        }

        _osdPreviewRendering = true;
        try
        {
            var bitmap = OsdPreviewRenderer.Render(scale, metrics);
            if (bitmap is not { } bmp)
            {
                return;
            }

            OsdPreviewImage.Source = await ImageBytes.PngToBitmapAsync(bmp.Png);
            OsdPreviewImage.Width = bmp.Width / raster;
            OsdPreviewImage.Height = bmp.Height / raster;

            // 舞台高度跟着图片走（含阴影留白），不留下与内容无关的大片空黑
            double stageHeight = Math.Round(bmp.Height / raster) + 4;
            if (Math.Abs(OsdPreviewStage.Height - stageHeight) > 0.5)
            {
                OsdPreviewStage.Height = stageHeight;
            }

            _osdPreviewSignature = signature;

            OsdPreviewHint.Text = "常态为紧凑态，鼠标悬停才展开为详细版；"
                + (_host.CurrentMetrics is null
                    ? "当前未开启屏幕性能悬浮窗，预览使用示例数据"
                    : metrics.FpsMeasured
                        ? "预览由桌面悬浮窗的真实渲染器生成，数值与实机同步"
                        : string.Create(CultureInfo.InvariantCulture,
                            $"预览由桌面悬浮窗的真实渲染器生成；当前环境读不到合成器帧计数，帧率格显示刷新率 {metrics.RefreshHz:0}Hz"));
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
            ("Vault", PanelVault),
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
        Motion.MoveY(panel, transform, 10 * direction, 0, 330, Motion.Ease);
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
        Motion.MoveY(SectionTitleHost, transform, 7, 0, 340, Motion.Ease);
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
        if (sender is not Button btn || btn.Tag is not string proc || string.IsNullOrWhiteSpace(proc))
        {
            return;
        }

        if (TryAddIgnore(proc, out var message))
        {
            RenderIgnoreChips();
            ApplyAndSave();
        }

        _host.Toast.Show(message);
    }

    /// <summary>点选列表：列出当前带窗口的程序，点一下就把它加进排除名单，不用知道 exe 叫什么。</summary>
    private void OnPickRunningApp(object sender, RoutedEventArgs e)
    {
        var apps = RunningApps.List();
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        var root = new StackPanel { Spacing = 8, Width = 380 };
        root.Children.Add(new TextBlock
        {
            Text = "点一下即可排除；之后从该程序复制的内容不会进入历史。",
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
        });

        if (apps.Count == 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = "没有检测到带窗口的运行中程序。把那个程序打开后再来选一次即可。",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            var wrap = new WrapPanel { MaxWidth = 372 };
            foreach (var app in apps)
            {
                wrap.Children.Add(BuildRunningAppChip(app, flyout));
            }

            root.Children.Add(new ScrollViewer
            {
                MaxHeight = 300,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = wrap,
            });
        }

        flyout.Content = root;
        flyout.ShowAt(PickRunningAppButton);
    }

    private Button BuildRunningAppChip(RunningApp app, Flyout flyout)
    {
        var excluded = ProcessNames.CoveredBy(_ignoredProcesses, app.ExeName);
        var chip = new Button
        {
            Content = Shorten(app.Display, 16),
            Style = (Style)Application.Current.Resources["FilterChipStyle"],
            Margin = new Thickness(0, 0, 6, 6),
            IsEnabled = !excluded,
        };
        ToolTipService.SetToolTip(chip, excluded ? $"已在排除列表中：{app.ExeName}" : $"{app.Title}\n{app.ExeName}");
        chip.Click += (_, _) =>
        {
            flyout.Hide();
            if (TryAddIgnore(app.ExeName, out var message))
            {
                RenderIgnoreChips();
                ApplyAndSave();
            }

            _host.Toast.Show(message);
        };
        return chip;
    }

    private static string Shorten(string text, int max)
        => text.Length <= max ? text : text[..(max - 1)] + "…";

    private async void OnTestAiConnection(object sender, RoutedEventArgs e)
    {
        var url = string.IsNullOrWhiteSpace(AiUrlBox.Text) ? AppSettings.DefaultAiApiUrl : AiUrlBox.Text.Trim();
        var key = AiKeyBox.Password.Trim();
        var model = string.IsNullOrWhiteSpace(AiModelBox.Text) ? AppSettings.DefaultAiModel : AiModelBox.Text.Trim();

        AiTestButton.IsEnabled = false;
        AiTestProgress.Visibility = Visibility.Visible;
        AiTestProgress.IsActive = true;
        AiTestStatusText.Text = "正在连通握手...";
        AiTestStatusText.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

        try
        {
            // 与 AiVisionService 同一份端点校验规则（绝对地址 + http 仅放行回环），不再各自维护一份。
            if (!AiVisionService.TryResolveApiBase(AiUrlBox.Text, out var baseUri, out var urlError))
            {
                AiTestStatusText.Text = "✕ " + urlError;
                AiTestStatusText.Foreground = (Brush)Application.Current.Resources["SnapFlowDangerBrush"];
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            if (!string.IsNullOrEmpty(key))
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            }

            // 用户可能直接粘贴了完整 chat 地址：先剥掉 /chat/completions 再拼 /models。
            const string ChatSuffix = "/chat/completions";
            var baseText = baseUri.AbsoluteUri;
            if (baseText.EndsWith(ChatSuffix, StringComparison.OrdinalIgnoreCase))
            {
                baseText = baseText[..^ChatSuffix.Length];
            }

            var resp = await client.GetAsync(baseText + "/models");
            sw.Stop();
            if (resp.IsSuccessStatusCode)
            {
                AiTestStatusText.Text = $"✓ 连通正常 ({sw.ElapsedMilliseconds}ms，端点握手成功)";
                AiTestStatusText.Foreground = (Brush)Application.Current.Resources["SnapFlowSuccessBrush"];
            }
            else
            {
                AiTestStatusText.Text = $"✕ 响应异常: {(int)resp.StatusCode} {resp.ReasonPhrase}（部分网关不提供 /models，此失败不代表视觉请求不可用）";
                AiTestStatusText.Foreground = (Brush)Application.Current.Resources["SnapFlowDangerBrush"];
            }
        }
        catch (Exception ex)
        {
            AiTestStatusText.Text = $"✕ 连接失败: {ex.Message}（部分网关不提供 /models，此失败不代表视觉请求不可用）";
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

    /// <summary>打开随包分发的第三方组件与数据来源声明（exe 同目录的 third-party-notices.md）。</summary>
    private async void OnOpenThirdPartyNotices(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "third-party-notices.md");
        try
        {
            if (!File.Exists(path))
            {
                await ShowSettingsInfoAsync("第三方声明",
                    "未找到 third-party-notices.md。\n该文件随安装包分发，位于 SnapFlow 安装目录；也可在源码仓库的 docs/ 目录查看。");
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Info("打开第三方声明失败：" + ex.Message);
        }
    }
}