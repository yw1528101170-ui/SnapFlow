using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    // 「显示密码」态使用的明文缓存：与 PasswordBox/TextBox 双控件互切配合。
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

        var exists = _vault.Exists;
        UnlockTitle.Text = exists ? "输入主密码解锁" : "创建密码库";
        UnlockSubtitle.Text = exists
            ? "密码库在本机加密存储，主密码无法找回。"
            : "设置主密码。主密码是唯一的解锁凭据，忘记后数据无法恢复，请务必牢记。";
        ConfirmBox.Visibility = exists ? Visibility.Collapsed : Visibility.Visible;
        StrengthPanel.Visibility = exists ? Visibility.Collapsed : Visibility.Visible;
        UnlockButton.Content = exists ? "解锁" : "创建密码库";
        UnlockError.IsOpen = false;
        MasterBox.Password = "";
        ConfirmBox.Password = "";
        UnlockError.Message = "";
        HelloButton.Visibility = exists && _vault.HasHelloBlob ? Visibility.Visible : Visibility.Collapsed;
        MasterBox.Focus(FocusState.Programmatic);
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

    private void LoadEditor(VaultEntry entry)
    {
        DetailPane.Visibility = Visibility.Visible;
        EmptyPane.Visibility = Visibility.Collapsed;
        DetailHeader.Text = "条目详情";
        _editingExisting = true;
        KindBox.Text = entry.Kind == "note" ? "安全笔记" : "登录";
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
        var entry = new VaultEntry
        {
            Id = _editingExisting && _selectedId != null ? _selectedId : Guid.NewGuid().ToString("N"),
            Kind = KindBox.Text.Contains("笔记") ? "note" : "login",
            Title = TitleBox.Text.Trim(),
            Username = UsernameBox.Text.Trim(),
            // 「显示密码」态下明文在 TextBox 里，PasswordBox 是旧的，取可见的那个。
            Password = PasswordBox.Visibility == Visibility.Visible ? PasswordBox.Password : PasswordTextBox.Text,
            Url = UrlBox.Text.Trim(),
            TotpSecret = secret,
            Tags = tags,
            Notes = NotesBox.Text,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        return entry;
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
        KindBox.Text = "登录";
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
        RefreshList(null);
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
            (sender as Button)!.Content = "隐藏";
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
    }

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

    private void OnCopyPassword(object sender, RoutedEventArgs e) => CopySecret(PasswordBox.Password, "密码");

    private void OnCopyUsername(object sender, RoutedEventArgs e) => CopySecret(UsernameBox.Text, "用户名");

    // ---------------- 生成器 ----------------

    private void OnToggleGenerator(object sender, RoutedEventArgs e)
    {
        GeneratorPanel.Visibility = GeneratorPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (GeneratorPanel.Visibility == Visibility.Visible)
        {
            OnGeneratePassword(sender, e);
        }
    }

    private void OnGeneratePassword(object sender, RoutedEventArgs e)
    {
        try
        {
            GenOutput.Text = GenPassphrase.IsChecked == true
                ? PasswordGenerator.GeneratePassphrase(4)
                : PasswordGenerator.Generate(Math.Clamp(int.Parse(GenLengthBox.Text), PasswordGenerator.MinLength, PasswordGenerator.MaxLength),
                    GenLower.IsChecked == true, GenUpper.IsChecked == true, GenDigits.IsChecked == true, GenSymbols.IsChecked == true);
        }
        catch (Exception ex)
        {
            GenOutput.Text = "";
            _host.Toast.Show("生成失败：" + ex.Message);
        }
    }

    private void OnUseGenerated(object sender, RoutedEventArgs e)
    {
        if (GenOutput.Text.Length == 0)
        {
            return;
        }

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
