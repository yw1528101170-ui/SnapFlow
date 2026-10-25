using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace Jietu;

/// <summary>PasswordWindow 的「列表 / 搜索 / 筛选 / 排序 / 布局」部分（partial）。</summary>
public sealed partial class PasswordWindow
{
    /// <summary>按当前搜索框内容刷新列表（各事件处理器的统一入口，避免查询串到处内联）。</summary>
    private void RefreshList() => RefreshList(string.IsNullOrEmpty(SearchBox.Text) ? null : SearchBox.Text);

    private void RefreshList(string? query)
    {
        if (!_vault.IsUnlocked)
        {
            return;
        }

        var data = _vault.Data!;
        var rows = new List<VaultRow>();
        foreach (var entry in FilteredEntries(data.Entries, query))
        {
            var title = DisplayTitle(entry.Title);
            var avatar = AvatarFor(title);
            rows.Add(new VaultRow
            {
                Id = entry.Id,
                Title = title,
                Subtitle = BuildSubtitle(entry),
                Initial = avatar.Initial,
                AvatarBrush = avatar.Background,
                AvatarFgBrush = avatar.Foreground,
            });
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

        // 计数走标题栏副标题（同 HistoryWindow 的 HeaderMeta）：未过滤只给总数，搜索中给「命中 / 总数」。
        var narrowed = query != null;
        CountBadge.Text = narrowed
            ? $"{rows.Count} / {data.Entries.Count} 个条目"
            : $"{data.Entries.Count} 个条目";
        CountBadge.Visibility = Visibility.Visible;

        _visibleRowCount = rows.Count;
        if (data.Entries.Count == 0)
        {
            EmptyTitle.Text = "还没有条目";
            EmptyHint.Text = "保存用户名与密码，全部在本机加密存储。";
        }

        UpdateSearchAffordances();
        SyncLayout();
    }

    private IEnumerable<VaultEntry> FilteredEntries(List<VaultEntry> entries, string? query)
    {
        IEnumerable<VaultEntry> source = entries;
        if (query != null)
        {
            source = source.Where(e => Matches(e, query));
        }

        // 标题排序用当前区域文化比较（中文按拼音），空标题统一沉到「（无标题）」位置。
        return _sort == VaultSort.Title
            ? source.OrderBy(e => DisplayTitle(e.Title), StringComparer.CurrentCultureIgnoreCase)
            : source.OrderByDescending(e => e.UpdatedUtc);
    }

    private static string DisplayTitle(string title)
        => string.IsNullOrWhiteSpace(title) ? "（无标题）" : title;

    private static string BuildSubtitle(VaultEntry entry)
    {
        if (entry.Kind == "note")
        {
            return "安全笔记";
        }

        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(entry.Username))
        {
            parts.Add(entry.Username.Trim());
        }

        if (!string.IsNullOrWhiteSpace(entry.Url))
        {
            parts.Add(ShortHost(entry.Url));
        }

        return parts.Count > 0 ? string.Join("  ·  ", parts) : "未填写用户名";
    }

    private static string ShortHost(string url)
    {
        var text = url.Trim();
        if (!Uri.TryCreate(text.Contains("://", StringComparison.Ordinal) ? text : "https://" + text, UriKind.Absolute, out var uri))
        {
            return text;
        }

        return uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
    }

    private static (string Initial, Brush Background, Brush Foreground) AvatarFor(string title)
    {
        var index = (title.GetHashCode() & 0x7FFFFFFF) % AvatarPalette.Length;
        var color = AvatarPalette[index];
        var initial = char.ToUpperInvariant(title.Trim().FirstOrDefault('?'));
        return (initial.ToString(),
            new SolidColorBrush(Color.FromArgb(0x2E, color.R, color.G, color.B)),
            new SolidColorBrush(color));
    }

