using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace Jietu;

/// <summary>PasswordWindow 的「解锁 / 创建 / Hello 快速解锁」部分（partial）。</summary>
public sealed partial class PasswordWindow
{
    private void RefreshState()
    {
        if (!_vault.IsUnlocked)
        {
            ShowUnlockPanel();
            ApplyWindowSize();
            return;
        }

        UnlockPanel.Visibility = Visibility.Collapsed;
        VaultPanel.Visibility = Visibility.Visible;
        LockPill.Visibility = Visibility.Collapsed;
        ApplyWindowSize();
        RefreshList(string.IsNullOrEmpty(SearchBox.Text) ? null : SearchBox.Text);
        UpdateFilterChips(animate: false);
    }

    private void ShowUnlockPanel()
    {
        VaultPanel.Visibility = Visibility.Collapsed;
        UnlockPanel.Visibility = Visibility.Visible;
        LockPill.Visibility = Visibility.Visible;
        CountBadge.Visibility = Visibility.Collapsed;
        StopTotpTimer();
        // 锁定/关闭后编辑器里的明文与列表行都不能残留。
        ClearEditor();
        EntryList.ItemsSource = null;

        var exists = _vault.Exists;
        var hasHello = exists && _vault.HasHelloBlob;
        UnlockTitle.Text = exists ? "输入主密码解锁" : "创建密码库";
        // 页头只留一句定位句：卡片里不再重复一遍「主密码无法找回」，避免两处说同一件事。
        UnlockModeBadge.Text = exists
            ? "本机加密存储 · 主密码丢失后无法找回"
            : "首次设置 · 主密码只在本机校验，不上传任何服务器";
        ConfirmFieldShell.Visibility = exists ? Visibility.Collapsed : Visibility.Visible;
        StrengthPanel.Visibility = exists ? Visibility.Collapsed : Visibility.Visible;
        MatchHint.Visibility = Visibility.Collapsed;
        CreateWarning.Visibility = exists ? Visibility.Collapsed : Visibility.Visible;
        HelloBlock.Visibility = hasHello ? Visibility.Visible : Visibility.Collapsed;
        // 有 Hello 时说明由磁贴副标题承担；没启用时才需要这行指路。
        HelloHint.Text = "可在 设置 → 密码库 中启用 Windows Hello，用指纹 / PIN 免输主密码。";
        HelloHint.Visibility = exists && !hasHello ? Visibility.Visible : Visibility.Collapsed;
        SetHelloBusy(false);
        SetUnlockBusy(false);
        HideUnlockMessage();
        CapsHint.Visibility = Visibility.Collapsed;
        MasterBox.Password = "";
        ConfirmBox.Password = "";

        // 旧库/过新/损坏的库在这里换成阻断卡：输入密码没有意义，但路径与恢复入口必须给到。
        RefreshBlockCard();
        if (BlockCard.Visibility == Visibility.Visible)
        {
            BlockOpenFolderButton.Focus(FocusState.Programmatic);
        }
        else
        {
            MasterBox.Focus(FocusState.Programmatic);
        }
    }

    private void OnMasterPasswordChanged(object sender, RoutedEventArgs e)
    {
        UpdateCreateStrength();
        UpdateCapsHint();
    }

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
        if (ConfirmFieldShell.Visibility != Visibility.Visible)
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

