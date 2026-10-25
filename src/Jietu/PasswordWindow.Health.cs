using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Jietu;

/// <summary>
/// 体检与回收站的界面（partial）。
///
/// 这两个面板都做成对话框而不是常驻列，理由是它们属于"偶尔打开一次"的维护性视图，
/// 常驻会挤压列表/详情两列的宽度，而密码库日常 95% 的时间是在列表和编辑器里。
/// </summary>
public sealed partial class PasswordWindow
{
    // ---------------- 密码体检 ----------------

    private async void OnOpenHealth(object sender, RoutedEventArgs e)
    {
        if (!_vault.IsUnlocked)
        {
            return;
        }

        // 5000 条约几十毫秒，放后台避免一次可见的停顿。
        var report = await Task.Run(() => _vault.ComputeHealth());
        await ShowHealthDialog(report);
    }

    private async Task ShowHealthDialog(VaultHealthReport report)
    {
        var body = new StackPanel { Spacing = 12, Width = 460 };

        body.Children.Add(new TextBlock
        {
            Text = $"共 {report.TotalEntriesToFix} 个条目需要处理",
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        // 五个计数各自独立、互不包含：同一条目可能既弱又重复，所以下面不出现任何合计。
        body.Children.Add(new TextBlock
        {
            Text = "各项独立统计，同一条目可能出现在多项里；上面的总数按条目去重。",
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });

        var counters = new StackPanel { Spacing = 6 };
        AddHealthRow(counters, "弱密码", report.WeakCount, "熵低于 50 bit 或长度不足 10 位");
        AddHealthRow(counters, "密码含名称", report.FragmentCount, "密码里出现了标题或用户名的片段");
        AddHealthRow(counters, "重复使用", report.ReusedGroupCount, "多条账号共用同一密码（按组计）");
        AddHealthRow(counters, "长期未更新", report.StaleCount, "超过 365 天没有改过密码");
        AddHealthRow(
            counters,
            "未开两步验证",
            report.MissingTwoFactorCount,
            report.MissingTwoFactorByUser > 0
                ? $"关键账号中，{report.MissingTwoFactorByUser} 个由你手动标记"
                : "关键账号建议启用动态验证码");
        body.Children.Add(counters);

        if (report.Issues.Count > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = "问题条目（最多显示 20 条）",
                FontSize = 12.5,
                Margin = new Thickness(0, 6, 0, 0),
            });

            var list = new StackPanel { Spacing = 4 };
            foreach (var issue in report.Issues.Take(20))
            {
                list.Children.Add(new TextBlock
                {
                    Text = $"· {DisplayTitle(issue.Title)} — {DescribeIssue(issue)}",
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            body.Children.Add(new ScrollViewer
            {
                Content = list,
                MaxHeight = 220,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            });
        }

        var dialog = new ContentDialog
        {
            Title = report.IsClean ? "密码体检：没有问题" : "密码体检",
            Content = new ScrollViewer { Content = body, MaxHeight = 480 },
            CloseButtonText = "关闭",
            XamlRoot = Content.XamlRoot,
        };

        await dialog.ShowAsync();
    }

    private static void AddHealthRow(StackPanel host, string title, int count, string hint)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock
        {
            Text = title,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 0);

        var detail = new TextBlock
        {
            // 数量为 0 时不写"0 个"，而是给一句"没有发现问题"——避免满屏 0 让人以为坏了。
            Text = count > 0 ? $"{count} 项 · {hint}" : "无",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources[
                count > 0 ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"],
        };
        Grid.SetColumn(detail, 1);

        row.Children.Add(label);
        row.Children.Add(detail);
        host.Children.Add(row);
    }

    private static string DescribeIssue(VaultHealthIssue issue) => issue.Dimension switch
    {
        VaultHealthDimension.Weak => issue.EntropyBits is { } bits ? $"弱密码（约 {bits:F0} bit）" : "弱密码",
        VaultHealthDimension.Fragment => $"密码含「{issue.Fragment}」",
        VaultHealthDimension.Reused => issue.PeerTitles is { Count: > 0 } peers
            ? $"与其他 {peers.Count} 个账号共用密码"
            : "重复使用密码",
        VaultHealthDimension.Stale => "长期未更新",
        VaultHealthDimension.MissingTwoFactor => issue.CriticalByUser
            ? "关键账号（你标记的）未开两步验证"
            : "关键账号未开两步验证",
        _ => "需要处理",
    };

    // ---------------- 回收站 ----------------

    private async void OnOpenRecycleBin(object sender, RoutedEventArgs e)
    {
        if (!_vault.IsUnlocked)
        {
            return;
        }

        await ShowRecycleBinDialog();
    }

    private async Task ShowRecycleBinDialog()
    {
        var body = new StackPanel { Spacing = 10, Width = 460 };

        body.Children.Add(new TextBlock
        {
            Text = $"回收站里的条目超过 {VaultLimits.TrashRetainDays} 天或超过 {VaultLimits.TrashMaxEntries} 条时会自动清理。",
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });

        var list = new StackPanel { Spacing = 6 };
        body.Children.Add(new ScrollViewer
        {
            Content = list,
            MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        var dialog = new ContentDialog
        {
            Title = "回收站",
            Content = body,
            PrimaryButtonText = "清空回收站",
            CloseButtonText = "关闭",
            XamlRoot = Content.XamlRoot,
        };

        void Rebuild()
        {
            list.Children.Clear();
            var entries = _vault.RecycleBin.ToList();
            if (entries.Count == 0)
            {
                list.Children.Add(new TextBlock { Text = "回收站是空的。", FontSize = 12.5 });
                dialog.IsPrimaryButtonEnabled = false;
                return;
            }

            dialog.IsPrimaryButtonEnabled = true;
            foreach (var entry in entries.OrderByDescending(item => item.DeletedUtc))
            {
                list.Children.Add(BuildRecycleRow(entry, Rebuild));
            }
        }

        Rebuild();

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        var cleared = _vault.ClearRecycleBin();
        if (cleared > 0)
        {
            _host.Toast.Show($"已清空回收站（{cleared} 条）");
        }
    }

    private UIElement BuildRecycleRow(VaultEntry entry, Action rebuild)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = DisplayTitle(entry.Title),
            FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = entry.DeletedUtc is { } deleted
                ? $"删除于 {deleted.ToLocalTime():yyyy-MM-dd HH:mm}"
                : "删除时间未知",
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        Grid.SetColumn(text, 0);

        var restore = new Button { Content = "恢复", FontSize = 12, Height = 28 };
        Grid.SetColumn(restore, 1);
        restore.Click += (_, _) =>
        {
            if (_vault.RestoreFromRecycleBin(entry.Id))
            {
                _host.Toast.Show("已恢复");
                RefreshList(string.IsNullOrEmpty(SearchBox.Text) ? null : SearchBox.Text);
                rebuild();
            }
        };

        var purge = new Button { Content = "彻底删除", FontSize = 12, Height = 28 };
        Grid.SetColumn(purge, 2);
        purge.Click += (_, _) =>
        {
            var confirm = NativeMethods.MessageBox(
                WinRT.Interop.WindowNative.GetWindowHandle(this),
                $"彻底删除「{DisplayTitle(entry.Title)}」？这个操作不可恢复。",
                "SnapFlow - 彻底删除",
                NativeMethods.MbYesNo | NativeMethods.MbIconWarning | NativeMethods.MbDefButton2);
            if (confirm != NativeMethods.IdYes)
            {
                return;
            }

            if (_vault.PurgeRecycleBinEntry(entry.Id))
            {
                _host.Toast.Show("已彻底删除");
                rebuild();
            }
        };

        row.Children.Add(text);
        row.Children.Add(restore);
        row.Children.Add(purge);
        return row;
    }
}
