namespace Jietu;

/// <summary>载荷格式版本常量。与容器的 formatVersion 相互独立：容器管字节布局，载荷管字段集合。</summary>
public static class VaultPayloadSchema
{
    /// <summary>
    /// 当前载荷版本。回收站 / 历史 / 体检忽略 / 关键账号标记全部在 v2 一次预留到位，
    /// 避免上线这些功能时被迫升 v3（升版 = 二次破坏用户数据）。
    /// </summary>
    public const int CurrentVersion = 2;
}

/// <summary>
/// 密码库条目。Kind 目前只有 login（登录凭据）与 note（安全笔记）。
///
/// 活跃条目与回收站条目共用同一个形状：只在哪个数组里、以及 <see cref="DeletedUtc"/>
/// 是否为空上有区别。复用形状是为了"恢复/删除"只是移动对象，不需要字段搬运。
/// </summary>
public sealed class VaultEntry
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "login";
    public string Title { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Url { get; set; } = "";
    public string Notes { get; set; } = "";
    public List<string> Tags { get; set; } = [];

    /// <summary>Base32 编码的 TOTP 密钥；空串表示没有验证器。</summary>
    public string TotpSecret { get; set; } = "";

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// 删除时刻。null = 活跃条目；非 null = 位于回收站。
    /// 不变式由 <see cref="VaultData.Normalize"/> 保证：Entries 里恒为 null，RecycleBin 里恒非 null。
    /// </summary>
    public DateTimeOffset? DeletedUtc { get; set; }

    /// <summary>
    /// 关键账号标记。true = 用户手动标记为关键；false = 手动排除；null = 由域名表自动判定。
    /// A 批只预留字段，体检服务在 C 批实现。
    /// </summary>
    public bool? CriticalOverride { get; set; }

    /// <summary>最近若干个内容版本，<c>[0]</c> 为最老。A 批只预留字段，历史服务在 C 批实现。</summary>
    public List<VaultEntryVersion> History { get; set; } = [];

    /// <summary>
    /// 为后台保存克隆一份结构化副本：只复制容器，<see cref="string"/> 仍共享引用。
    ///
    /// 为什么不做深拷贝字符串：.NET 的 string 不可变，共享引用即安全，且**不会产生额外的明文副本**
    /// （对"密钥/明文尽量少留"的目标是加分项，不是妥协）。可变容器必须新分配，
    /// 否则 UI 在后台序列化期间继续编辑会把快照改脏（撕裂快照）。
    /// </summary>
    internal VaultEntry CloneForBackground()
    {
        var copy = (VaultEntry)MemberwiseClone();
        copy.Tags = [.. Tags];
        copy.History = History.Select(version => version.CloneForBackground()).ToList();
        return copy;
    }
}

/// <summary>
/// 条目历史的一个版本：被覆盖之前的完整内容快照。
/// 存完整值而不是 diff：查看/回滚需要完整内容，且口令的"差量"没有意义（改一个字符就是全新口令）。
/// </summary>
public sealed class VaultEntryVersion
{
    /// <summary>该版本所代表内容的产生时刻（= 被覆盖前的 UpdatedUtc）。</summary>
    public DateTimeOffset ChangedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>与下一版相比改动的字段名（title/username/password/url/notes/tags/totpSecret）。</summary>
    public List<string> ChangedFields { get; set; } = [];

