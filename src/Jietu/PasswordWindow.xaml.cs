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

/// <summary>列表行视图模型：只暴露展示字段，不携带密码明文。</summary>
public sealed class VaultRow
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    // 列表彩色头像：按标题哈希取色的首字母。
    public string Initial { get; init; } = "";
    public Brush AvatarBrush { get; init; } = new SolidColorBrush(Colors.Transparent);
    public Brush AvatarFgBrush { get; init; } = new SolidColorBrush(Colors.Transparent);
}

/// <summary>
/// 密码库主窗口：锁定态（内嵌解锁/创建视图）与解锁态（列表 + 编辑器）切换。
/// 安全约束：窗口防截屏（WDA_EXCLUDEFROMCAPTURE，开发期经 ScreenshotProtectionEnabled
/// 临时关闭）；前台时 SensitiveForeground 暂停截图与剪贴板记录；复制走 SetTextSecret
/// （不进任何剪贴板历史 + 定时清除）。
/// </summary>
public sealed partial class PasswordWindow : Window
{
    // 防截屏：true 时窗口对系统级截图不可见（WDA_EXCLUDEFROMCAPTURE），
    // 且自家截图功能在密码库前台时暂停。const 让"未启用"分支在编译期整体消除。
    private const bool ScreenshotProtectionEnabled = true;

    private readonly AppHost _host;
    private readonly VaultSession _vault;
    private bool _open;
    private string? _selectedId;
    // 正在编辑的条目是否已存在于库中（决定保存是更新还是插入）。
    private bool _editingExisting;
    // 编辑器脏标记：LoadEditor/ClearEditor/显示切换期间置 _editorLoading 抑制误报，
    // 「未保存」徽标的可见性即脏状态本身。
    private bool _editorLoading;
    // 详情态密码显示开关：切换条目/进入编辑时复位为隐藏。
    private bool _detailRevealed;
    // XAML 解析期间 ValueChanged/Checked/SelectionChanged 就会触发（NumberBox 的 Value、
    // CheckBox 的 IsChecked、ComboBox 的 SelectedIndex 都是解析期赋值），
    // 此时后面的命名元素还不存在——所有这类处理器必须先看本标志。
    private bool _xamlReady;

    // 解锁动作的互斥与状态：Argon2id 派生要跑几百毫秒到两秒，期间不能让人连按 Enter 起多份。
    private bool _unlocking;
    private bool _helloBusy;
    // 输入框未聚焦时的描边（主题资源，切换主题时重取），聚焦时换成品牌色。
    private Brush? _fieldIdleStroke;
    // 编辑器「干净」时的内容指纹：程序化装载会延后派发变更事件，靠它区分真改与假改。
    private string _cleanSignature = "";

    // 列表排序状态：只影响展示，不写回库文件。
    private VaultSort _sort = VaultSort.Updated;
    // 右侧详情区是否有内容（编辑器或新建草稿）：决定详情列的显隐。
    private bool _editorVisible;
    // 最近一次刷新后列表里的可见行数（0 且库非空 = 被搜索筛空）。
    private int _visibleRowCount;

    private enum VaultSort { Updated, Title }

