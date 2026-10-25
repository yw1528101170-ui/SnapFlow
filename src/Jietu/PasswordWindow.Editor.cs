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

/// <summary>PasswordWindow 的「条目编辑器 / 复制 / 快捷键」部分（partial）。</summary>
public sealed partial class PasswordWindow
{
    private void OnEntrySelected(object sender, SelectionChangedEventArgs e)
    {
        if (EntryList.SelectedItem is not VaultRow row)
        {
            return;
        }

        _vault.TouchActivity();
        // 列表刷新（搜索/排序）会重设选中项：同一条目不再重载，
        // 否则未保存的修改会被库里旧值覆盖、详情态的显示态也会被打断。
        if (row.Id == _selectedId)
        {
            return;
        }

        _selectedId = row.Id;
        var entry = _vault.Data?.Entries.FirstOrDefault(en => en.Id == row.Id);
        if (entry == null)
        {
            return;
        }

        ShowDetail(entry);
    }

    /// <summary>
    /// 详情态（只读名片）：点列表条目 / 保存后进入。编辑器字段同步填充，
    /// 这样字段行上的复制按钮在两种模式下都能取到当前值。
    /// </summary>
    private void ShowDetail(VaultEntry entry)
    {
        FillEditorFields(entry);
        _editorVisible = false;
        HideDetailReveal();
        FillDetailView(entry);
        MarkEditorClean();
        SyncLayout();
        DetailPane.ChangeView(null, 0, null, true);
    }

    /// <summary>详情态 → 编辑态：字段已在 ShowDetail 时填充，这里从库里再取一次保证干净。</summary>
    private void OnEditEntry(object sender, RoutedEventArgs e)
    {
        if (_selectedId == null)
        {
            return;
        }

        var entry = _vault.Data?.Entries.FirstOrDefault(en => en.Id == _selectedId);
        if (entry == null)
        {
            return;
        }

        FillEditorFields(entry);
        _editorVisible = true;
        HideReveal();
        MarkEditorClean();
        SyncLayout();
        DetailPane.ChangeView(null, 0, null, true);
        TitleBox.Focus(FocusState.Programmatic);
    }

    /// <summary>编辑态 → 详情态（已有条目）或直接收起（草稿）。</summary>
    private void OnCancelEdit(object sender, RoutedEventArgs e)
    {
        if (_editingExisting && _selectedId != null)
        {
            var entry = _vault.Data?.Entries.FirstOrDefault(en => en.Id == _selectedId);
            if (entry != null)
            {
                ShowDetail(entry);
                return;
            }
        }

        ClearEditor();
        RefreshList(string.IsNullOrEmpty(SearchBox.Text) ? null : SearchBox.Text);
    }

    /// <summary>填充编辑器字段与共享头（不含模式切换）。</summary>
    private void FillEditorFields(VaultEntry entry)
    {
        _editorLoading = true;
        var title = DisplayTitle(entry.Title);
        var avatar = AvatarFor(title);
        DetailHeader.Text = title;
        DetailInitial.Text = avatar.Initial;
        DetailInitial.Foreground = avatar.Foreground;
        DetailMeta.Text = $"上次修改 {entry.UpdatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
        _editingExisting = true;
        TitleBox.Text = entry.Title;
        UsernameBox.Text = entry.Username;
        PasswordBox.Password = entry.Password;
        PasswordTextBox.Text = entry.Password;
        UrlBox.Text = entry.Url;
        NotesBox.Text = entry.Notes;
        _editorLoading = false;
    }

    /// <summary>填充只读名片：字段值 + 空字段整行隐藏 + 末行去掉分隔线。</summary>
    private void FillDetailView(VaultEntry entry)
    {
        DetailUsername.Text = entry.Username;
        DetailUserRow.Visibility = entry.Username.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        DetailPasswordValue.Text = "••••••••";
        DetailPasswordRow.Visibility = entry.Password.Length == 0 && entry.Kind == "note"
            ? Visibility.Collapsed
            : Visibility.Visible;
        DetailUrlValue.Text = entry.Url;
        DetailUrlRow.Visibility = entry.Url.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        DetailNotes.Text = entry.Notes;
        DetailNotesRow.Visibility = entry.Notes.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        // 发丝分隔线挂在每行底边：把最后一个可见行的底线收掉，避免卡片底部出现孤线。
        var rows = new[] { DetailUserRow, DetailPasswordRow, DetailUrlRow, DetailNotesRow };
        Border? last = null;
        foreach (var row in rows)
        {
            if (row.Visibility == Visibility.Visible)
            {
                last = row;
            }
        }

        foreach (var row in rows)
        {
            row.BorderThickness = row == last ? new Thickness(0) : new Thickness(0, 0, 0, 1);
        }
    }

