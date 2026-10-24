using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;
using WinRT.Interop;

namespace Jietu;

public sealed partial class HistoryWindow : Window
{
    private enum HistoryFilter
    {
        All,
        Text,
        Image,
        Shot,
        Pinned
    }

    private const int PageSize = 48;
    private readonly AppHost _host;
    private readonly ObservableCollection<ClipRow> _rows = [];
    private readonly DispatcherTimer _searchTimer;
    private HistoryFilter _filter = HistoryFilter.All;
    private HistoryCursor? _cursor;
    private ScrollViewer? _scroll;
    private bool _open;
    private bool _allowClose;
    private bool _hiding;
    private bool _hasMore = true;
    private bool _loadingPage;
    private bool _followUp;
    private bool _awaitingLayout;
    private int _fillLoads;
    private int _epoch;
    private readonly HashSet<string> _loadedIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _hidden = new(StringComparer.Ordinal);
    private ClipRow? _watched;
    private bool _previewExplicitlyOpened;
    private bool _previewOpen;
    private int _previewToken;
    private int _entranceBudget;
    private double _thumbX;
    private const string RowActionsTag = "row-actions";
	public bool IsOpen => _open;

	public PasteTarget PasteTarget { get; private set; }

	private string FilterKey => _filter switch
	{
		HistoryFilter.Text => "text", 
		HistoryFilter.Image => "image", 
		HistoryFilter.Shot => "shot", 
		HistoryFilter.Pinned => "pinned", 
		_ => "all", 
	};