    public string Title { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Url { get; set; } = "";
    public string Notes { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public string TotpSecret { get; set; } = "";

    internal VaultEntryVersion CloneForBackground()
    {
        var copy = (VaultEntryVersion)MemberwiseClone();
        copy.ChangedFields = [.. ChangedFields];
        copy.Tags = [.. Tags];
        return copy;
    }
}

/// <summary>
/// 体检忽略项：用户说"这条问题我知道，别再提醒"。
/// <see cref="GroupKey"/> 只用于"重复密码"的组级忽略，取值是条目 id 列表的 SHA-256，
/// **不含任何口令派生材料**（否则等于把口令的指纹写进库文件）。
/// </summary>
public sealed class VaultHealthIgnore
{
    public string EntryId { get; set; } = "";

    /// <summary>weak / reused / stale / missingTotp。</summary>
    public string Dimension { get; set; } = "";

    public string? GroupKey { get; set; }

    public DateTimeOffset IgnoredUtc { get; set; } = DateTimeOffset.UtcNow;

    internal VaultHealthIgnore CloneForBackground() => (VaultHealthIgnore)MemberwiseClone();
}

/// <summary>
/// 已关联的浏览器扩展：持有关联时扩展出示的随机令牌的 SHA-256（十六进制）。
/// 后续每条请求都带令牌原文，桌面端比对哈希。用令牌而非非对称密钥：本机命名
/// 管道已限制同一用户，令牌哈希足够，且 MV3 的 WebCrypto Ed25519 兼容性不稳。
/// </summary>
public sealed class VaultTrustedBrowser
{
    public string Id { get; set; } = "";

    /// <summary>浏览器名 + 扩展名的展示文本，确认弹窗里给用户看。</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>关联令牌的 SHA-256（小写十六进制），令牌原文永不出现在桌面端。</summary>
    public string TokenHash { get; set; } = "";

    public DateTimeOffset AddedUtc { get; set; } = DateTimeOffset.UtcNow;

    internal VaultTrustedBrowser CloneForBackground() => (VaultTrustedBrowser)MemberwiseClone();
}

/// <summary>
/// 解密后的库明文载荷。只在内存中存在，锁定时随密钥一起清场。
///
/// 线程纪律：本类及其所有字段**只允许 UI 线程读写**。后台需要数据时走
/// VaultSession 的编组方法拿不可变副本；直接把 <see cref="Entries"/> 交给线程池遍历
/// 会与编辑器并发写撕裂（表现为莫名丢条目或 InvalidOperationException）。
/// </summary>
public sealed class VaultData
{
    /// <summary>载荷版本。&gt; 2 必须拒绝打开（旧版本回写会静默丢字段）。</summary>
    public int SchemaVersion { get; set; } = VaultPayloadSchema.CurrentVersion;

    /// <summary>活跃条目。</summary>
    public List<VaultEntry> Entries { get; set; } = [];

    /// <summary>
    /// 回收站条目。独立成数组（而不是用 DeletedUtc 过滤 Entries）是刻意的：
    /// 现有代码全部遍历 <see cref="Entries"/>，列表/搜索/体检/扩展填充/CSV 导出
    /// 不可能因为漏改某一处就把已删条目泄漏出去——范围隔离在类型层面成立。
    /// </summary>
    public List<VaultEntry> RecycleBin { get; set; } = [];

    public List<VaultTrustedBrowser> TrustedBrowsers { get; set; } = [];

    /// <summary>体检忽略项（C 批使用，A 批只预留字段与兜底）。</summary>
    public List<VaultHealthIgnore> HealthIgnores { get; set; } = [];

    /// <summary>复制密码后自动清空剪贴板的秒数；0 表示不清空。</summary>
    public int ClipboardClearSeconds { get; set; } = VaultLimits.DefaultClipboardClearSeconds;

    /// <summary>
    /// UI 线程调用：为后台保存/改密克隆一份结构化副本。之后对源对象的就地修改不影响副本。
    /// 只复制容器与条目对象，字符串共享引用（不可变 ⇒ 安全且不产生额外明文副本）。
    /// </summary>
    public VaultData CloneForBackground()
    {
        var copy = (VaultData)MemberwiseClone();
        copy.Entries = Entries.Select(entry => entry.CloneForBackground()).ToList();
        copy.RecycleBin = RecycleBin.Select(entry => entry.CloneForBackground()).ToList();
        copy.TrustedBrowsers = TrustedBrowsers
            .Select(browser => browser.CloneForBackground())
            .ToList();
        copy.HealthIgnores = HealthIgnores
            .Select(ignore => ignore.CloneForBackground())
            .ToList();
        return copy;
    }

    /// <summary>
    /// 修复载荷不变式。容错、绝不抛：这里接到的可能是别人（或旧版本）写的半成品 JSON。
    ///
    /// 修的四条：
    /// ① 活跃条目的 DeletedUtc 恒为 null；② 回收站条目的 DeletedUtc 恒非 null（缺失补 now，
    ///    否则会出现"永久留在回收站且永不过期"的幽灵条目）；
    /// ③ 两个列表按 Id 去重，跨列表重复时以 Entries 优先（活跃的就是活着的）；
    /// ④ 清理孤立数据：引用不存在条目的历史版本/体检忽略项、超上限的历史版本。
    /// </summary>
    public void Normalize()
    {
        if (SchemaVersion <= 0)
        {
            // 缺字段/写 0 只可能来自我们自己早期的备份或手工改坏的文件：按当前版本容错读。
            SchemaVersion = VaultPayloadSchema.CurrentVersion;
        }

        Entries ??= [];
        RecycleBin ??= [];
        TrustedBrowsers ??= [];
        HealthIgnores ??= [];
        ClipboardClearSeconds = ClipboardClearSeconds is < 0 or > VaultLimits.MaxClipboardClearSeconds
            ? VaultLimits.DefaultClipboardClearSeconds
            : ClipboardClearSeconds;

        // ① 活跃条目不得带删除时间；顺手把可空集合补齐（"entries":[{ "tags": null }] 会 NRE）。
        foreach (var entry in Entries)
        {
            entry.DeletedUtc = null;
            NormalizeEntry(entry);
        }

        // ③-a 同列表内重复 id：保留首个，其余重新生成 id。
        // 为什么不是直接丢弃：重复 id 通常来自"从两份备份合并"，丢弃等于静默删掉用户的条目。
        var activeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Entries)
        {
            if (string.IsNullOrEmpty(entry.Id))
            {
                entry.Id = NewEntryId();
            }

            if (!activeIds.Add(entry.Id))
            {
                entry.Id = NewEntryId();
                activeIds.Add(entry.Id);
            }
        }

        // ② + ③-b 回收站：补删除时间；跨列表以 Entries 优先（重复的直接丢，绝不抢活跃条目的 id）。
        var seenIds = new HashSet<string>(activeIds, StringComparer.Ordinal);
        var keptTrash = new List<VaultEntry>(RecycleBin.Count);
        foreach (var entry in RecycleBin)
        {
            if (string.IsNullOrEmpty(entry.Id))
            {
                entry.Id = NewEntryId();
            }

            if (!seenIds.Add(entry.Id))
            {
                continue;
            }

            entry.DeletedUtc ??= DateTimeOffset.UtcNow;
            NormalizeEntry(entry);
            keptTrash.Add(entry);
        }

        RecycleBin = keptTrash;

        // ④ 孤立体检忽略项：条目已不存在（或维度拼错）就丢掉，否则"忽略"会永远挂着一团空气。
        var livingIds = new HashSet<string>(seenIds, StringComparer.Ordinal);
        var keptIgnores = new List<VaultHealthIgnore>(HealthIgnores.Count);
        var ignoreKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ignore in HealthIgnores)
        {
            if (ignore == null || string.IsNullOrEmpty(ignore.EntryId) || !livingIds.Contains(ignore.EntryId))
            {
                continue;
            }

            if (!IsKnownDimension(ignore.Dimension))
            {
                continue;
            }

            var key = ignore.EntryId + "\u0000" + ignore.Dimension + "\u0000" + (ignore.GroupKey ?? "");
            if (!ignoreKeys.Add(key))
            {
                continue;
            }

            ignore.GroupKey = string.IsNullOrEmpty(ignore.GroupKey) ? null : ignore.GroupKey;
            keptIgnores.Add(ignore);
        }

        HealthIgnores = keptIgnores;

        foreach (var browser in TrustedBrowsers)
        {
            if (browser == null)
            {
                continue;
            }

            browser.Id = string.IsNullOrEmpty(browser.Id) ? Guid.NewGuid().ToString("N") : browser.Id;
        }

        TrustedBrowsers.RemoveAll(b => b == null);
    }