    private void OnToggleDetailReveal(object sender, RoutedEventArgs e)
    {
        if (_detailRevealed)
        {
            HideDetailReveal();
            return;
        }

        _detailRevealed = true;
        DetailPasswordValue.Text = CurrentPassword;
        DetailRevealButton.Content = "隐藏";
    }

    private void HideDetailReveal()
    {
        _detailRevealed = false;
        DetailPasswordValue.Text = "••••••••";
        DetailRevealButton.Content = "显示";
    }

    private void OnCopyDetailUrl(object sender, RoutedEventArgs e) => CopySecret(UrlBox.Text, "网址");

    private void ClearEditor()
    {
        _editorLoading = true;
        _selectedId = null;
        _editingExisting = false;
        _editorVisible = false;
        DetailHeader.Text = "条目详情";
        DetailInitial.Text = "";
        DetailMeta.Text = "";
        TitleBox.Text = "";
        UsernameBox.Text = "";
        PasswordBox.Password = "";
        PasswordTextBox.Text = "";
        UrlBox.Text = "";
        NotesBox.Text = "";
        HideReveal();
        _editorLoading = false;
        MarkEditorClean();
        SyncLayout();
    }

    private void OnEditorDirty(object sender, RoutedEventArgs e)
    {
        if (!_xamlReady || _editorLoading || !_vault.IsUnlocked || !_editorVisible)
        {
            return;
        }

        // 标题改动实时同步到详情头，不必等保存。
        if (ReferenceEquals(sender, TitleBox))
        {
            var title = DisplayTitle(TitleBox.Text.Trim());
            DetailHeader.Text = title;
            DetailInitial.Text = AvatarFor(title).Initial;
        }

        UnsavedBadge.Visibility = EditorSignature() == _cleanSignature
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void MarkEditorClean()
    {
        _cleanSignature = EditorSignature();
        UnsavedBadge.Visibility = Visibility.Collapsed;
    }

    private string EditorSignature() => string.Join(
        '\u0001',
        TitleBox.Text,
        UsernameBox.Text,
        CurrentPassword,
        UrlBox.Text,
        NotesBox.Text);

    private VaultEntry CollectEditor()
    {
        var now = DateTimeOffset.UtcNow;
        return new VaultEntry
        {
            Id = _editingExisting && _selectedId != null ? _selectedId : Guid.NewGuid().ToString("N"),
            Kind = "login",
            Title = TitleBox.Text.Trim(),
            // 「显示密码」态下明文在 TextBox 里，PasswordBox 是旧的，取可见的那个。
            Username = UsernameBox.Text.Trim(),
            Password = PasswordBox.Visibility == Visibility.Visible ? PasswordBox.Password : PasswordTextBox.Text,
            Url = UrlBox.Text.Trim(),
            TotpSecret = "",
            Tags = [],
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
        _editorVisible = true;
        DetailHeader.Text = "新建条目";
        DetailInitial.Text = "+";
        DetailMeta.Text = "草稿 · 尚未保存";
        SyncLayout();
        DetailPane.ChangeView(null, 0, null, true);
        TitleBox.Focus(FocusState.Programmatic);
    }

    private void OnSaveEntry(object sender, RoutedEventArgs e)
    {
        if (!_vault.IsUnlocked || !_editorVisible)
        {
            return;
        }

        var entry = CollectEditor();
        if (entry.Title.Length == 0)
        {
            _host.Toast.Show("标题不能为空");
            TitleBox.Focus(FocusState.Programmatic);
            return;
        }

        var data = _vault.Data!;
        var index = data.Entries.FindIndex(en => en.Id == entry.Id);
        if (index >= 0)
        {
            var previous = data.Entries[index];

            // 把"被覆盖的那一版"存进历史，改错了还能退回上一步。
            // 注意是拿 previous 去记录（它才是旧内容），再把整条历史链交给新条目。
            previous.History ??= [];
            VaultHistoryService.Record(previous, previous.UpdatedUtc);
            entry.History = previous.History;
            entry.CreatedUtc = previous.CreatedUtc;
            data.Entries[index] = entry;
        }
        else
        {
            data.Entries.Add(entry);
        }

        try
        {
            _vault.RequestSave();
            _host.Toast.Show("已保存");
            _selectedId = entry.Id;
            ShowDetail(entry);
            RefreshList(string.IsNullOrEmpty(SearchBox.Text) ? null : SearchBox.Text);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            _host.Toast.Show("保存失败：" + ex.Message);
        }
    }

    private async void OnDeleteEntry(object sender, RoutedEventArgs e)
    {
        if (!_vault.IsUnlocked || _selectedId == null)
        {
            return;
        }

        var entry = _vault.Data?.Entries.FirstOrDefault(en => en.Id == _selectedId);
        if (entry == null)
        {
            return;
        }

        var confirmed = await ShowAppConfirmAsync(
            "移到回收站",
            $"把「{DisplayTitle(entry.Title)}」移到回收站？\n可以在「更多 → 回收站」里恢复；"
            + $"超过 {VaultLimits.TrashRetainDays} 天或超过 {VaultLimits.TrashMaxEntries} 条时会自动清理。",
            "移到回收站");
        if (!confirmed)
        {
            return;
        }

        // 不再直接销毁：删除是可逆的，只有回收站里的"彻底删除"才不可逆。
        if (!_vault.MoveToRecycleBin(_selectedId))
        {
            return;
        }
        try
        {
            _vault.RequestSave();
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

    private void OnToggleReveal(object sender, RoutedEventArgs e)
    {
        // 显隐切换不改内容，抑制 PasswordChanged/TextChanged 误报脏标记。
        _editorLoading = true;
        if (PasswordBox.Visibility == Visibility.Visible)
        {
            PasswordTextBox.Text = PasswordBox.Password;
            PasswordBox.Visibility = Visibility.Collapsed;
            PasswordTextBox.Visibility = Visibility.Visible;
            RevealButton.Content = "隐藏";
            _editorLoading = false;
        }
        else
        {
            _editorLoading = false;
            HideReveal();
        }
    }

    private void HideReveal()
    {
        _editorLoading = true;
        PasswordBox.Password = PasswordTextBox.Text;
        PasswordBox.Visibility = Visibility.Visible;
        PasswordTextBox.Visibility = Visibility.Collapsed;
        RevealButton.Content = "显示";
        _editorLoading = false;
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

    private void OnCopyPassword(object sender, RoutedEventArgs e) => CopySecret(CurrentPassword, "密码");

    private void OnCopyUsername(object sender, RoutedEventArgs e) => CopySecret(UsernameBox.Text, "用户名");

    private async void OnOpenUrl(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text.Trim();
        if (url.Length == 0)
        {
            return;
        }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        try
        {
            await Launcher.LaunchUriAsync(new Uri(url));
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            _host.Toast.Show("无法打开网址：" + ex.Message);
        }
    }

    private void OnAccNew(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OnNewEntry(this, new RoutedEventArgs());
    }

    private void OnAccSearch(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (_vault.IsUnlocked)
        {
            SearchBox.Focus(FocusState.Keyboard);
        }
    }

    private void OnAccSave(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (_vault.IsUnlocked && _editorVisible)
        {
            OnSaveEntry(this, new RoutedEventArgs());
        }
    }

    private void OnAccDelete(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (!_vault.IsUnlocked || _selectedId == null)
        {
            return;
        }

        // 输入控件里的 Delete 是编辑键，只有焦点不在任何输入控件上时才删条目。
        if (FocusManager.GetFocusedElement() is Microsoft.UI.Xaml.Controls.TextBox
            or Microsoft.UI.Xaml.Controls.PasswordBox
            or Microsoft.UI.Xaml.Controls.ComboBox
            or Microsoft.UI.Xaml.Controls.NumberBox
            or Microsoft.UI.Xaml.Controls.AutoSuggestBox)
        {
            return;
        }

        OnDeleteEntry(this, new RoutedEventArgs());
    }
}