    public PasswordWindow(AppHost host)
    {
        _host = host;
        _vault = host.Vault;
        InitializeComponent();
        _xamlReady = true;
        // WinUI 的 ScrollViewer 会把 Stretch+MaxWidth 的内容右对齐（普通 Grid 是居中），
        // 表单宽度在这里钉成 min(720, 视口) 并显式居中，和下方操作条永远同列同宽。
        // 720 是双栏表单的舒适上限：空库时两栏各约 350，有列表列时各约 315。
        DetailPane.SizeChanged += (_, e) =>
        {
            var width = Math.Min(720, Math.Max(160, e.NewSize.Width - 2));
            DetailRoot.Width = width;
            EditorActionBar.MaxWidth = width;
        };
        _fieldIdleStroke = MasterFieldShell.BorderBrush;
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
                if (ScreenshotProtectionEnabled)
                {
                    SensitiveForeground.SetVaultWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
                }
                _vault.TouchActivity();
            }
        };

        // 空闲/锁屏/睡眠/托盘/桥都会触发 Locked（全部在 UI 线程回调）：
        // 立即切回解锁视图并清空编辑器，明文不能在锁定后还留在界面上。
        _vault.Locked += () => DispatcherQueue.TryEnqueue(RefreshState);

        // 主题切换后分段控件的文字色要跟着换（选中的主/次文字色是主题资源，重取一次即可）。
        Root.ActualThemeChanged += (_, _) =>
        {
            if (!_xamlReady)
            {
                return;
            }

            // 输入框描边也是主题资源：重新取一份空闲态的颜色，别让上一套主题的灰边留着。
            _fieldIdleStroke = MasterFieldShell.BorderBrush = (Brush)Application.Current.Resources["HistFieldStrokeBrush"];
        };
    }

    public bool IsOpen => _open;

    // 锁定态是一张窄卡片（解锁只需一条密码），解锁后才是「列表 + 详情」的大窗口。
    // 尺寸跟着状态走，避免解锁页在 980×660 的窗口里飘着一小坨内容。
    private const int LockedWidth = 468;
    private const int LockedHeight = 596;
    private const int UnlockedWidth = 980;
    private const int UnlockedHeight = 660;

    /// <summary>唤出窗口：未创建库时进创建流程，锁定时进解锁流程。</summary>
    public void Present()
    {
        RefreshState();
        if (!_open)
        {
            _open = true;
            AppWindow.IsShownInSwitchers = true;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var (width, height) = _vault.IsUnlocked
                ? (UnlockedWidth, UnlockedHeight)
                : (LockedWidth, LockedHeight);
            AppWindow.MoveAndResize(ScreenUtil.PlaceNearCursor(hwnd, width, height));
            // WDA_EXCLUDEFROMCAPTURE：BitBlt / DXGI / WGC 全部抓不到本窗口，
            // 同时也保证自家截图/贴图/OCR 功能不会把密码内容摄进去。
            if (ScreenshotProtectionEnabled)
            {
                NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WdaExcludeFromCapture);
            }

            Activate();
            // RefreshState 里的 Focus 发生在窗口还没激活时，会落空；
            // 激活之后再点一次，唤出即可直接打字（否则要先点一下输入框）。
            if (!_vault.IsUnlocked)
            {
                MasterBox.Focus(FocusState.Programmatic);
            }
        }
        else
        {
            Activate();
        }
    }

    /// <summary>锁定 / 解锁时把窗口换成对应尺寸：只改大小，左上角位置不动。</summary>
    private void ApplyWindowSize()
    {
        if (!_open)
        {
            return;
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var (width, height) = _vault.IsUnlocked
            ? (UnlockedWidth, UnlockedHeight)
            : (LockedWidth, LockedHeight);
        var position = AppWindow.Position;
        AppWindow.MoveAndResize(ScreenUtil.FitAt(hwnd, position.X, position.Y, width, height));
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
        // 编辑器里的明文不能留在控件里跨「关闭」存续：重置整个编辑区。
        ClearEditor();
    }

    /// <summary>
    /// 应用内确认 / 提示对话框（替代 Win32 MessageBox）：跟随窗口主题与明暗色。
    /// confirmText 为 null 时只有「知道了」一个按钮，用于纯提示；
    /// danger=true 时默认焦点落在「取消」上，回车不会误触发不可逆操作。
    /// </summary>
    private async Task<bool> ShowAppConfirmAsync(string title, string message, string? confirmText, bool danger = false)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5,
                LineHeight = 20,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            },
            XamlRoot = Content.XamlRoot,
        };
        if (confirmText == null)
        {
            dialog.CloseButtonText = "知道了";
            dialog.DefaultButton = ContentDialogButton.Close;
        }
        else
        {
            dialog.PrimaryButtonText = confirmText;
            dialog.CloseButtonText = "取消";
            dialog.DefaultButton = danger ? ContentDialogButton.Close : ContentDialogButton.Primary;
        }

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>窗口内模态对话框。窗口可能处于隐藏态，隐藏时退回 MessageBox 提示。</summary>
    private async Task<ContentDialogResult> ShowVaultDialog(
        string title, UIElement content, string primaryText, Func<string?>? validate = null)
    {
        // 校验失败时就地显示原因，别把对话框关掉让人重开重输
        var error = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["SnapFlowDangerBrush"],
        };
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(content);
        body.Children.Add(error);

        var dialog = new ContentDialog
        {
            Title = title,
            Content = body,
            PrimaryButtonText = primaryText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        if (validate != null)
        {
            dialog.PrimaryButtonClick += (_, args) =>
            {
                var message = validate();
                if (string.IsNullOrEmpty(message))
                {
                    return;
                }

                error.Text = message;
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
            };
        }

        return await dialog.ShowAsync();
    }

    // 列表头像调色板：按标题哈希取色，同一条目颜色稳定。
    private static readonly Color[] AvatarPalette =
    [
        Color.FromArgb(0xFF, 0x2D, 0x6E, 0xFA), // 品牌蓝
        Color.FromArgb(0xFF, 0x73, 0x41, 0xF5), // 紫
        Color.FromArgb(0xFF, 0x00, 0xB9, 0xFF), // 天蓝
        Color.FromArgb(0xFF, 0x10, 0xB9, 0x81), // 绿
        Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B), // 琥珀
        Color.FromArgb(0xFF, 0xEC, 0x48, 0x99), // 粉
        Color.FromArgb(0xFF, 0x8B, 0x5C, 0xF6), // 蓝紫
    ];

    /// <summary>当前编辑中的密码：显示态明文在 TextBox（可能被改过），否则取 PasswordBox。</summary>
    private string CurrentPassword => PasswordBox.Visibility == Visibility.Visible
        ? PasswordBox.Password
        : PasswordTextBox.Text;
}
