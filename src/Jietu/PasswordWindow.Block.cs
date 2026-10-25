using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Jietu;

/// <summary>
/// 解锁视图的"阻断与失败"逻辑（partial）。
///
/// 三种不可解锁的情形必须分开呈现，因为它们要求的动作完全不同：
/// ① 旧版 v1（LegacyV1）：数据在，只是格式变了 ⇒ 指路 + 给路径 + 打开文件夹，程序零写盘；
/// ② 版本过新（NewerVersion）：换新版程序就能开 ⇒ 只说"升级"；
/// ③ 结构损坏/无法识别（IntegrityFailure / UnknownFile）：可以留一份备份，再引导从加密备份恢复。
///
/// 而"认证失败"（tag 不匹配）**不属于上面任何一种**：它可能只是一次手滑输错密码，
/// 所以保留输入区、保留"再试一次"的权利，只在下方给三层提示与恢复入口。
/// </summary>
public sealed partial class PasswordWindow
{
    /// <summary>按会话状态刷新阻断卡（ShowUnlockPanel 末尾调用）。</summary>
    private void RefreshBlockCard()
    {
        if (_vault.State == VaultState.Blocked && _vault.Block.Kind != VaultBlockKind.None)
        {
            ShowBlockCard(_vault.Block);
        }
        else
        {
            HideBlockCard();
        }
    }

    /// <summary>显示阻断卡并收起密码输入区（阻断态下输密码没有意义，但恢复入口必须留着）。</summary>
    private void ShowBlockCard(VaultBlockInfo block)
    {
        BlockCard.Visibility = Visibility.Visible;
        BlockTitle.Text = block.Kind switch
        {
            VaultBlockKind.LegacyV1 => VaultMessages.BlockLegacyTitle,
            VaultBlockKind.NewerVersion => VaultMessages.BlockNewerTitle,
            VaultBlockKind.UnknownFile => VaultMessages.BlockUnknownTitle,
            _ => VaultMessages.BlockIntegrityTitle,
        };
        BlockBody.Text = block.Message;
        // 完整路径必须可选中：用户要做的第一件事就是把它复制出去做备份。
        BlockPath.Text = block.Path;
        BlockPath.Visibility = Visibility.Visible;

        MasterFieldShell.Visibility = Visibility.Collapsed;
        ConfirmFieldShell.Visibility = Visibility.Collapsed;
        StrengthPanel.Visibility = Visibility.Collapsed;
        MatchHint.Visibility = Visibility.Collapsed;
        HelloBlock.Visibility = Visibility.Collapsed;
        CapsHint.Visibility = Visibility.Collapsed;
        UnlockButton.Visibility = Visibility.Collapsed;
        CreateWarning.Visibility = Visibility.Collapsed;
        HelloHint.Visibility = Visibility.Collapsed;
        HideUnlockMessage();
    }

    private void HideBlockCard()
    {
        BlockCard.Visibility = Visibility.Collapsed;
        BlockPath.Visibility = Visibility.Collapsed;
        UnlockButton.Visibility = Visibility.Visible;
    }

    /// <summary>「打开所在文件夹」：只定位，不做任何文件操作。</summary>
    private void OnOpenVaultFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = _vault.VaultPath;
            if (!File.Exists(path))
            {
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            _host.Toast.Show("打开文件夹失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 「重新检测」：重新读盘分类。用户移走/替换文件后不必重启程序。
    /// 我们**不**替用户删除或改名任何文件。
    /// </summary>
    private void OnRetryAfterMove(object sender, RoutedEventArgs e)
    {
        RefreshState();
        if (_vault.State != VaultState.Blocked)
        {
            _host.Toast.Show("已重新检测：现在可以创建密码库了");
        }
        else
        {
            _host.Toast.Show("文件仍然无法打开，请查看上方说明");
        }
    }

    /// <summary>
    /// 三层解锁失败提示：标题 / 正文 / 辅助 + 恢复入口。
    /// 认证失败走这里（不进阻断态）；阻断态走 <see cref="ShowBlockCard"/>。
    /// </summary>
    private void ShowUnlockFailure(VaultUnlockOutcome outcome)
    {
        if (outcome.IsBlocked)
        {
            // 会话已经把 state/block 设好，统一交给阻断卡呈现。
            RefreshBlockCard();
            return;
        }

        UnlockError.Visibility = Visibility.Visible;
        UnlockNotice.Visibility = Visibility.Collapsed;
        UnlockErrorTitle.Visibility = Visibility.Visible;
        UnlockErrorTitle.Text = outcome.Title ?? VaultMessages.UnlockFailedTitle;
        UnlockErrorText.Text = outcome.Body ?? VaultMessages.UnlockFailedBody;
        UnlockErrorHint.Text = outcome.Hint ?? "";
        UnlockErrorHint.Visibility = string.IsNullOrEmpty(outcome.Hint) ? Visibility.Collapsed : Visibility.Visible;
        // 只有在"可能真的坏了"的情况下才给恢复入口，避免每次手滑都劝人去恢复备份。
        UnlockRecoverButton.Visibility = outcome.Status == VaultOpenStatus.AuthenticationFailed
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// 「用备份文件恢复」：**只验证，不写盘**。
    ///
    /// 为什么锁定时不做真正的恢复：库文件此时可能是旧版/损坏的，任何"写回"都等于覆盖用户
    /// 唯一的原始数据，而本版本明确禁止程序内覆盖旧库。所以这里只帮用户确认"这份备份是好的、
    /// 里面有 N 条"，再告诉他下一步怎么做。
    /// </summary>
    private async void OnRecoverFromBackup(object sender, RoutedEventArgs e)
    {
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

        var box = new Microsoft.UI.Xaml.Controls.PasswordBox
        {
            Header = "备份密码",
            PlaceholderText = "导出备份时设置的备份密码",
        };
        var hint = new TextBlock
        {
            Text = "这里只校验备份能否打开，不会改动你的库文件。校验通过后再按提示重建密码库并导入。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11.5,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(hint);
        panel.Children.Add(box);
        if (await ShowVaultDialog("校验加密备份", panel, "校验",
                () => box.Password.Length == 0 ? "请输入备份密码。" : null) != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            var buffer = await Windows.Storage.FileIO.ReadBufferAsync(file);
            Windows.Security.Cryptography.CryptographicBuffer.CopyToByteArray(buffer, out var bytes);
            var opened = await Task.Run(() => VaultImportExport.OpenEncryptedBackup(bytes, box.Password));
            switch (opened.Status)
            {
                case VaultOpenStatus.Ok:
                    _host.Toast.Show(
                        $"备份校验通过：{opened.Data!.Entries.Count} 条有效条目。请先移走旧库文件，再创建新库并导入这份备份。");
                    break;
                case VaultOpenStatus.AuthenticationFailed:
                    ShowUnlockError(VaultMessages.BackupWrongPassword);
                    break;
                case VaultOpenStatus.LegacyFormat:
                    _host.Toast.Show(VaultMessages.BackupLegacyRefused);
                    break;
                case VaultOpenStatus.NewerFormat:
                case VaultOpenStatus.PayloadTooNew:
                    _host.Toast.Show(VaultMessages.BackupNewerRefused);
                    break;
                default:
                    _host.Toast.Show(VaultMessages.BackupWrongPassword);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            _host.Toast.Show("校验备份失败：" + ex.Message);
        }
    }
}