    /// <summary>单条目的兜底：可空集合补齐、历史裁剪、超长字段截断。</summary>
    private static void NormalizeEntry(VaultEntry entry)
    {
        if (entry == null)
        {
            return;
        }

        entry.Tags ??= [];
        entry.History ??= [];

        // 孤立历史：null 版本（JSON 里写了 null），以及所有内容字段都为空的空壳版本
        // （只可能来自被外部改坏的文件；留着会让"历史"里出现一行空白）。
        entry.History.RemoveAll(v => v == null
            || (string.IsNullOrEmpty(v.Title)
                && string.IsNullOrEmpty(v.Username)
                && string.IsNullOrEmpty(v.Password)
                && string.IsNullOrEmpty(v.Url)
                && string.IsNullOrEmpty(v.Notes)
                && string.IsNullOrEmpty(v.TotpSecret)
                && v.ChangedFields is not { Count: > 0 }));
        foreach (var version in entry.History)
        {
            version.ChangedFields ??= [];
            version.Tags ??= [];
            version.Title = Truncate(version.Title);
            version.Username = Truncate(version.Username);
            version.Password = Truncate(version.Password);
            version.Url = Truncate(version.Url);
            version.Notes = Truncate(version.Notes);
            version.TotpSecret = Truncate(version.TotpSecret);
        }

        // 历史超过上限：FIFO 丢最老（[0] 是最老）。
        while (entry.History.Count > VaultLimits.HistoryVersions)
        {
            entry.History.RemoveAt(0);
        }
    }

    private static string Truncate(string value)
        => value is null ? "" : value.Length <= VaultLimits.MaxHistoryFieldChars ? value : value[..VaultLimits.MaxHistoryFieldChars];

    private static bool IsKnownDimension(string dimension)
        => dimension is "weak" or "reused" or "stale" or "missingTotp";

    private static string NewEntryId() => Guid.NewGuid().ToString("N");
}

/// <summary>新建库时生成的初始载荷。</summary>
public static class VaultDefaults
{
    public static VaultData NewData() => new();
}
