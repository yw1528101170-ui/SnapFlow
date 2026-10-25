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
        // 列表刷新（搜索/排序）会重设选中项：同一条目已在编辑时不再重载，
        // 否则未保存的修改会被库里旧值覆盖。
        if (row.Id == _selectedId && _editorVisible)
        {
            return;
        }

        _selectedId = row.Id;
        var entry = _vault.Data?.Entries.FirstOrDefault(en => en.Id == row.Id);
        if (entry == null)
        {
            return;
        }

        LoadEditor(entry);
    }

    private void OnKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_xamlReady)
        {
            return;
        }

        CredentialFields.Visibility = IsNoteKind ? Visibility.Collapsed : Visibility.Visible;
        // 笔记没有凭据，复制密码/用户名两个按钮留着只会误导。
        var credentialButtons = IsNoteKind ? Visibility.Collapsed : Visibility.Visible;
        CopyPasswordButton.Visibility = credentialButtons;
        CopyUsernameButton.Visibility = credentialButtons;

        // 草稿的副标题跟着类型走（已有条目显示的是「上次修改 …」，不该被覆盖）。
        if (_editorVisible && !_editingExisting)
        {
            DetailMeta.Text = IsNoteKind ? "草稿 · 安全笔记，尚未保存" : "草稿 · 登录凭据，尚未保存";
        }
    }

    private void LoadEditor(VaultEntry entry)
    {
        _editorLoading = true;
        _editorVisible = true;
        var title = DisplayTitle(entry.Title);
        var avatar = AvatarFor(title);
        DetailHeader.Text = title;
        DetailInitial.Text = avatar.Initial;
        DetailInitial.Foreground = avatar.Foreground;
        DetailMeta.Text = $"上次修改 {entry.UpdatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}"
                          + (entry.Kind == "note" ? " · 安全笔记" : " · 登录");
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
        _editorLoading = false;
        MarkEditorClean();
        SyncLayout();
        DetailPane.ChangeView(null, 0, null, true);
    }

    private void ClearEditor()
    {
        _editorLoading = true;
        _selectedId = null;
        _editingExisting = false;
        _editorVisible = false;
        DetailHeader.Text = "条目详情";
        DetailInitial.Text = "";
        DetailMeta.Text = "";
        KindCombo.SelectedIndex = 0;
        TitleBox.Text = "";
        UsernameBox.Text = "";
        PasswordBox.Password = "";
        PasswordTextBox.Text = "";
        UrlBox.Text = "";
        TotpBox.Text = "";
        TagsBox.Text = "";
        NotesBox.Text = "";
        TotpDisplayRow.Visibility = Visibility.Collapsed;
        HideReveal();
        StopTotpTimer();
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
        KindCombo.SelectedIndex.ToString(),
        TitleBox.Text,
        UsernameBox.Text,
        CurrentPassword,
        UrlBox.Text,
        TotpBox.Text,
        TagsBox.Text,
        NotesBox.Text);

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
        _editorVisible = true;
        DetailHeader.Text = "新建条目";
        DetailInitial.Text = "+";
        DetailMeta.Text = IsNoteKind ? "草稿 · 安全笔记，尚未保存" : "草稿 · 登录凭据，尚未保存";
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
            _editingExisting = true;
            var title = DisplayTitle(entry.Title);
            DetailHeader.Text = title;
            DetailInitial.Text = AvatarFor(title).Initial;
            DetailMeta.Text = $"上次修改 {entry.UpdatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}"
                              + (entry.Kind == "note" ? " · 安全笔记" : " · 登录");
            MarkEditorClean();
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
            $"把这个条目移到回收站？\n\n可以在「更多 → 回收站」里恢复；超过 {VaultLimits.TrashRetainDays} 天或超过 {VaultLimits.TrashMaxEntries} 条时会自动清理。",
            "SnapFlow - 移到回收站",
            NativeMethods.MbYesNo | NativeMethods.MbIconQuestion | NativeMethods.MbDefButton2);
        if (res != NativeMethods.IdYes)
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
        if (!_vault.IsUnlocked || !_editorVisible || _selectedId == null)
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
