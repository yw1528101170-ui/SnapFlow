using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Jietu;

/// <summary>列表行视图模型：只暴露展示字段，不携带密码明文。</summary>
public sealed class VaultRow
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
}

/// <summary>
/// 密码库主窗口：锁定态（内嵌解锁/创建视图）与解锁态（列表 + 编辑器）切换。
/// 安全约束：窗口防截屏（WDA_EXCLUDEFROMCAPTURE）；前台时 SensitiveForeground 暂停
/// 截图与剪贴板记录；复制走 SetTextSecret（不进任何剪贴板历史 + 定时清除）。
/// </summary>
public sealed partial class PasswordWindow : Window
{
    private readonly AppHost _host;
    private readonly VaultSession _vault;
    private bool _open;
    private string? _selectedId;
    // 正在编辑的条目是否已存在于库中（决定保存是更新还是插入）。
    private bool _editingExisting;
    // TOTP 验证码 1 秒刷新计时器。
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _totpTimer;

    public PasswordWindow(AppHost host)
    {
        _host = host;
        _vault = host.Vault;
        InitializeComponent();
        WindowChrome.Apply(this, TitleBar, Root);
        AppWindow.Title = "密码库";

        AppWindow.Closing += (AppWindow _, AppWindowClosingEventArgs e) =>
        {
            // 关闭一律转为隐藏：窗口复用，锁定策略由 VaultSession 管理。
            e.Cancel = true;
            Dismiss();
        };
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                SensitiveForeground.SetVaultWindow(IntPtr.Zero);
            }
            else
            {
                // 按真实 HWND 精确挡：前台是密码库时暂停截图与剪贴板记录。
                SensitiveForeground.SetVaultWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
                _vault.TouchActivity();
            }
        };

        // 空闲/锁屏/睡眠/托盘/桥都会触发 Locked（全部在 UI 线程回调）：
        // 立即切回解锁视图并清空编辑器，明文不能在锁定后还留在界面上。
        _vault.Locked += () => DispatcherQueue.TryEnqueue(RefreshState);
    }

    public bool IsOpen => _open;

    /// <summary>唤出窗口：未创建库时进创建流程，锁定时进解锁流程。</summary>
    public void Present()
    {
        RefreshState();
        if (!_open)
        {
            _open = true;
            AppWindow.IsShownInSwitchers = true;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            AppWindow.MoveAndResize(ScreenUtil.PlaceNearCursor(hwnd, 860, 600));
            // WDA_EXCLUDEFROMCAPTURE：BitBlt / DXGI / WGC 全部抓不到本窗口，
            // 同时也保证自家截图/贴图/OCR 功能不会把密码内容摄进去。
            NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WdaExcludeFromCapture);
            Activate();
        }
        else
        {
            Activate();
        }
    }

    public void Dismiss()
    {
        if (!_open)
        {
            return;
        }

        _open = false;
        AppWindow.Hide();
        AppWindow.IsShownInSwitchers = false;
        SensitiveForeground.SetVaultWindow(IntPtr.Zero);
        StopTotpTimer();
        // 编辑器里的明文不能留在控件里跨「关闭」存续：重置整个编辑区。
        ClearEditor();
    }

    // ---------------- 状态切换 ----------------

    private void RefreshState()
    {
        if (!_vault.IsUnlocked)
        {
            ShowUnlockPanel();
            return;
        }

        UnlockPanel.Visibility = Visibility.Collapsed;
        VaultPanel.Visibility = Visibility.Visible;
        LockBadge.Visibility = Visibility.Collapsed;
        RefreshList(string.IsNullOrEmpty(SearchBox.Text) ? null : SearchBox.Text);
    }

    private void ShowUnlockPanel()
    {
        VaultPanel.Visibility = Visibility.Collapsed;
        UnlockPanel.Visibility = Visibility.Visible;
        LockBadge.Visibility = Visibility.Visible;
        StopTotpTimer();
        // 锁定/关闭后编辑器里的明文与列表行都不能残留。
        ClearEditor();
        EntryList.ItemsSource = null;

        var exists = _vault.Exists;
        UnlockTitle.Text = exists ? "输入主密码解锁" : "创建密码库";
        UnlockModeBadge.Text = exists ? "本机加密存储" : "首次设置";
        UnlockSubtitle.Text = exists
            ? "密码库在本机加密存储，主密码无法找回。"
            : "主密码是唯一的解锁凭据：库内所有数据用它加密，忘记后无法恢复。";
        ConfirmBox.Visibility = exists ? Visibility.Collapsed : Visibility.Visible;
        StrengthPanel.Visibility = exists ? Visibility.Collapsed : Visibility.Visible;
        CreateWarning.IsOpen = !exists;
        HelloButton.Visibility = exists && _vault.HasHelloBlob ? Visibility.Visible : Visibility.Collapsed;
        HelloHint.Visibility = exists ? Visibility.Visible : Visibility.Collapsed;
        HelloHint.Text = _vault.HasHelloBlob
            ? "免输主密码：点击上方按钮用指纹 / PIN 快速解锁。"
            : "可在 设置 → 密码库 中启用 Windows Hello 快速解锁（指纹 / PIN 免输主密码）。";
        UnlockButton.Content = exists ? "解锁" : "创建密码库";
        UnlockError.IsOpen = false;
        UnlockError.Message = "";
        MasterBox.Password = "";
        ConfirmBox.Password = "";
        MasterBox.Focus(FocusState.Programmatic);
    }

    // ---------------- 创建态实时反馈：强度 / 要求清单 / 两次一致 ----------------

    private void OnMasterPasswordChanged(object sender, RoutedEventArgs e) => UpdateCreateStrength();

    private void OnConfirmPasswordChanged(object sender, RoutedEventArgs e) => UpdateMatchHint();

    private void UpdateCreateStrength()
    {
        if (StrengthPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        var password = MasterBox.Password;
        var (bits, classes) = PasswordGenerator.EstimatePasswordEntropy(password);
        var lengthOk = password.Length >= 12;
        var mixedOk = classes >= 3 && password.Length >= 8;

        StrengthBar.Value = Math.Min(bits, 100);
        (var label, var color) = (bits, lengthOk && mixedOk) switch
        {
            ( >= 90, true) => ("非常强", Colors.MediumSeaGreen),
            ( >= 60, true) => ("强", Colors.MediumSeaGreen),
            ( >= 40, _) => ("一般", Colors.Goldenrod),
            ( >= 1, _) => ("弱", Colors.Crimson),
            _ => ("", Colors.Gray),
        };
        StrengthLabel.Text = label;
        StrengthLabel.Foreground = new SolidColorBrush(color);
        StrengthBar.Foreground = new SolidColorBrush(color);
        StrengthText.Text = password.Length == 0
            ? "开始输入以评估强度"
            : $"约 {bits:F0} bit 熵 · {password.Length} 个字符";

        ReqLength.Text = (lengthOk ? "✓ " : "✕ ") + "至少 12 个字符";
        ReqMixed.Text = (mixedOk ? "✓ " : "✕ ") + "混合大小写、数字或符号更安全";
        var okBrush = new SolidColorBrush(Colors.MediumSeaGreen);
        var todoBrush = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
        ReqLength.Foreground = lengthOk ? okBrush : todoBrush;
        ReqMixed.Foreground = mixedOk ? okBrush : todoBrush;

        UpdateMatchHint();
    }

    private void UpdateMatchHint()
    {
        if (ConfirmBox.Visibility != Visibility.Visible)
        {
            return;
        }

        if (ConfirmBox.Password.Length == 0)
        {
            MatchHint.Visibility = Visibility.Collapsed;
            return;
        }

        MatchHint.Visibility = Visibility.Visible;
        if (MasterBox.Password == ConfirmBox.Password)
        {
            MatchHint.Text = "✓ 两次输入一致";
            MatchHint.Foreground = new SolidColorBrush(Colors.MediumSeaGreen);
        }
        else
        {
            MatchHint.Text = "两次输入不一致";
            MatchHint.Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
        }
    }

    // ---------------- 解锁 / 创建 / Hello ----------------

    private void OnMasterKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            OnUnlockClick(sender, e);
        }
    }

    private async void OnUnlockClick(object sender, RoutedEventArgs e)
    {
        var password = MasterBox.Password;
        if (password.Length == 0)
        {
            return;
        }

        try
        {
            if (_vault.Exists)
            {
                UnlockButton.IsEnabled = false;
                var (ok, error) = await _vault.TryUnlockAsync(password);
                UnlockButton.IsEnabled = true;
                if (ok)
                {
                    RefreshState();
                }
                else
                {
                    SetUnlockError(error);
                }
            }
            else
            {
                // 创建流程：先校验两次一致与强度，再走 Argon2 + 落盘。
                if (password != ConfirmBox.Password)
                {
                    SetUnlockError("两次输入的主密码不一致。");
                    return;
                }

                if (password.Length < 12)
                {
                    SetUnlockError("主密码太短，请至少使用 12 个字符。");
                    return;
                }

                UnlockButton.IsEnabled = false;
                await _vault.CreateAsync(password);
                UnlockButton.IsEnabled = true;
                RefreshState();
                _host.Toast.Show("密码库已创建");
            }
        }
        catch (Exception ex)
        {
            UnlockButton.IsEnabled = true;
            SetUnlockError(ex.Message);
        }
    }

    private bool SetUnlockError(string? message)
    {
        UnlockError.Message = message ?? "主密码不正确。";
        UnlockError.IsOpen = true;
        return false;
    }

    private async void OnHelloClick(object sender, RoutedEventArgs e)
    {
        var blob = _vault.ReadHelloBlob();
        if (blob == null)
        {
            return;
        }

        HelloButton.IsEnabled = false;
        var key = await HelloUnlock.UnwrapKeyAsync(blob);
        HelloButton.IsEnabled = true;
        if (key == null)
        {
            SetUnlockError("Windows Hello 验证未通过，请用主密码解锁。");
            return;
        }

        if (!_vault.TryUnlockWithKey(key, out var error))
        {
            SetUnlockError(error);
            return;
        }

        RefreshState();
    }

    /// <summary>启用快速解锁（设置页调用）：包一层 Hello 副本并落盘。</summary>
    public async Task EnableHelloAsync()
    {
        if (await _vault.EnableHelloAsync())
        {
            _host.Toast.Show("Windows Hello 快速解锁已启用");
        }
        else
        {
            _host.Toast.Show("未能启用快速解锁（可能已取消）");
        }
    }

    private void OnLockClick(object sender, RoutedEventArgs e)
    {
        _vault.Lock();
        _host.Toast.Show("密码库已锁定");
    }

    // ---------------- 导入 / 导出 / 备份 / 改主密码 ----------------

    private async void OnImportCsv(object sender, RoutedEventArgs e)
    {
        if (!_vault.IsUnlocked)
        {
            return;
        }

        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeFilter.Add(".csv");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file == null)
            {
                return;
            }

            var text = await Windows.Storage.FileIO.ReadTextAsync(file);
            var entries = VaultImportExport.ToEntries(VaultImportExport.ParseCsv(text));
            var added = VaultImportExport.MergeInto(_vault.Data!, entries);
            if (added > 0)
            {
                _vault.Save();
                RefreshList(string.IsNullOrEmpty(SearchBox.Text) ? null : SearchBox.Text);
            }

            _host.Toast.Show($"导入完成：新增 {added} 条，跳过重复 {entries.Count - added} 条");
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            _host.Toast.Show("导入失败：" + ex.Message);
        }
    }

    /// <summary>从加密备份恢复：与 CSV 导入相同的合并去重策略，不覆盖现有条目。</summary>
    private async void OnRestoreBackup(object sender, RoutedEventArgs e)
    {
        if (!_vault.IsUnlocked)
        {
            return;
        }

        Windows.Storage.StorageFile file;
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeFilter.Add(".spvault");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            file = await picker.PickSingleFileAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            _host.Toast.Show("打开文件失败：" + ex.Message);
            return;
        }

        if (file == null)
        {
            return;
        }

        var box = new Microsoft.UI.Xaml.Controls.PasswordBox { PlaceholderText = "备份口令" };
        var hint = new TextBlock
        {
            Text = "备份文件用导出时设置的备份口令加密（与主密码无关）。恢复按「标题+用户名」去重合并，不会覆盖现有条目。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
        };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(hint);
        panel.Children.Add(box);
        if (await ShowVaultDialog("从加密备份恢复", panel, "恢复") != ContentDialogResult.Primary)
        {
            return;
        }

        if (box.Password.Length == 0)
        {
            _host.Toast.Show("请输入备份口令");
            return;
        }

        try
        {
            var buffer = await Windows.Storage.FileIO.ReadBufferAsync(file);
            Windows.Security.Cryptography.CryptographicBuffer.CopyToByteArray(buffer, out var bytes);
            var backupData = await Task.Run(() => VaultImportExport.OpenEncryptedBackup(bytes, box.Password));
            if (backupData == null)
            {
                _host.Toast.Show("备份口令不正确或备份文件已损坏");
                return;
            }

            var added = VaultImportExport.MergeInto(_vault.Data!, backupData.Entries);
            if (added > 0)
            {
                _vault.Save();
                RefreshList(string.IsNullOrEmpty(SearchBox.Text) ? null : SearchBox.Text);
            }

            _host.Toast.Show($"恢复完成：新增 {added} 条，跳过已有 {backupData.Entries.Count - added} 条");
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            _host.Toast.Show("恢复失败：" + ex.Message);
        }
    }

    private async void OnBackupVault(object sender, RoutedEventArgs e)
    {
        if (!_vault.IsUnlocked)
        {
            return;
        }

        var panel = new StackPanel { Spacing = 10 };
        var hint = new TextBlock
        {
            Text = "备份文件将用下面的备份口令独立加密（与主密码无关）。口令丢失将无法恢复备份。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
        };
        var box = new Microsoft.UI.Xaml.Controls.PasswordBox { PlaceholderText = "备份口令（至少 8 位）" };
        var confirm = new Microsoft.UI.Xaml.Controls.PasswordBox { PlaceholderText = "再输入一次备份口令" };
        panel.Children.Add(hint);
        panel.Children.Add(box);
        panel.Children.Add(confirm);

        var dialog = await ShowVaultDialog("加密备份", panel, "导出备份");
        if (dialog != ContentDialogResult.Primary)
        {
            return;
        }

        if (box.Password.Length < 8)
        {
            _host.Toast.Show("备份口令至少 8 位");
            return;
        }

        if (box.Password != confirm.Password)
        {
            _host.Toast.Show("两次输入的备份口令不一致");
            return;
        }

        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedFileName = $"snapflow-vault-{DateTime.Now:yyyyMMdd}",
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeChoices.Add("SnapFlow 加密备份", [".spvault"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file == null)
            {
                return;
            }

            var blob = await Task.Run(() => VaultImportExport.BuildEncryptedBackup(_vault.Data!, box.Password));
            await Windows.Storage.FileIO.WriteBytesAsync(file, blob);
            _host.Toast.Show("备份已导出（加密）");
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            _host.Toast.Show("备份失败：" + ex.Message);
        }
    }

    private async void OnExportCsv(object sender, RoutedEventArgs e)
    {
        if (!_vault.IsUnlocked)
        {
            return;
        }

        var res = NativeMethods.MessageBox(
            WinRT.Interop.WindowNative.GetWindowHandle(this),
            "即将导出包含全部密码明文的 CSV 文件。\n\n任何拿到这个文件的人都能直接读出所有密码，请确认存放位置安全。",
            "SnapFlow - 明文导出",
            NativeMethods.MbOkCancel | NativeMethods.MbIconQuestion | NativeMethods.MbDefButton2);
        if (res != NativeMethods.IdOk)
        {
            return;
        }

        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedFileName = $"snapflow-passwords-{DateTime.Now:yyyyMMdd}",
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeChoices.Add("CSV 文件", [".csv"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file == null)
            {
                return;
            }

            var csv = await Task.Run(() => VaultImportExport.BuildCsv(_vault.Data!.Entries));
            await Windows.Storage.FileIO.WriteTextAsync(file, csv);
            _host.Toast.Show("已明文导出，请立即妥善保管或删除");
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            _host.Toast.Show("导出失败：" + ex.Message);
        }
    }

    private async void OnChangeMasterPassword(object sender, RoutedEventArgs e)
    {
        if (!_vault.IsUnlocked)
        {
            return;
        }

        var panel = new StackPanel { Spacing = 10 };
        var hint = new TextBlock
        {
            Text = "整库将用新主密码重新加密，Windows Hello 快速解锁会被禁用（需重新启用）。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
        };
        var newBox = new Microsoft.UI.Xaml.Controls.PasswordBox { PlaceholderText = "新主密码（至少 12 位）" };
        var confirmBox = new Microsoft.UI.Xaml.Controls.PasswordBox { PlaceholderText = "再输入一次新主密码" };
        panel.Children.Add(hint);
        panel.Children.Add(newBox);
        panel.Children.Add(confirmBox);

        var dialog = await ShowVaultDialog("修改主密码", panel, "确认修改");
        if (dialog != ContentDialogResult.Primary)
        {
            return;
        }

        if (newBox.Password.Length < 12)
        {
            _host.Toast.Show("主密码至少 12 位");
            return;
        }

        if (newBox.Password != confirmBox.Password)
        {
            _host.Toast.Show("两次输入的密码不一致");
            return;
        }

        try
        {
            await Task.Run(() => _vault.ChangeMasterPassword(newBox.Password));
            _host.Toast.Show("主密码已修改");
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            _host.Toast.Show("修改失败：" + ex.Message);
        }
    }

    /// <summary>窗口内模态对话框。窗口可能处于隐藏态，隐藏时退回 MessageBox 提示。</summary>
    private async Task<ContentDialogResult> ShowVaultDialog(string title, UIElement content, string primaryText)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        return await dialog.ShowAsync();
    }

    // ---------------- 列表 ----------------

    private void RefreshList(string? query)
    {
        if (!_vault.IsUnlocked)
        {
            return;
        }

        var data = _vault.Data!;
        var rows = new List<VaultRow>();
        foreach (var entry in data.Entries.OrderByDescending(e => e.UpdatedUtc))
        {
            if (query != null && !Matches(entry, query))
            {
                continue;
            }

            var subtitle = entry.Kind == "note" ? "安全笔记" : entry.Username;
            if (entry.Kind != "note" && !string.IsNullOrWhiteSpace(entry.Url))
            {
                subtitle += "  ·  " + entry.Url;
            }

            rows.Add(new VaultRow { Id = entry.Id, Title = string.IsNullOrWhiteSpace(entry.Title) ? "（无标题）" : entry.Title, Subtitle = subtitle });
        }

        var selected = _selectedId;
        EntryList.ItemsSource = rows;
        if (selected != null)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i].Id == selected)
                {
                    EntryList.SelectedIndex = i;
                    break;
                }
            }
        }

        EmptyPane.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool Matches(VaultEntry entry, string query)
        => entry.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
           || entry.Username.Contains(query, StringComparison.OrdinalIgnoreCase)
           || entry.Url.Contains(query, StringComparison.OrdinalIgnoreCase)
           || entry.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase));

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _vault.TouchActivity();
        RefreshList(string.IsNullOrEmpty(SearchBox.Text) ? null : SearchBox.Text);
    }

    private void OnEntrySelected(object sender, SelectionChangedEventArgs e)
    {
        if (EntryList.SelectedItem is not VaultRow row)
        {
            return;
        }

        _vault.TouchActivity();
        _selectedId = row.Id;
        var entry = _vault.Data?.Entries.FirstOrDefault(en => en.Id == row.Id);
        if (entry == null)
        {
            return;
        }

        LoadEditor(entry);
    }

    // ---------------- 编辑器 ----------------

    private bool IsNoteKind => KindCombo.SelectedIndex == 1;

    /// <summary>类型切换：安全笔记没有用户名/密码/网址/TOTP，收起凭据字段避免误填。</summary>
    private void OnKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CredentialFields is null)
        {
            return;
        }

        CredentialFields.Visibility = IsNoteKind ? Visibility.Collapsed : Visibility.Visible;
    }

    private void LoadEditor(VaultEntry entry)
    {
        DetailPane.Visibility = Visibility.Visible;
        EmptyPane.Visibility = Visibility.Collapsed;
        DetailHeader.Text = "条目详情";
        _editingExisting = true;
        KindCombo.SelectedIndex = entry.Kind == "note" ? 1 : 0;
        TitleBox.Text = entry.Title;
        UsernameBox.Text = entry.Username;
        PasswordBox.Password = entry.Password;
        PasswordTextBox.Text = entry.Password;
        UrlBox.Text = entry.Url;
        TagsBox.Text = string.Join(", ", entry.Tags);
        NotesBox.Text = entry.Notes;
        // TOTP 输入框显示 otpauth URI 或 Base32；note 类型不显示。
        TotpBox.Text = entry.TotpSecret;
        TotpDisplayRow.Visibility = string.IsNullOrEmpty(entry.TotpSecret) ? Visibility.Collapsed : Visibility.Visible;
        UpdateTotpDisplay();
        StartTotpTimer();
    }

    private void ClearEditor()
    {
        _selectedId = null;
        _editingExisting = false;
        DetailPane.Visibility = Visibility.Collapsed;
        KindCombo.SelectedIndex = 0;
        TitleBox.Text = "";
        UsernameBox.Text = "";
        PasswordBox.Password = "";
        PasswordTextBox.Text = "";
        UrlBox.Text = "";
        TotpBox.Text = "";
        TagsBox.Text = "";
        NotesBox.Text = "";
        HideReveal();
        StopTotpTimer();
    }

    private VaultEntry CollectEditor()
    {
        var tags = TagsBox.Text
            .Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var secret = TotpBox.Text.Trim();
        if (secret.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase))
        {
            secret = Totp.SecretFromOtpAuthUri(secret) ?? "";
        }

        var now = DateTimeOffset.UtcNow;
        var note = IsNoteKind;
        return new VaultEntry
        {
            Id = _editingExisting && _selectedId != null ? _selectedId : Guid.NewGuid().ToString("N"),
            Kind = note ? "note" : "login",
            Title = TitleBox.Text.Trim(),
            // 「显示密码」态下明文在 TextBox 里，PasswordBox 是旧的，取可见的那个。
            Username = note ? "" : UsernameBox.Text.Trim(),
            Password = note ? "" : (PasswordBox.Visibility == Visibility.Visible ? PasswordBox.Password : PasswordTextBox.Text),
            Url = note ? "" : UrlBox.Text.Trim(),
            TotpSecret = note ? "" : secret,
            Tags = tags,
            Notes = NotesBox.Text,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
    }

    private void OnNewEntry(object sender, RoutedEventArgs e)
    {
        if (!_vault.IsUnlocked)
        {
            return;
        }

        EntryList.SelectedIndex = -1;
        ClearEditor();
        _selectedId = null;
        _editingExisting = false;
        DetailPane.Visibility = Visibility.Visible;
        EmptyPane.Visibility = Visibility.Collapsed;
        DetailHeader.Text = "新建条目";
        TitleBox.Focus(FocusState.Programmatic);
    }

    private void OnSaveEntry(object sender, RoutedEventArgs e)
    {
        if (!_vault.IsUnlocked)
        {
            return;
        }

        var entry = CollectEditor();
        if (entry.Title.Length == 0)
        {
            _host.Toast.Show("标题不能为空");
            return;
        }

        var data = _vault.Data!;
        var index = data.Entries.FindIndex(en => en.Id == entry.Id);
        if (index >= 0)
        {
            entry.CreatedUtc = data.Entries[index].CreatedUtc;
            data.Entries[index] = entry;
        }
        else
        {
            data.Entries.Add(entry);
        }

        try
        {
            _vault.Save();
            _host.Toast.Show("已保存");
            DetailHeader.Text = "条目详情";
            _selectedId = entry.Id;
            _editingExisting = true;
            RefreshList(string.IsNullOrEmpty(SearchBox.Text) ? null : SearchBox.Text);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            _host.Toast.Show("保存失败：" + ex.Message);
        }
    }

    private void OnDeleteEntry(object sender, RoutedEventArgs e)
    {
        if (!_vault.IsUnlocked || _selectedId == null)
        {
            return;
        }

        var res = NativeMethods.MessageBox(
            WinRT.Interop.WindowNative.GetWindowHandle(this),
            "确定删除这个条目吗？删除后无法恢复。",
            "SnapFlow - 删除条目",
            NativeMethods.MbYesNo | NativeMethods.MbIconQuestion | NativeMethods.MbDefButton2);
        if (res != NativeMethods.IdYes)
        {
            return;
        }

        var data = _vault.Data!;
        data.Entries.RemoveAll(en => en.Id == _selectedId);
        try
        {
            _vault.Save();
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            _host.Toast.Show("保存失败：" + ex.Message);
        }

        ClearEditor();
        RefreshList(string.IsNullOrEmpty(SearchBox.Text) ? null : SearchBox.Text);
        _host.Toast.Show("条目已删除");
    }

    // ---------------- 密码显示 / 复制 ----------------

    private void OnToggleReveal(object sender, RoutedEventArgs e)
    {
        if (PasswordBox.Visibility == Visibility.Visible)
        {
            PasswordTextBox.Text = PasswordBox.Password;
            PasswordBox.Visibility = Visibility.Collapsed;
            PasswordTextBox.Visibility = Visibility.Visible;
            RevealButton.Content = "隐藏";
        }
        else
        {
            HideReveal();
        }
    }

    private void HideReveal()
    {
        PasswordBox.Password = PasswordTextBox.Text;
        PasswordBox.Visibility = Visibility.Visible;
        PasswordTextBox.Visibility = Visibility.Collapsed;
        RevealButton.Content = "显示";
    }

    /// <summary>当前编辑中的密码：显示态明文在 TextBox（可能被改过），否则取 PasswordBox。</summary>
    private string CurrentPassword => PasswordBox.Visibility == Visibility.Visible
        ? PasswordBox.Password
        : PasswordTextBox.Text;

    private void CopySecret(string text, string label)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            var seconds = _vault.Data?.ClipboardClearSeconds ?? 30;
            _host.Clipboard.SetTextSecret(text, seconds);
            _host.Toast.Show(seconds > 0 ? $"已复制{label}（{seconds} 秒后自动清除）" : $"已复制{label}");
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            _host.Toast.Show("复制失败：" + ex.Message);
        }
    }

    private void OnCopyPassword(object sender, RoutedEventArgs e) => CopySecret(CurrentPassword, "密码");

    private void OnCopyUsername(object sender, RoutedEventArgs e) => CopySecret(UsernameBox.Text, "用户名");

    // ---------------- 生成器 ----------------

    private void OnToggleGenerator(object sender, RoutedEventArgs e)
    {
        GeneratorPanel.Visibility = GeneratorPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (GeneratorPanel.Visibility == Visibility.Visible)
        {
            Generate();
        }
    }

    /// <summary>选项变化（长度/词数/短语/字符类开关）时立即重生成，所见即所得。</summary>
    private void OnGeneratorOptionChanged(object sender, RoutedEventArgs e) => Regenerate();

    private void OnGeneratorLengthChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => Regenerate();

    private void Regenerate()
    {
        var phrase = GenPassphrase.IsChecked == true;
        GenWordsBox.Visibility = phrase ? Visibility.Visible : Visibility.Collapsed;
        GenLengthBox.IsEnabled = !phrase;
        if (GeneratorPanel.Visibility == Visibility.Visible)
        {
            Generate();
        }
    }

    private void OnGeneratePassword(object sender, RoutedEventArgs e) => Generate();

    private void Generate()
    {
        if (GenPassphrase.IsChecked == true)
        {
            var words = Math.Clamp((int)GenWordsBox.Value, PasswordGenerator.MinWords, PasswordGenerator.MaxWords);
            GenOutput.Text = PasswordGenerator.GeneratePassphrase(words);
            GenEntropyText.Text = $"约 {PasswordGenerator.EstimatePassphraseEntropyBits(words):F0} bit 熵（{words} 个随机单词）";
            return;
        }

        var lower = GenLower.IsChecked == true;
        var upper = GenUpper.IsChecked == true;
        var digits = GenDigits.IsChecked == true;
        var symbols = GenSymbols.IsChecked == true;
        if (!lower && !upper && !digits && !symbols)
        {
            GenOutput.Text = "";
            GenEntropyText.Text = "至少选择一个字符类";
            return;
        }

        var length = Math.Clamp((int)GenLengthBox.Value, PasswordGenerator.MinLength, PasswordGenerator.MaxLength);
        GenOutput.Text = PasswordGenerator.Generate(length, lower, upper, digits, symbols);
        var bits = PasswordGenerator.EstimateEntropyBits(length, lower, upper, digits, symbols);
        GenEntropyText.Text = $"约 {bits:F0} bit 熵";
    }

    private void OnCopyGenerated(object sender, RoutedEventArgs e) => CopySecret(GenOutput.Text, "生成的密码");

    private void OnUseGenerated(object sender, RoutedEventArgs e)
    {
        if (GenOutput.Text.Length == 0)
        {
            return;
        }

        // 密码只属于登录条目：note 模式下填入自动切回登录。
        KindCombo.SelectedIndex = 0;
        HideReveal();
        PasswordBox.Password = GenOutput.Text;
        GenOutput.Text = "";
        _host.Toast.Show("已填入密码（尚未保存）");
    }

    // ---------------- TOTP ----------------

    private void StartTotpTimer()
    {
        if (_totpTimer != null)
        {
            return;
        }

        _totpTimer = Root.DispatcherQueue.CreateTimer();
        _totpTimer.Interval = TimeSpan.FromSeconds(1);
        _totpTimer.Tick += (_, _) => UpdateTotpDisplay();
        _totpTimer.Start();
    }

    private void StopTotpTimer() => _totpTimer?.Stop();

    private void UpdateTotpDisplay()
    {
        if (_selectedId == null || !_vault.IsUnlocked)
        {
            return;
        }

        var entry = _vault.Data?.Entries.FirstOrDefault(en => en.Id == _selectedId);
        if (entry == null || entry.TotpSecret.Length == 0)
        {
            TotpCode.Text = "------";
            return;
        }

        try
        {
            TotpCode.Text = Totp.Compute(entry.TotpSecret) + "  (" + Totp.RemainingSeconds() + "s)";
        }
        catch
        {
            TotpCode.Text = "密钥无效";
        }
    }

    private void OnCopyTotp(object sender, RoutedEventArgs e)
    {
        if (_selectedId == null)
        {
            return;
        }

        var entry = _vault.Data?.Entries.FirstOrDefault(en => en.Id == _selectedId);
        if (entry?.TotpSecret.Length > 0)
        {
            try
            {
                CopySecret(Totp.Compute(entry.TotpSecret), "验证码");
            }
            catch
            {
                _host.Toast.Show("TOTP 密钥无效");
            }
        }
    }
}
