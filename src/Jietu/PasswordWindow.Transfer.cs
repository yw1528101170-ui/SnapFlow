using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Jietu;

/// <summary>PasswordWindow 的「导入 / 导出 / 加密备份 / 修改主密码」部分（partial）。</summary>
public sealed partial class PasswordWindow
{
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
            var rows = VaultImportExport.ParseCsv(text);

            // 上限校验必须在写入之前，且是**整体拒绝**：任何一条不合格就一条都不写。
            // 用对话框而不是 Toast——这段文案要用户读明白"一条都没导进去"。
            var limitError = VaultImportExport.ValidateImportRows(rows);
            if (limitError != null)
            {
                await ShowAppConfirmAsync("导入未执行", limitError, null);
                return;
            }

            var entries = VaultImportExport.ToEntries(rows);
            var added = VaultImportExport.MergeInto(_vault.Data!, entries);
            if (added > 0)
            {
                // 导入是大批量写入：用 Flush 立刻落盘，别留给防抖窗口（用户此时最怕"导完就崩"）。
                _vault.RequestSave();
                await _vault.FlushAsync();
                RefreshList();
            }

            _host.Toast.Show($"导入完成：新增 {added} 条，跳过重复 {entries.Count - added} 条");
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            _host.Toast.Show("导入失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 选一个 .spvault 备份文件并让用户输入备份口令。「校验备份」与「从备份恢复」
    /// 两个入口共用这一段；返回 null 表示用户取消或读取失败（已提示）。
    /// </summary>
    private async Task<(byte[] Bytes, string Password)?> PickBackupFileWithPasswordAsync(
        string title, string hint, string actionText)
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
            return null;
        }

        if (file == null)
        {
            return null;
        }

        var box = new Microsoft.UI.Xaml.Controls.PasswordBox
        {
            Header = "备份口令",
            PlaceholderText = "导出备份时设置的备份口令",
        };
        var hintBlock = new TextBlock
        {
            Text = hint,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11.5,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(hintBlock);
        panel.Children.Add(box);
        if (await ShowVaultDialog(title, panel, actionText,
                () => box.Password.Length == 0 ? "请输入备份口令。" : null) != ContentDialogResult.Primary)
        {
            return null;
        }

        try
        {
            var buffer = await Windows.Storage.FileIO.ReadBufferAsync(file);
            Windows.Security.Cryptography.CryptographicBuffer.CopyToByteArray(buffer, out var bytes);
            return (bytes, box.Password);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            _host.Toast.Show("读取备份失败：" + ex.Message);
            return null;
        }
    }

    private async void OnRestoreBackup(object sender, RoutedEventArgs e)
    {
        if (!_vault.IsUnlocked)
        {
            return;
        }

        var picked = await PickBackupFileWithPasswordAsync(
            "从加密备份恢复",
            "备份文件用导出时设置的备份口令加密（与主密码无关）。恢复按「标题+用户名」去重合并，不会覆盖现有条目。",
            "恢复");
        if (picked is not { } backup)
        {
            return;
        }

        try
        {
            var opened = await Task.Run(() => VaultImportExport.OpenEncryptedBackup(backup.Bytes, backup.Password));
            switch (opened.Status)
            {
                case VaultOpenStatus.Ok:
                    break;
                case VaultOpenStatus.LegacyFormat:
                    _host.Toast.Show(VaultMessages.BackupLegacyRefused);
                    return;
                case VaultOpenStatus.NewerFormat:
                case VaultOpenStatus.PayloadTooNew:
                    _host.Toast.Show(VaultMessages.BackupNewerRefused);
                    return;
                default:
                    _host.Toast.Show(VaultMessages.BackupWrongPassword);
                    return;
            }

            var backupData = opened.Data!;
            var added = VaultImportExport.MergeInto(_vault.Data!, backupData.Entries);
            if (added > 0)
            {
                // 同导入：恢复完立刻落盘，不让数据停在防抖窗口里。
                _vault.RequestSave();
                await _vault.FlushAsync();
                RefreshList();
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
            FontSize = 11.5,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        var box = new Microsoft.UI.Xaml.Controls.PasswordBox
        {
            Header = "备份口令",
            PlaceholderText = "至少 8 个字符",
        };
        var confirm = new Microsoft.UI.Xaml.Controls.PasswordBox
        {
            Header = "确认备份口令",
            PlaceholderText = "再输入一次",
        };
        panel.Children.Add(hint);
        panel.Children.Add(box);
        panel.Children.Add(confirm);

        var dialog = await ShowVaultDialog("加密备份", panel, "导出备份", () =>
        {
            if (box.Password.Length < 8)
            {
                return "备份口令至少需要 8 个字符。";
            }

            return box.Password != confirm.Password ? "两次输入的备份口令不一致。" : null;
        });
        if (dialog != ContentDialogResult.Primary)
        {
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

        if (!await ShowAppConfirmAsync(
                "明文导出",
                "即将导出包含全部密码明文的 CSV 文件。\n任何拿到这个文件的人都能直接读出所有密码，请确认存放位置安全。",
                "仍要导出",
                danger: true))
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
            FontSize = 11.5,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        var newBox = new Microsoft.UI.Xaml.Controls.PasswordBox
        {
            Header = "新主密码",
            PlaceholderText = "至少 12 个字符",
        };
        var confirmBox = new Microsoft.UI.Xaml.Controls.PasswordBox
        {
            Header = "确认新主密码",
            PlaceholderText = "再输入一次",
        };
        panel.Children.Add(hint);
        panel.Children.Add(newBox);
        panel.Children.Add(confirmBox);

        var dialog = await ShowVaultDialog("修改主密码", panel, "确认修改", () =>
        {
            if (newBox.Password.Length < 12)
            {
                return "新主密码至少需要 12 个字符。";
            }

            return newBox.Password != confirmBox.Password ? "两次输入的新主密码不一致。" : null;
        });
        if (dialog != ContentDialogResult.Primary)
        {
            return;
        }

        // 改密要跑 Argon2 + 整库重加密，1~2 秒；用带进度条的模态遮挡，避免用户以为卡死。
        // 「取消」只在文件替换之前有效——之后取消已经来不及（此时新文件已原子落盘）。
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0 };
        var progressText = new TextBlock
        {
            Text = "正在按新主密码重新加密…",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        var busyBody = new StackPanel { Spacing = 12, Width = 320 };
        busyBody.Children.Add(progressText);
        busyBody.Children.Add(bar);

        var busyDialog = new ContentDialog
        {
            Title = "修改主密码",
            Content = busyBody,
            CloseButtonText = "取消",
            XamlRoot = Content.XamlRoot,
        };

        using var cancellation = new CancellationTokenSource();
        busyDialog.CloseButtonClick += (_, _) => cancellation.Cancel();

        var showTask = busyDialog.ShowAsync().AsTask();
        try
        {
            await _vault.ChangeMasterPasswordAsync(
                newBox.Password,
                new Progress<int>(percent => bar.Value = percent),
                cancellation.Token);
            busyDialog.Hide();
            _host.Toast.Show("主密码已修改");
        }
        catch (OperationCanceledException)
        {
            busyDialog.Hide();
            _host.Toast.Show("已取消，密码库保持原样");
        }
        catch (Exception ex)
        {
            busyDialog.Hide();
            Log.Error(ex);
            _host.Toast.Show("修改失败：" + ex.Message);
        }
        finally
        {
            // ShowAsync 的 Task 在 Hide 之后完成；不 await 也可以，但要观察异常避免未处理。
            _ = showTask.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        }
    }
}