    private void OnMasterKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            OnUnlockClick(sender, e);
        }
    }

    private async void OnUnlockClick(object sender, RoutedEventArgs e)
    {
        // Argon2id 要跑几百毫秒到两秒：期间连按 Enter 会起多份派生，必须挡住。
        if (_unlocking || _helloBusy)
        {
            return;
        }

        var password = MasterBox.Password;
        if (password.Length == 0)
        {
            MasterBox.Focus(FocusState.Programmatic);
            return;
        }

        HideUnlockMessage();
        if (_vault.Exists)
        {
            await UnlockWithPasswordAsync(password);
        }
        else
        {
            await CreateVaultAsync(password);
        }
    }

    private async Task UnlockWithPasswordAsync(string password)
    {
        SetUnlockBusy(true);
        try
        {
            var outcome = await _vault.TryUnlockAsync(password);
            if (outcome.Ok)
            {
                RefreshState();
                return;
            }

            // 三层提示（标题/正文/辅助）+ 恢复入口；认证失败不进阻断态，输入区保留，可改密码重试。
            ShowUnlockFailure(outcome);
            if (!outcome.IsBlocked)
            {
                MasterBox.SelectAll();
                MasterBox.Focus(FocusState.Programmatic);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            ShowUnlockError("解锁失败：" + ex.Message);
        }
        finally
        {
            SetUnlockBusy(false);
        }
    }

    private async Task CreateVaultAsync(string password)
    {
        if (password != ConfirmBox.Password)
        {
            ShowUnlockError("两次输入的主密码不一致。");
            ConfirmBox.Focus(FocusState.Programmatic);
            return;
        }

        if (password.Length < 12)
        {
            ShowUnlockError("主密码太短，请至少使用 12 个字符。");
            MasterBox.Focus(FocusState.Programmatic);
            return;
        }

        SetUnlockBusy(true);
        try
        {
            await _vault.CreateAsync(password);
            RefreshState();
            _host.Toast.Show("密码库已创建");
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            ShowUnlockError(ex.Message);
        }
        finally
        {
            SetUnlockBusy(false);
        }
    }

    private void SetUnlockBusy(bool busy)
    {
        _unlocking = busy;
        var exists = _vault.Exists;
        UnlockButton.IsEnabled = !busy;
        UnlockButton.Content = busy
            ? (exists ? "正在解锁…" : "正在创建…")
            : (exists ? "解锁" : "创建密码库");
    }

    private void SetHelloBusy(bool busy)
    {
        _helloBusy = busy;
        HelloButtonTitle.Text = busy ? "等待 Windows Hello 验证…" : "使用 Windows Hello 解锁";
        HelloButtonSubtitle.Text = busy ? "请在弹出的窗口中完成指纹或 PIN" : "指纹 / PIN 快速解锁，免输主密码";
        HelloBusyRing.IsActive = busy;
        HelloBusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        HelloChevron.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowUnlockError(string? message)
    {
        // 单行错误（创建校验、Hello 失败等）：不需要三层结构，标题与辅助行收起。
        UnlockErrorTitle.Visibility = Visibility.Collapsed;
        UnlockErrorHint.Visibility = Visibility.Collapsed;
        UnlockRecoverButton.Visibility = Visibility.Collapsed;
        UnlockErrorText.Text = string.IsNullOrWhiteSpace(message) ? "解锁失败，请重试。" : message;
        UnlockError.Visibility = Visibility.Visible;
        UnlockNotice.Visibility = Visibility.Collapsed;
    }

    private void ShowUnlockNotice(string message)
    {
        UnlockNotice.Text = message;
        UnlockNotice.Visibility = Visibility.Visible;
        UnlockError.Visibility = Visibility.Collapsed;
    }

    private void HideUnlockMessage()
    {
        UnlockError.Visibility = Visibility.Collapsed;
        UnlockErrorTitle.Visibility = Visibility.Collapsed;
        UnlockErrorHint.Visibility = Visibility.Collapsed;
        UnlockRecoverButton.Visibility = Visibility.Collapsed;
        UnlockNotice.Visibility = Visibility.Collapsed;
    }

    private void OnFieldFocus(object sender, RoutedEventArgs e)
    {
        if (!_xamlReady)
        {
            return;
        }

        if (ReferenceEquals(sender, MasterBox))
        {
            SetFieldFocused(MasterFieldShell, MasterBox.FocusState != FocusState.Unfocused);
        }
        else if (ReferenceEquals(sender, ConfirmBox))
        {
            SetFieldFocused(ConfirmFieldShell, ConfirmBox.FocusState != FocusState.Unfocused);
        }

        UpdateCapsHint();
    }

    private void SetFieldFocused(Border shell, bool focused)
    {
        shell.BorderBrush = focused
            ? (Brush)Application.Current.Resources["SnapFlowAccentBrush"]
            : _fieldIdleStroke;
    }

    private void UpdateCapsHint()
    {
        if (!_xamlReady)
        {
            return;
        }

        var caps = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.CapitalLock)
            .HasFlag(CoreVirtualKeyStates.Locked);
        CapsHint.Visibility = caps && UnlockPanel.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void OnHelloClick(object sender, RoutedEventArgs e)
    {
        if (_helloBusy || _unlocking)
        {
            return;
        }

        var blob = _vault.ReadHelloBlob();
        if (blob == null)
        {
            ShowUnlockError("没有可用的快速解锁副本，请用主密码解锁。");
            return;
        }

        // 上一次失败的提示必须先撤掉：否则 Hello 弹窗一出现，屏幕上还挂着上一条红字。
        HideUnlockMessage();
        SetHelloBusy(true);
        try
        {
            var result = await HelloUnlock.UnwrapKeyAsync(blob);
            if (!result.Ok)
            {
                ReportHelloFailure(result);
                return;
            }

            var outcome = _vault.TryUnlockWithKey(result.Key!);
            if (!outcome.Ok)
            {
                // 失败文案统一由会话给出（认证失败给三层提示，阻断给卡片）。
                ShowUnlockFailure(outcome);
                return;
            }

            RefreshState();
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            ShowUnlockError("快速解锁失败：" + ex.Message);
        }
        finally
        {
            SetHelloBusy(false);
        }
    }

    private void ReportHelloFailure(HelloResult result)
    {
        switch (result.Outcome)
        {
            case HelloOutcome.Canceled:
                ShowUnlockNotice("已取消 Windows Hello 验证，可以重试或直接用主密码解锁。");
                break;
            case HelloOutcome.PreferPassword:
                ShowUnlockNotice("已切换为主密码验证，请输入主密码。");
                MasterBox.Focus(FocusState.Programmatic);
                break;
            case HelloOutcome.NoCredential:
                ShowUnlockError("本机的 Windows Hello 凭据已失效。请先用主密码解锁，再到 设置 → 密码库 重新启用快速解锁。");
                break;
            default:
                ShowUnlockError("Windows Hello 未能完成验证，可以再试一次，或用主密码解锁。");
                Log.Info("Hello 快速解锁失败：" + result.Detail);
                break;
        }
    }

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
}