    private static bool Matches(VaultEntry entry, string query)
        => entry.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
           || entry.Username.Contains(query, StringComparison.OrdinalIgnoreCase)
           || entry.Url.Contains(query, StringComparison.OrdinalIgnoreCase)
           || entry.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase));

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _vault.TouchActivity();
        RefreshList();
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || SearchBox.Text.Length == 0)
        {
            return;
        }

        e.Handled = true;
        ClearSearch();
    }

    private void OnClearSearch(object sender, RoutedEventArgs e) => ClearSearch();

    private void ClearSearch()
    {
        if (SearchBox.Text.Length == 0)
        {
            return;
        }

        SearchBox.Text = "";
        SearchBox.Focus(FocusState.Keyboard);
    }

    private void OnClearFilters(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
    }

    private void OnSortByUpdated(object sender, RoutedEventArgs e) => SetSort(VaultSort.Updated);

    private void OnSortByTitle(object sender, RoutedEventArgs e) => SetSort(VaultSort.Title);

    private void SetSort(VaultSort sort)
    {
        _sort = sort;
        SortLabel.Text = sort == VaultSort.Title ? "标题 A → Z" : "最近修改";
        RefreshList();
    }

    private void UpdateSearchAffordances()
    {
        SearchClearButton.Visibility = SearchBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSearchFocus(object sender, RoutedEventArgs e)
    {
        Motion.Fade(SearchFocusRing, 0, 1, 170, Motion.Ease);
    }

    private void OnSearchFocusOut(object sender, RoutedEventArgs e)
    {
        Motion.Fade(SearchFocusRing, 1, 0, 190, Motion.EaseIn);
    }

    private void SyncLayout()
    {
        if (!_vault.IsUnlocked || _vault.Data == null)
        {
            DetailPane.Visibility = Visibility.Collapsed;
            DetailPlaceholder.Visibility = Visibility.Collapsed;
            EditorActionBar.Visibility = Visibility.Collapsed;
            return;
        }

        var paneOpen = _editorVisible || _selectedId != null;
        var emptyVault = _vault.Data.Entries.Count == 0;

        // 空库时列表里没有任何可浏览的东西：整列收起，宽度让给表单/欢迎页。
        // 顺带消掉「左边写着『点新建开始』、右边正在新建」这种自相矛盾的画面。
        ListCard.Visibility = emptyVault ? Visibility.Collapsed : Visibility.Visible;
        ListColumn.Width = emptyVault ? new GridLength(0) : new GridLength(272);
        ContentGrid.ColumnSpacing = emptyVault ? 0 : 12;

        EmptyPane.Visibility = emptyVault && !paneOpen ? Visibility.Visible : Visibility.Collapsed;
        if (emptyVault)
        {
            // 密码库热键可在设置页改，提示文案跟着配置走，不写死 Alt+P。
            var vaultHotkey = HotkeyGesture.TryParse(_host.Settings.VaultHotkey, out var gesture, out _)
                ? gesture.Display
                : _host.Settings.VaultHotkey;
            EmptyHotkeyHint.Text = $"{vaultHotkey} 可随时唤出密码库";
        }
        // 空库时排序也无事可做，直接收起。
        SortButton.Visibility = emptyVault ? Visibility.Collapsed : Visibility.Visible;

        // 列表为空时给就地提示；只有「筛掉」会出现这个面板（空库整个列表都收起了）。
        var noRows = _visibleRowCount == 0;
        ListEmptyPanel.Visibility = !emptyVault && noRows
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!emptyVault && noRows)
        {
            ListEmptyTitle.Text = "没有匹配的条目";
            ListEmptyHint.Text = "换个关键词再试。";
            ListEmptyIcon.Glyph = "\uE721";
            ListEmptyClearButton.Visibility = Visibility.Visible;
        }

        // 三态：编辑态（表单）→ 详情态（只读名片）→ 引导占位。
        DetailPane.Visibility = paneOpen ? Visibility.Visible : Visibility.Collapsed;
        EditorRoot.Visibility = _editorVisible ? Visibility.Visible : Visibility.Collapsed;
        DetailView.Visibility = !_editorVisible && _selectedId != null ? Visibility.Visible : Visibility.Collapsed;
        // 详情/编辑共用一个头部：「编辑」按钮只在详情态出现。
        EditButton.Visibility = !_editorVisible && _selectedId != null ? Visibility.Visible : Visibility.Collapsed;
        DetailPlaceholder.Visibility = !paneOpen && !emptyVault ? Visibility.Visible : Visibility.Collapsed;

        // 还没保存进库的草稿没有「删除」可言，垃圾桶留着只会诱导误点。
        DeleteButton.Visibility = _editingExisting ? Visibility.Visible : Visibility.Collapsed;
        EditorActionBar.Visibility = _editorVisible ? Visibility.Visible : Visibility.Collapsed;
    }
}
