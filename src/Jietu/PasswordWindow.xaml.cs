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
    // 行尾语义小图标：x:Bind 不做 bool→Visibility 的隐式转换，这里直接暴露可见性。
    public Visibility TotpBadgeVisibility { get; init; } = Visibility.Collapsed;
    public Visibility NoteBadgeVisibility { get; init; } = Visibility.Collapsed;
}

/// <summary>
/// 密码库主窗口：锁定态（内嵌解锁/创建视图）与解锁态（列表 + 编辑器）切换。
/// 安全约束：窗口防截屏（WDA_EXCLUDEFROMCAPTURE，开发期经 ScreenshotProtectionEnabled
/// 临时关闭）；前台时 SensitiveForeground 暂停截图与剪贴板记录；复制走 SetTextSecret
/// （不进任何剪贴板历史 + 定时清除）。
/// </summary>
public sealed partial class PasswordWindow : Window
{
    // 开发期临时关闭防截屏：true 时窗口对系统级截图不可见、且自家截图功能在前台时暂停。
    // 发布前必须改回 true 并恢复 const（编译期常量可让分支整体消除）。
    private static readonly bool ScreenshotProtectionEnabled = false;

    private readonly AppHost _host;
    private readonly VaultSession _vault;
    private bool _open;
    private string? _selectedId;
    // 正在编辑的条目是否已存在于库中（决定保存是更新还是插入）。
    private bool _editingExisting;
    // 编辑器脏标记：LoadEditor/ClearEditor/显示切换期间置 _editorLoading 抑制误报，
    // 「未保存」徽标的可见性即脏状态本身。
    private bool _editorLoading;
    // TOTP 验证码 1 秒刷新计时器。
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _totpTimer;
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

    // 列表过滤/排序状态：只影响展示，不写回库文件。
    private VaultFilter _filter = VaultFilter.All;
    private VaultSort _sort = VaultSort.Updated;
    // 右侧详情区是否有内容（编辑器或新建立稿）：与生成器面板一起决定详情列的显隐。
    private bool _editorVisible;
    // 最近一次刷新后列表里的可见行数（0 且库非空 = 被搜索/筛选筛空）。
    private int _visibleRowCount;
    // 分段控件滑块当前所在位置（DIP），供弹簧动画从当前位置起跳。
    private double _thumbX;

    private enum VaultFilter { All, Login, Note, Totp }

    private enum VaultSort { Updated, Title }

    public PasswordWindow(AppHost host)
    {
        _host = host;
        _vault = host.Vault;
        InitializeComponent();
        _xamlReady = true;
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
            if (_vault.IsUnlocked)
            {
                UpdateFilterChips(animate: false);
            }
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
        StopTotpTimer();
        // 编辑器里的明文不能留在控件里跨「关闭」存续：重置整个编辑区。
        ClearEditor();
    }

    // ---------------- 状态切换 ----------------


    // ---------------- 创建态实时反馈：强度 / 要求清单 / 两次一致 ----------------


    // ---------------- 解锁 / 创建 / Hello ----------------


    /// <summary>解锁按钮的进行态：文案换掉 + 禁用，让 Argon2 那一两秒有明确反馈。</summary>

    /// <summary>Hello 磁贴的进行态：换文案 + 转圈 + 收起箭头，不把按钮整块置灰。</summary>


    /// <summary>软提示：用户取消 Hello、切回主密码这类「不是错」的事，用灰字而不是红条。</summary>


    // ---------------- 输入框焦点 / Caps Lock ----------------

    /// <summary>输入框聚焦时外壳描边换成品牌色；顺带刷新 Caps Lock 提示。</summary>


    /// <summary>主密码最怕「打了半天其实是大写」：Caps Lock 亮着就直说。</summary>


    /// <summary>Hello 失败分情况说话：取消是正常操作，凭据丢失要指路，真坏了才用红条。</summary>

    /// <summary>启用快速解锁（设置页调用）：包一层 Hello 副本并落盘。</summary>


    // ---------------- 导入 / 导出 / 备份 / 改主密码 ----------------


    /// <summary>从加密备份恢复：与 CSV 导入相同的合并去重策略，不覆盖现有条目。</summary>


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

    // ---------------- 列表 ----------------


    /// <summary>按当前筛选 + 搜索 + 排序产出条目序列（只读，不改库）。</summary>


    /// <summary>
    /// 列表副标题：笔记只标类型，登录条目拼「用户名 · 域名」。
    /// 网址只留主机名（去掉协议与路径），一行内能塞下更多信息。
    /// </summary>


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

    /// <summary>标题 → (首字母, 淡底, 实色)：列表行与详情头共用，保证同一条目配色一致。</summary>


    /// <summary>搜索框内 Esc：先清空关键词，再按一次才交给窗口（不抢占对话框的 Esc）。</summary>


    /// <summary>无结果时的退路：清空搜索 + 回到「全部」筛选。</summary>

    // ---------------- 分段筛选 / 排序 ----------------


    /// <summary>
    /// 分段控件选中态：滑块弹簧平移到当前段 + 主文字色 / 半粗体。
    /// 与 HistoryWindow 的 UpdateFilterChips 同一套观感（不染色，只靠滑块与字重）。
    /// </summary>

    /// <summary>把滑块滑到当前筛选位置（首帧直接落位，避免打开窗口时飞入）。</summary>


    /// <summary>有输入才显示清除按钮；与剪贴板历史一致，搜索胶囊里不再塞 Ctrl+F 键帽。</summary>


    /// <summary>
    /// 内容区显隐的唯一出口（列表卡片 / 空态 / 详情 / 引导占位）：
    /// 编辑器与生成器任一打开就算「详情区已展开」，空库此时让位给表单而不是压一层欢迎页。
    /// </summary>


    // ---------------- 编辑器 ----------------

    private bool IsNoteKind => KindCombo.SelectedIndex == 1;

    /// <summary>类型切换：安全笔记没有用户名/密码/网址/TOTP，收起凭据字段避免误填。</summary>


    /// <summary>
    /// 编辑器任一字段变化：展示「未保存」徽标。
    /// 光靠 _editorLoading 挡不住 PasswordBox 这类延后派发的变更事件（装载完才回调，
    /// 于是刚点开一条就冒出「未保存」），所以这里再跟装载时的快照比对一次：
    /// 内容和装载时一致就不算改过。
    /// </summary>


    /// <summary>编辑器当前内容的指纹：用于判断「真的改过」而不是「事件来过」。</summary>


    // ---------------- 密码显示 / 复制 ----------------


    /// <summary>当前编辑中的密码：显示态明文在 TextBox（可能被改过），否则取 PasswordBox。</summary>
    private string CurrentPassword => PasswordBox.Visibility == Visibility.Visible
        ? PasswordBox.Password
        : PasswordTextBox.Text;


    // ---------------- 键盘快捷键 ----------------


    // ---------------- 生成器 ----------------


    /// <summary>选项变化（长度/词数/短语/字符类开关）时立即重生成，所见即所得。</summary>


    // ---------------- TOTP ----------------


}