	public HistoryWindow(AppHost host)
	{
		_host = host;
		InitializeComponent();
		WindowChrome.Apply(this, TitleBar, Root);
		ItemList.ItemsSource = _rows;
		ItemList.Loaded += (object _, RoutedEventArgs _) =>
		{
			EnsureScroll();
		};
		Root.PreviewKeyDown += OnPreviewKey;
		_searchTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(160L)
		};
		_searchTimer.Tick += (object? _, object _) =>
		{
			_searchTimer.Stop();
			if (_open)
			{
				Reload();
			}
		};
		if (AppWindow.Presenter is OverlappedPresenter overlappedPresenter)
		{
			overlappedPresenter.IsAlwaysOnTop = true;
			overlappedPresenter.PreferredMinimumWidth = 360;
			overlappedPresenter.PreferredMinimumHeight = 480;
		}
		try
		{
			AppWindow.IsShownInSwitchers = false;
		}
		catch
		{
		}
		AppWindow.Closing += (AppWindow _, AppWindowClosingEventArgs e) =>
		{
			if (!_allowClose && !_hiding)
			{
				e.Cancel = true;
				Dismiss();
			}
		};
	}

	public void AllowClose()
	{
		_allowClose = true;
	}

	public void Present(PasteTarget pasteTarget)
	{
		PasteTarget = pasteTarget;
		_open = true;
		_previewExplicitlyOpened = false;
		_previewOpen = false;
		_previewToken++;
		PreviewHost.Visibility = Visibility.Collapsed;
		ClearConfirmFlyout?.Hide();
		SetPaused(_host.IsPaused);
		UpdateFilterChips();
		if (AppWindow.Presenter is OverlappedPresenter overlappedPresenter)
		{
			overlappedPresenter.IsAlwaysOnTop = true;
		}
		nint hwnd = WindowNative.GetWindowHandle(this);
		AppWindow.MoveAndResize(ScreenUtil.PlaceNearCursor(hwnd, 420, 680));
		try
		{
			AppWindow.IsShownInSwitchers = true;
		}
		catch
		{
		}
		Activate();
		NativeMethods.BringToForeground(hwnd);
		DispatcherQueue.TryEnqueue(() =>
		{
			if (_open)
			{
				NativeMethods.BringToForeground(hwnd);
			}
		});
		_searchTimer.Stop();
		// 打开窗口默认不选中任何一条：选中意味着“即将粘贴这一条”，
		// 强选第一项会让用户以为已经命中，也把行内动作按钮常显出来。
		ItemList.SelectedIndex = -1;
		Reload();
		SearchBox.Focus(FocusState.Programmatic);
		UpdateSearchClear();
		// 本次打开的第一批行做错峰入场
		_entranceBudget = 14;
		Motion.WindowIn(Root);
	}

	public void Dismiss()
	{
		_open = false;
		_searchTimer.Stop();
		ClearConfirmFlyout?.Hide();
		_hiding = true;
		AppWindow.Hide();
		_hiding = false;
		try
		{
			AppWindow.IsShownInSwitchers = false;
		}
		catch
		{
		}
		if (AppWindow.Presenter is OverlappedPresenter overlappedPresenter)
		{
			overlappedPresenter.IsAlwaysOnTop = false;
		}
		NativeMethods.SetWindowPos(WindowNative.GetWindowHandle(this), NativeMethods.HwndNotopmost, 0, 0, 0, 0, 19u);
	}

	public void SetPaused(bool paused)
	{
		PausedBar.IsOpen = paused;
	}

	public void NotifyInserted(ClipRecord record)
	{
		if (!_open)
		{
			return;
		}
		if (!string.IsNullOrWhiteSpace(SearchBox.Text))
		{
			Reload();
			return;
		}
		for (int i = 0; i < _rows.Count; i++)
		{
			if (!(_rows[i].Id != record.Id))
			{
				_rows.RemoveAt(i);
				_loadedIds.Remove(record.Id);
				break;
			}
		}
		if (!Accepts(record))
		{
			UpdateEmpty();
			return;
		}
		InsertRow(ClipRow.From(record));
		UpdateEmpty();
	}

	public void NotifyPurged()
	{
		if (_open)
		{
			Reload();
		}
	}

	public void NotifyRecognized(string id, string text)
	{
		if (!_open)
		{
			return;
		}
		ClipRow clipRow = _rows.FirstOrDefault((ClipRow r) => r.Id == id);
		if (clipRow != null)
		{
			clipRow.UpdateRecognizedText(text);
			if (_previewExplicitlyOpened && ItemList.SelectedItem == clipRow)
			{
				ShowPreview(clipRow);
			}
		}
	}

	internal void Reload()
	{
		_epoch++;
		_rows.Clear();
		_loadedIds.Clear();
		_hidden.Clear();
		_cursor = null;
		_hasMore = true;
		_followUp = false;
		_fillLoads = 0;
		if (!_loadingPage)
		{
			LoadPage();
		}
	}

	private async void LoadPage()
	{
		if (_loadingPage || !_hasMore || !_open)
		{
			return;
		}
		_loadingPage = true;
		int epoch = _epoch;
		string search = SearchBox.Text;
		string filter = FilterKey;
		HistoryCursor? cursor = _cursor;
		IReadOnlyList<ClipRecord> records = null;
		try
		{
			records = await Task.Run(() => _host.Search(search, filter, 48, cursor));
		}
		catch (Exception exception)
		{
			Log.Error(exception);
		}
		try
		{
			if (epoch != _epoch || !_open)
			{
				_loadingPage = false;
				if (_open && epoch != _epoch)
				{
					LoadPage();
				}
				return;
			}
			if (records == null)
			{
				_loadingPage = false;
				return;
			}
			if (records.Count < 48)
			{
				_hasMore = false;
			}
			foreach (ClipRecord item in records)
			{
				_cursor = new HistoryCursor(item.Pinned ? 1 : 0, item.CreatedUtc, item.Id);
				if (!_hidden.Contains(item.Id) && _loadedIds.Add(item.Id))
				{
					_rows.Add(ClipRow.From(item));
				}
			}
			_loadingPage = false;
			StampShortcuts();
			UpdateEmpty();
			EnsureScroll();
			DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ContinueIfNeeded);
		}
		catch (Exception exception)
		{
			_loadingPage = false;
			Log.Error(exception);
		}
	}

	private void ContinueIfNeeded()
	{
		if (!_open || !_hasMore || _loadingPage || _scroll == null)
		{
			return;
		}
		if (_scroll.ViewportHeight <= 1.0)
		{
			if (!_awaitingLayout)
			{
				_awaitingLayout = true;
				_scroll.SizeChanged += (object _, SizeChangedEventArgs _) =>
				{
					ContinueIfNeeded();
				};
			}
		}
		else if (_scroll.ScrollableHeight <= 8.0)
		{
			if (_fillLoads < 3)
			{
				_fillLoads++;
				LoadPage();
			}
		}
		else if (_followUp && _scroll.ScrollableHeight - _scroll.VerticalOffset < 240.0)
		{
			_followUp = false;
			LoadPage();
		}
	}

	private void EnsureScroll()
	{
		if (!(_scroll != null))
		{
			_scroll = FindScroll(ItemList);
			if (_scroll != null)
			{
				_scroll.ViewChanged += OnViewChanged;
			}
		}
	}

	private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
	{
		if (!e.IsIntermediate && !(_scroll == null) && _open && _hasMore && !_loadingPage && _scroll.ScrollableHeight > 8.0 && _scroll.ScrollableHeight - _scroll.VerticalOffset < 240.0)
		{
			_followUp = true;
			LoadPage();
		}
	}

	private static ScrollViewer? FindScroll(DependencyObject root)
	{
		if (root is ScrollViewer result)
		{
			return result;
		}
		int childrenCount = VisualTreeHelper.GetChildrenCount(root);
		for (int i = 0; i < childrenCount; i++)
		{
			ScrollViewer scrollViewer = FindScroll(VisualTreeHelper.GetChild(root, i));
			if (scrollViewer != null)
			{
				return scrollViewer;
			}
		}
		return null;
	}

	private void OnFilterClick(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<HistoryFilter>(tag, out var result) && _filter != result)
		{
			_filter = result;
			UpdateFilterChips();
			Reload();
		}
	}

	private void UpdateFilterChips()
	{
		SetChipActive(FilterAllButton, _filter == HistoryFilter.All);
		SetChipActive(FilterTextButton, _filter == HistoryFilter.Text);
		SetChipActive(FilterImageButton, _filter == HistoryFilter.Image);
		SetChipActive(FilterShotButton, _filter == HistoryFilter.Shot);
		SetChipActive(FilterPinnedButton, _filter == HistoryFilter.Pinned);
		LayoutSegmentThumb(animate: true);
	}

	/// <summary>把分段控件的滑块滑到当前筛选位置（首帧直接落位，避免入场时飞入）。</summary>
	private void LayoutSegmentThumb(bool animate)
	{
		double width = FilterStrip.ActualWidth - 6.0;
		if (width <= 8.0)
		{
			return;
		}
		double segment = width / 5.0;
		SegThumb.Width = segment;
		double target = segment * (int)_filter;
		if (!animate)
		{
			_thumbX = target;
			SegThumbTr.TranslateX = target;
			return;
		}
		double from = _thumbX;
		_thumbX = target;
		Motion.MoveX(SegThumb, SegThumbTr, from, target, 340, Motion.Spring(0.18));
	}

	private void OnFilterStripSizeChanged(object sender, SizeChangedEventArgs e)
	{
		LayoutSegmentThumb(animate: false);
	}

	private static void SetChipActive(Button button, bool active)
	{
		button.Foreground = (Brush)Application.Current.Resources[active ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"];
		button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
	}

	private void InsertRow(ClipRow row)
	{
		_hidden.Remove(row.Id);
		if (!_rows.Any((ClipRow existing) => existing.Id == row.Id))
		{
			_loadedIds.Add(row.Id);
			_rows.Insert(IndexFor(row), row);
			StampShortcuts();
		}
	}

	private int IndexFor(ClipRow row)
	{
		for (int i = 0; i < _rows.Count; i++)
		{
			if (Compare(row, _rows[i]) < 0)
			{
				return i;
			}
		}
		return _rows.Count;
	}

	private static int Compare(ClipRow left, ClipRow right)
	{
		if (left.Pinned != right.Pinned)
		{
			if (!left.Pinned)
			{
				return 1;
			}
			return -1;
		}
		return right.CreatedUtc.CompareTo(left.CreatedUtc);
	}

	private bool Accepts(ClipRecord record)
	{
		switch (_filter)
		{
		case HistoryFilter.Text:
		{
			bool result;
			switch (record.Kind)
			{
			case "text":
			case "rich":
			case "file":
				result = true;
				break;
			default:
				result = false;
				break;
			}
			return result;
		}
		case HistoryFilter.Image:
			return record.Kind == "image" && record.Source != "screenshot";
		case HistoryFilter.Shot:
			return record.Source == "screenshot";
		case HistoryFilter.Pinned:
			return record.Pinned;
		default:
			return true;
		}
	}

	private void UpdateEmpty()
	{
		UpdateHeaderMeta();
		if (_rows.Count > 0)
		{
			EmptyState.Visibility = Visibility.Collapsed;
			ItemList.Visibility = Visibility.Visible;
			return;
		}
		bool flag = !string.IsNullOrWhiteSpace(SearchBox.Text);
		bool appeared = EmptyState.Visibility != Visibility.Visible;
		EmptyState.Visibility = Visibility.Visible;
		ItemList.Visibility = Visibility.Collapsed;
		EmptyIcon.Glyph = (flag ? "\ue721" : "\ue77f");
		TextBlock emptyText = EmptyText;
		string text;
		if (flag)
		{
			text = "没有匹配的内容";
		}
		else
		{
			text = ((_filter == HistoryFilter.All) ? "还没有复制记录" : "没有这类记录");
		}
		emptyText.Text = text;
		if (appeared)
		{
			var transform = Motion.Prep(EmptyState);
			Motion.Fade(EmptyState, 0, 1, 220, Motion.Ease);
			Motion.Scale(EmptyState, transform, 0.96, 1, 340, Motion.Spring(0.2));
		}
	}

	/// <summary>标题栏副标题：仅有筛选/搜索时给出结果数，避免误导的总量。</summary>
	private void UpdateHeaderMeta()
	{
		bool filtered = !string.IsNullOrWhiteSpace(SearchBox.Text) || _filter != HistoryFilter.All;
		if (_rows.Count == 0)
		{
			HeaderMeta.Text = _host.IsPaused ? "记录已暂停" : "";
			return;
		}
		HeaderMeta.Text = filtered ? $"{_rows.Count} 条结果" : $"{_rows.Count} 项";
	}

	private void OnSearch(object sender, TextChangedEventArgs e)
	{
		UpdateSearchClear();
		_searchTimer.Stop();
		_searchTimer.Start();
	}

	private void UpdateSearchClear()
	{
		ClearSearchButton.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Collapsed : Visibility.Visible;
	}

	private void OnClearSearch(object sender, RoutedEventArgs e)
	{
		SearchBox.Text = string.Empty;
		SearchBox.Focus(FocusState.Programmatic);
	}

	private void OnSearchFocus(object sender, RoutedEventArgs e)
	{
		Motion.Fade(SearchFocusRing, 0, 1, 170, Motion.Ease);
	}

	private void OnSearchFocusOut(object sender, RoutedEventArgs e)
	{
		Motion.Fade(SearchFocusRing, 1, 0, 190, Motion.EaseIn);
	}

	private void StampShortcuts()
	{
		for (int i = 0; i < _rows.Count; i++)
		{
			_rows[i].SetShortcut((i < 9) ? (i + 1).ToString() : "");
		}
	}

	private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		foreach (object item in e.RemovedItems)
		{
			UpdateRowActions(item as ClipRow, show: false);
		}
		foreach (object item in e.AddedItems)
		{
			UpdateRowActions(item as ClipRow, show: true);
		}
		Watch(ItemList.SelectedItem as ClipRow);
	}

	private void UpdateRowActions(ClipRow? row, bool show)
	{
		if (row != null && ItemList.ContainerFromItem(row) is FrameworkElement container)
		{
			SetRowActions(container, show);
		}
	}

	private void OnRowPointerEntered(object sender, PointerRoutedEventArgs e)
	{
		if (sender is FrameworkElement surface)
		{
			SetRowActions(surface, show: true);
		}
	}

	private void OnRowPointerExited(object sender, PointerRoutedEventArgs e)
	{
		if (sender is FrameworkElement surface)
		{
			// 键盘选中的行始终保持动作按钮可见，不被指针离开抹掉
			if (surface.DataContext is ClipRow row && ReferenceEquals(ItemList.SelectedItem, row))
			{
				return;
			}
			SetRowActions(surface, show: false);
		}
	}

	/// <summary>行内动作按钮的淡入淡出（Apple 式“悬停才浮现”）。</summary>
	private static void SetRowActions(FrameworkElement surface, bool show)
	{
		FrameworkElement? panel = FindByTag(surface, RowActionsTag);
		if (panel == null)
		{
			return;
		}
		if (show)
		{
			Motion.Fade(panel, 0, 1, 170, Motion.Ease);
		}
		else
		{
			Motion.Fade(panel, 1, 0, 140, Motion.EaseIn);
		}
	}

	private static FrameworkElement? FindByTag(DependencyObject root, string tag)
	{
		int count = VisualTreeHelper.GetChildrenCount(root);
		for (int i = 0; i < count; i++)
		{
			DependencyObject child = VisualTreeHelper.GetChild(root, i);
			if (child is FrameworkElement element && element.Tag as string == tag)
			{
				return element;
			}
			FrameworkElement? nested = FindByTag(child, tag);
			if (nested != null)
			{
				return nested;
			}
		}
		return null;
	}

	/// <summary>预览卡片：自下而上弹出。</summary>
	private void ShowPreviewHost()
	{
		if (_previewOpen)
		{
			return;
		}
		_previewOpen = true;
		_previewToken++;
		PreviewHost.Visibility = Visibility.Visible;
		Motion.Fade(PreviewHost, 0, 1, 210, Motion.Ease);
		Motion.MoveY(PreviewHost, PreviewTr, 14, 0, 340, Motion.Spring(0.16));
		Motion.Scale(PreviewHost, PreviewTr, 0.98, 1, 340, Motion.Ease);
	}

	/// <summary>预览卡片：轻缩放 + 下沉淡出。</summary>
	private void HidePreview()
	{
		if (!_previewOpen)
		{
			PreviewHost.Visibility = Visibility.Collapsed;
			return;
		}
		_previewOpen = false;
		int token = ++_previewToken;
		Motion.MoveY(PreviewHost, PreviewTr, 0, 10, 180, Motion.EaseIn);
		Motion.Scale(PreviewHost, PreviewTr, 1, 0.985, 180, Motion.EaseIn);
		Motion.Fade(PreviewHost, 1, 0, 160, Motion.EaseIn, 0, () =>
		{
			if (token == _previewToken)
			{
				PreviewHost.Visibility = Visibility.Collapsed;
			}
		});
	}

	private void Watch(ClipRow? row)
	{
		if (_watched != null)
		{
			_watched.PropertyChanged -= OnWatchedChanged;
		}
		_watched = row;
		if (row != null)
		{
			row.PropertyChanged += OnWatchedChanged;
			if (row.IsImage)
			{
				row.EnsureThumbnailAsync();
			}
		}
		if (_previewExplicitlyOpened)
		{
			ShowPreview(row);
		}
		else
		{
			HidePreview();
		}
	}

	private void OnWatchedChanged(object? sender, PropertyChangedEventArgs e)
	{
		if ((e.PropertyName == "Preview" || e.PropertyName == "RecognizedText") && sender == _watched && _previewExplicitlyOpened)
		{
			ShowPreview(_watched);
		}
	}

	private void ShowPreview(ClipRow? row)
	{
		if (row == null)
		{
			HidePreview();
			return;
		}
		string text = (row.IsImage ? (row.RecognizedText ?? _host.ReadText(row.Id)) : _host.ReadText(row.Id));
		if (string.IsNullOrWhiteSpace(text) && !row.IsImage)
		{
			text = row.Preview;
		}
		text = text?.Trim() ?? "";
		if (string.IsNullOrWhiteSpace(text))
		{
			HidePreview();
			return;
		}
		if (row.IsImage)
		{
			PreviewTypeIcon.Glyph = "\ue8c1";
			PreviewCaption.Text = (string.IsNullOrEmpty(row.TimeText) ? "识别文字 (OCR)" : ("识别文字 (OCR) · " + row.TimeText));
		}
		else
		{
			PreviewTypeIcon.Glyph = "\ue8a5";
			PreviewCaption.Text = (string.IsNullOrEmpty(row.TimeText) ? "全文预览" : ("全文预览 · " + row.TimeText));
		}
		PreviewText.Text = text;
		var lineCount = text.Split('\n').Length;
		PreviewStats.Text = $"{lineCount} 行 · {text.Length:N0} 字符";
		ResetCopyButton();
		ShowPreviewHost();
		ConstrainPreview(PreviewHost.ActualWidth);
		ClipRow target = row;
		DispatcherQueue.TryEnqueue(() =>
		{
			if (_open && ItemList.SelectedItem == target)
			{
				ItemList.ScrollIntoView(target);
			}
		});
	}

	private void ToggleRowPreview(ClipRow row)
	{
		if (_previewExplicitlyOpened && ItemList.SelectedItem == row)
		{
			_previewExplicitlyOpened = false;
			HidePreview();
		}
		else
		{
			ItemList.SelectedItem = row;
			_previewExplicitlyOpened = true;
			ShowPreview(row);
		}
	}

	private void OnTogglePreviewClick(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement { DataContext: ClipRow dataContext })
		{
			ToggleRowPreview(dataContext);
		}
	}

	private void OnClosePreviewClick(object sender, RoutedEventArgs e)
	{
		_previewExplicitlyOpened = false;
		HidePreview();
	}

	private async void OnCopyPreviewClick(object sender, RoutedEventArgs e)
	{
		string text = PreviewText.Text;
		if (!string.IsNullOrEmpty(text))
		{
			try
			{
				_host.CopyText(text);
			}
			catch (Exception exception)
			{
				Log.Error(exception);
				CopyPreviewText.Text = "复制失败";
				await Task.Delay(1500);
				ResetCopyButton();
				return;
			}
			CopyPreviewIcon.Glyph = "\ue73e";
			CopyPreviewText.Text = "已复制";
			await Task.Delay(1500);
			ResetCopyButton();
		}
	}

	private void ResetCopyButton()
	{
		CopyPreviewIcon.Glyph = "\ue8c8";
		CopyPreviewText.Text = "复制";
	}

	private void OnPreviewHostSizeChanged(object sender, SizeChangedEventArgs e)
	{
		ConstrainPreview(e.NewSize.Width);
	}

	private void ConstrainPreview(double hostWidth)
	{
		double num = hostWidth - 28.0;
		if (num > 40.0)
		{
			PreviewText.MaxWidth = num;
		}
	}

	private void OnCancelClear(object sender, RoutedEventArgs e)
	{
		ClearConfirmFlyout?.Hide();
	}

	private void OnConfirmClear(object sender, RoutedEventArgs e)
	{
		ClearConfirmFlyout?.Hide();
		_host.ClearHistory();
		ClipRow.ClearThumbnails();
		Reload();
	}

	private void OnPinToDeskClick(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement { DataContext: ClipRow row })
		{
			if (row.IsImage && !string.IsNullOrEmpty(row.ImagePath) && File.Exists(row.ImagePath))
			{
				_host.PinFile(row.ImagePath);
				Dismiss();
			}
			else
			{
				var text = _host.ReadText(row.Id);
				if (string.IsNullOrWhiteSpace(text)) text = row.Preview;
				if (!string.IsNullOrWhiteSpace(text))
				{
					_host.PinText(text);
					Dismiss();
				}
			}
		}
	}

	private void OnCopyItemClick(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement { DataContext: ClipRow row })
		{
			_host.CopyAsync(row);
		}
	}

	private void OnDeleteClick(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement { DataContext: ClipRow dataContext })
		{
			DeleteRow(dataContext);
		}
	}

	private void OnPinClick(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement { DataContext: ClipRow dataContext })
		{
			TogglePin(dataContext);
		}
	}

	private void DeleteRow(ClipRow row)
	{
		ClipRow.ForgetThumbnail(row.ImagePath);
		_hidden.Add(row.Id);
		_loadedIds.Remove(row.Id);
		_host.Delete(row);
		int removedIndex = _rows.IndexOf(row);
		_rows.Remove(row);
		StampShortcuts();
		// 删除后把选中落到相邻行（删的是最后一行就往上挪），连续按 Del 不会断；
		// 列表已空则保持空选。
		if (removedIndex >= 0 && _rows.Count > 0)
		{
			ItemList.SelectedIndex = Math.Clamp(removedIndex, 0, _rows.Count - 1);
		}

		UpdateEmpty();
	}

	private void TogglePin(ClipRow row)
	{
		row.Pinned = !row.Pinned;
		_host.SetPinned(row.Id, row.Pinned);
		_rows.Remove(row);
		InsertRow(row);
	}

	private void OnItemClick(object sender, ItemClickEventArgs e)
	{
		if (!IsInsideButton(e.OriginalSource as DependencyObject) && e.ClickedItem is ClipRow row)
		{
			_host.PasteAsync(row);
		}
	}

	private void OnItemRightTapped(object sender, RightTappedRoutedEventArgs e)
	{
		if (!(sender is FrameworkElement { DataContext: var dataContext } frameworkElement))
		{
			return;
		}

		if (!(dataContext is ClipRow row))
		{
			return;
		}

		e.Handled = true;
		ItemList.SelectedItem = row;
		MenuFlyout menuFlyout = new MenuFlyout();

		// ① 主操作：最高频，固定在最上面
		menuFlyout.Items.Add(MenuItem("粘贴", MenuGlyph.Paste, () => _host.PasteAsync(row), "↵"));
		menuFlyout.Items.Add(MenuItem("复制", MenuGlyph.Copy, () => _host.CopyAsync(row), "Ctrl+C"));
		if (row.Kind == ClipKind.Rich)
		{
			menuFlyout.Items.Add(MenuItem("粘贴纯文本", MenuGlyph.PlainText, () => _host.PastePlainAsync(row), "Shift+↵"));
		}

		// ② 内容工具：随笔记录类型变化
		var tools = new List<MenuFlyoutItem>();
		if (row.IsImage)
		{
			if (row.HasRecognizedText || !string.IsNullOrWhiteSpace(_host.ReadText(row.Id)))
			{
				tools.Add(MenuItem("查看识别文字", MenuGlyph.Text, () => ToggleRowPreview(row), "Space"));
				tools.Add(MenuItem("复制识别文字", MenuGlyph.Copy, () => _host.CopyRecognizedTextAsync(row)));
				tools.Add(MenuItem("提取为代码块", MenuGlyph.Code, () => CopyAsCodeBlockAsync(row)));
				tools.Add(MenuItem("重新识别文字", MenuGlyph.Refresh, () => TriggerOcrAsync(row, true)));
			}
			else if (!string.IsNullOrEmpty(row.ImagePath))
			{
				tools.Add(MenuItem("提取文字 (OCR)", MenuGlyph.Text, () => TriggerOcrAsync(row)));
			}
		}
		else
		{
			tools.Add(MenuItem("查看全文", MenuGlyph.Document, () => ToggleRowPreview(row), "Space"));
			tools.Add(MenuItem("复制为代码块", MenuGlyph.Code, () => CopyTextAsCodeBlockAsync(row)));
		}

		if (row.Kind == ClipKind.File)
		{
			tools.Add(MenuItem("复制路径", MenuGlyph.Folder, () => _host.CopyPathAsync(row)));
		}

		AppendGroup(menuFlyout, tools);

		// ③ 输出到桌面 / 交给视觉模型
		var extras = new List<MenuFlyoutItem>();
		if (row.IsImage && !string.IsNullOrEmpty(row.ImagePath))
		{
			extras.Add(MenuItem("钉在桌面", MenuGlyph.Display, () => _host.PinFile(row.ImagePath)));
			extras.Add(MenuItem("AI 视觉分析", MenuGlyph.Robot, () => AnalyzeWithAiAsync(row)));
		}
		else
		{
			string noteText = _host.ReadText(row.Id);
			if (string.IsNullOrWhiteSpace(noteText))
			{
				noteText = row.Preview;
			}
			if (!string.IsNullOrEmpty(noteText))
			{
				extras.Add(MenuItem("钉在桌面为便签", MenuGlyph.Display, () => _host.PinText(noteText)));
			}
		}

		AppendGroup(menuFlyout, extras);

		// ④ 状态与危险操作：固定/删除永远在最后，删除标红
		menuFlyout.Items.Add(new MenuFlyoutSeparator());
		menuFlyout.Items.Add(MenuItem(row.Pinned ? "取消固定" : "固定", MenuGlyph.Pin, () => TogglePin(row)));
		MenuFlyoutItem deleteItem = MenuItem("删除", MenuGlyph.Delete, () => DeleteRow(row), "Del");
		if (CriticalBrush() is { } critical)
		{
			deleteItem.Foreground = critical;
			if (deleteItem.Icon is FontIcon criticalIcon)
			{
				criticalIcon.Foreground = critical;
			}
		}
		menuFlyout.Items.Add(deleteItem);
		menuFlyout.ShowAt(frameworkElement, e.GetPosition(frameworkElement));
	}

	/// <summary>一组菜单项之间插分隔线，空组直接跳过。</summary>
	private static void AppendGroup(MenuFlyout flyout, List<MenuFlyoutItem> items)
	{
		if (items.Count == 0)
		{
			return;
		}

		if (flyout.Items.Count > 0)
		{
			flyout.Items.Add(new MenuFlyoutSeparator());
		}

		foreach (MenuFlyoutItem item in items)
		{
			flyout.Items.Add(item);
		}
	}

	/// <summary>菜单项统一带图标，并把窗口里真实可用的快捷键写在右侧。</summary>
	private static MenuFlyoutItem MenuItem(string text, string glyph, Action action, string? accelerator = null)
	{
		MenuFlyoutItem item = new MenuFlyoutItem
		{
			Text = text,
			Icon = new FontIcon { Glyph = glyph, FontSize = 14 },
		};
		if (!string.IsNullOrEmpty(accelerator))
		{
			item.KeyboardAcceleratorTextOverride = accelerator;
		}

		item.Click += (object _, RoutedEventArgs _) =>
		{
			action();
		};
		return item;
	}

	/// <summary>删除项用的系统告警色，缺资源时返回 null（保持默认前景色）。</summary>
	private static Brush? CriticalBrush()
	{
		return Application.Current.Resources.TryGetValue("SystemFillColorCriticalBrush", out object? value)
			&& value is Brush brush
			? brush
			: null;
	}

	private static class MenuGlyph
	{
		public const string Paste = "\uE77F";
		public const string Copy = "\uE8C8";
		public const string PlainText = "\uE8E9";
		public const string Document = "\uE8A5";
		public const string Text = "\uE8FD";
		public const string Code = "\uE943";
		public const string Refresh = "\uE117";
		public const string Robot = "\uE99A";
		public const string Display = "\uE7F4";
		public const string Pin = "\uE840";
		public const string Folder = "\uE8B7";
		public const string Delete = "\uE74D";
	}

	private async Task CopyAsCodeBlockAsync(ClipRow row)
	{
		string rawText = row.RecognizedText ?? _host.ReadText(row.Id);
		if (string.IsNullOrWhiteSpace(rawText) && !string.IsNullOrEmpty(row.ImagePath))
		{
			try
			{
				rawText = await OcrService.ReadAsync(await File.ReadAllBytesAsync(row.ImagePath));
			}
			catch (Exception exception)
			{
				Log.Error(exception);
			}
		}
		if (!string.IsNullOrWhiteSpace(rawText))
		{
			CodeExtractResult codeExtractResult = CodeExtractService.Process(rawText);
			_host.CopyCodeMarkdown(codeExtractResult.Markdown, codeExtractResult.Language);
		}
	}

	private Task CopyTextAsCodeBlockAsync(ClipRow row)
	{
		string text = _host.ReadText(row.Id);
		if (string.IsNullOrWhiteSpace(text))
		{
			text = row.Preview;
		}
		if (!string.IsNullOrWhiteSpace(text))
		{
			CodeExtractResult codeExtractResult = CodeExtractService.Process(text);
			_host.CopyCodeMarkdown(codeExtractResult.Markdown, codeExtractResult.Language);
		}
		return Task.CompletedTask;
	}

	private async Task AnalyzeWithAiAsync(ClipRow row)
	{
		if (string.IsNullOrEmpty(row.ImagePath) || !File.Exists(row.ImagePath))
		{
			return;
		}
		try
		{
			byte[] png = await File.ReadAllBytesAsync(row.ImagePath);
			_host.OpenAiVision(png);
		}
		catch (Exception exception)
		{
			Log.Error(exception);
		}
	}

	private async Task TriggerOcrAsync(ClipRow row, bool force = false)
	{
		if (string.IsNullOrEmpty(row.ImagePath) || !File.Exists(row.ImagePath))
		{
			return;
		}
		try
		{
			byte[] png = await File.ReadAllBytesAsync(row.ImagePath);
			_host.RecognizeImage(row.Id, png, force);
		}
		catch (Exception exception)
		{
			Log.Error(exception);
		}
	}

	private static bool IsInsideButton(DependencyObject? source)
	{
		while (source != null)
		{
			if (source is Button)
			{
				return true;
			}
			source = VisualTreeHelper.GetParent(source);
		}
		return false;
	}

	private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
	{
		// 焦点在搜索框时，Delete / Ctrl+C 会被 TextBox 自己先吃掉（哪怕框里是空的），
		// 用方向键高亮了一行却删不掉、复制不了。这里在隧道路由阶段先接一手；
		// 框里有文字时照常交给 TextBox 删字/复制文本。
		if (SearchBox.FocusState == FocusState.Unfocused || SearchBox.Text.Length > 0)
		{
			return;
		}

		if (e.Key == VirtualKey.Delete)
		{
			if (ItemList.SelectedItem is ClipRow row)
			{
				DeleteRow(row);
				e.Handled = true;
			}
		}
		else if (e.Key == VirtualKey.C && IsCtrlDown())
		{
			if (ItemList.SelectedItem is ClipRow selected)
			{
				_host.CopyAsync(selected);
				e.Handled = true;
			}
		}
	}

	private void OnKeyDown(object sender, KeyRoutedEventArgs e)
	{
		bool flag = SearchBox.FocusState != FocusState.Unfocused;
		// 方向键选完行后焦点仍在搜索框，所以 Del / Ctrl+C / Space 不能在“搜索框有焦点”
		// 时一律拦掉，否则列表里已经高亮了一行却删不掉、复制不了、看不清全文。
		// 只有搜索框里确实有文字时才让给输入框自己处理。
		bool listShortcutAllowed = !flag || SearchBox.Text.Length == 0;
		if (e.Key == VirtualKey.Escape)
		{
			if (_previewExplicitlyOpened || _previewOpen)
			{
				_previewExplicitlyOpened = false;
				HidePreview();
				e.Handled = true;
			}
			else
			{
				Dismiss();
				e.Handled = true;
			}
			return;
		}
		if (e.Key == VirtualKey.Space && listShortcutAllowed)
		{
			if (ItemList.SelectedItem is ClipRow row)
			{
				ToggleRowPreview(row);
				e.Handled = true;
			}
			return;
		}
		VirtualKey key = e.Key;
		if ((key == VirtualKey.Up || key == VirtualKey.Down) ? true : false)
		{
			MoveSelection((e.Key == VirtualKey.Down) ? 1 : (-1));
			e.Handled = true;
		}
		else if (e.Key == VirtualKey.Delete && listShortcutAllowed)
		{
			if (ItemList.SelectedItem is ClipRow row2)
			{
				DeleteRow(row2);
			}
			e.Handled = true;
		}
		else if (e.Key == VirtualKey.C && IsCtrlDown() && listShortcutAllowed)
		{
			if (ItemList.SelectedItem is ClipRow row3)
			{
				_host.CopyAsync(row3);
			}
			e.Handled = true;
		}
		else
		{
			if (e.Key != VirtualKey.Enter)
			{
				return;
			}
			ClipRow clipRow = (ItemList.SelectedItem as ClipRow) ?? _rows.FirstOrDefault();
			if (clipRow != null)
			{
				if (!IsShiftDown())
				{
					_host.PasteAsync(clipRow);
				}
				else
				{
					_host.PastePlainAsync(clipRow);
				}
			}
			e.Handled = true;
		}
	}

	private void OnPreviewKey(object sender, KeyRoutedEventArgs e)
	{
		bool flag = IsCtrlDown();
		bool flag2 = (NativeMethods.GetKeyState(18) & 0x8000) != 0;
		bool flag3 = !(FocusManager.GetFocusedElement(Root.XamlRoot) is TextBox) && !flag && !flag2;
		if ((flag | flag2 | flag3) && TryShortcutIndex(e.Key, out var index) && index < _rows.Count)
		{
			e.Handled = true;
			_host.PasteAsync(_rows[index]);
		}
	}

	private static bool TryShortcutIndex(VirtualKey key, out int index)
	{
		int num;
		switch (key)
		{
		case VirtualKey.Number1:
		case VirtualKey.NumberPad1:
			num = 0;
			break;
		case VirtualKey.Number2:
		case VirtualKey.NumberPad2:
			num = 1;
			break;
		case VirtualKey.Number3:
		case VirtualKey.NumberPad3:
			num = 2;
			break;
		case VirtualKey.Number4:
		case VirtualKey.NumberPad4:
			num = 3;
			break;
		case VirtualKey.Number5:
		case VirtualKey.NumberPad5:
			num = 4;
			break;
		case VirtualKey.Number6:
		case VirtualKey.NumberPad6:
			num = 5;
			break;
		case VirtualKey.Number7:
		case VirtualKey.NumberPad7:
			num = 6;
			break;
		case VirtualKey.Number8:
		case VirtualKey.NumberPad8:
			num = 7;
			break;
		case VirtualKey.Number9:
		case VirtualKey.NumberPad9:
			num = 8;
			break;
		default:
			num = -1;
			break;
		}
		index = num;
		return index >= 0;
	}

	private void MoveSelection(int delta)
	{
		if (_rows.Count != 0)
		{
			int selectedIndex = ItemList.SelectedIndex;
			selectedIndex = ((selectedIndex >= 0) ? Math.Clamp(selectedIndex + delta, 0, _rows.Count - 1) : ((delta <= 0) ? (_rows.Count - 1) : 0));
			ClipRow item = _rows[selectedIndex];
			ItemList.SelectedIndex = selectedIndex;
			ItemList.ScrollIntoView(item);
		}
	}

	private static bool IsCtrlDown()
	{
		return InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
	}

	private static bool IsShiftDown()
	{
		return InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
	}

	private void OnContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
	{
		if (args.InRecycleQueue || args.Item is not ClipRow clipRow)
		{
			return;
		}
		clipRow.EnsureThumbnailAsync();
		// 容器回收后会带着上一行的悬停状态，重新绑定时先归零
		if (args.Phase == 0 && args.ItemContainer is FrameworkElement container)
		{
			FrameworkElement? panel = FindByTag(container, RowActionsTag);
			if (panel != null)
			{
				Motion.Snap(panel, ReferenceEquals(args.Item, ItemList.SelectedItem) ? 1 : 0);
			}
		}
		if (args.Phase == 0 && _entranceBudget > 0)
		{
			_entranceBudget--;
			Motion.RowIn(args.ItemContainer, 14 - _entranceBudget);
		}
	}

}