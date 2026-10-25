namespace Jietu;

/// <summary>
/// 所有面向用户的密码库文案的**单一来源**。XAML 与窗口代码不得硬编码同一句话的副本——
/// 否则改一次文案要满仓搜索，且必然漏掉一处。
///
/// 纪律：
/// ① 全部简体中文；② 纯字符串或用简单参数拼接（可单测）；③ 不含任何路径之外的敏感信息；
/// ④ 纯装饰性 UI 标签（"或使用主密码"这类）不强制进本类，但**所有错误/阻断/安全提示必须进**。
/// </summary>
public static class VaultMessages
{
    // ---------------- 解锁失败（三层：标题 / 正文 / 辅助） ----------------

    /// <summary>第一层：标题。刻意不写"密码错误"，因为 tag 失败也可能是文件被改。</summary>
    public const string UnlockFailedTitle = "无法解锁密码库";

    /// <summary>
    /// 第二层：正文。GCM 的 tag 失败在密码学上**无法**区分"密码错"与"文件被改"，
    /// 所以一句话同时覆盖两种可能，而不是骗用户"你密码错了"或吓用户"文件坏了"。
    /// </summary>
    public const string UnlockFailedBody = "主密码不正确，或这个密码库已损坏／被修改。";

    /// <summary>第三层：辅助。给出可执行的下一步，而不是让用户卡在这里。</summary>
    public const string UnlockFailedHint = "如果确认主密码无误，可从加密备份恢复。";

    // ---------------- 阻断卡（三种不可解锁的库） ----------------

    public const string BlockLegacyTitle = "无法打开这个密码库";

    public static string BlockLegacyBody(string path)
        => $"当前密码库由旧版本 SnapFlow 创建，本版本已升级为新的加密格式，无法直接打开。你的库文件完整保留在：{path}。"
           + "如需取回其中数据，请先安装 SnapFlow 1.1.4 导出 CSV，再回到本版本重新导入。我们不会删除或覆盖你的库文件。";

    public const string BlockLegacyHint = "建议先复制一份库文件再做任何操作。";

    public const string BlockIntegrityTitle = "密码库校验失败";

    public static string BlockIntegrityBody(string path)
        => $"文件结构不完整或已损坏，为避免风险已停止解锁。文件保存在：{path}。"
           + "如果确认文件不该有问题，你可以从加密备份恢复，或先把这份文件复制出来再联系我们。";

    public const string BlockNewerTitle = "密码库版本过新";

    public static string BlockNewerBody(string path)
        => $"此密码库由更新版本的 SnapFlow 创建，请升级 SnapFlow 后再打开。文件已保持原样：{path}。";

    public const string BlockUnknownTitle = "无法识别这个密码库文件";

    public static string BlockUnknownBody(string path)
        => $"这个文件不是 SnapFlow 密码库，或它的版本号无法识别。为避免误删，我们没有改动它：{path}。";

    // ---------------- 阻断卡上的动作 ----------------

    public const string OpenVaultFolder = "打开所在文件夹";
    public const string RestoreFromBackup = "用备份文件恢复";
    public const string RetryAfterMove = "重新检测";

    // ---------------- 创建受阻 / 读取失败 ----------------

    /// <summary>库已存在时点"创建"给的提示。v1 旧库不触发这条（会改名留档后直接创建）。</summary>
    public const string CreateBlockedByExistingVault = "当前目录已有密码库，请先备份并移走后再创建。";

    public const string MasterPasswordTooShort = "主密码至少需要 12 个字符。";

    /// <summary>文件不存在时不吓人：这是"还没创建"，不是"坏了"。</summary>
    public const string VaultMissing = "本机还没有密码库，请先创建。";

    public const string PasswordMismatch = "两次输入的主密码不一致。";

    public static string VaultReadFailed(string? detail)
        => string.IsNullOrWhiteSpace(detail) ? "读取密码库文件失败，请检查文件是否被其它程序占用。" : "读取密码库文件失败：" + detail;

    // ---------------- 备份 / 恢复 ----------------

    public const string BackupWrongPassword = "备份密码不正确，或这个备份文件已损坏。";

    public const string BackupLegacyRefused = "这个备份文件是旧版本格式，本版本无法恢复。";

    public const string BackupNewerRefused = "这个备份文件由更新版本的 SnapFlow 创建，请升级后再恢复。";

    // ---------------- 导入上限（ROB-07） ----------------
    // 三条文案都强调「本次没有导入任何条目」：用户最怕的是"提示失败但其实导了一半"。

    public static string ImportTooManyEntries(int count)
        => $"共 {count} 条，超过 {VaultLimits.MaxImportEntries} 条上限，请分批导入。本次没有导入任何条目。";

    /// <summary>
    /// 字段超限必须指出**第几条 + 哪个字段**：只报"有字段过长"等于让用户自己翻 5000 行去找。
    /// <paramref name="rowNumber"/> 是文件里的行号（含表头，从 1 数起）。
    /// </summary>
    public static string ImportFieldTooLong(int rowNumber, string field)
        => $"第 {rowNumber} 条的{field}超过 {VaultLimits.MaxFieldChars} 字符，已停止导入，本次没有导入任何条目。请修改后重试。";

    public static string ImportTextTooLarge(long bytes)
        => $"文件大小 {bytes / (1024.0 * 1024.0):F1} MB，超过 {VaultLimits.MaxCsvTextBytes / (1024 * 1024)} MB 上限，请先拆分再导入。";

    public static string CorruptBackupCreated(string fileName)
        => $"密码库文件结构损坏，已为你复制一份备份：{fileName}。原文件保持不动，你可以从加密备份恢复。";

    /// <summary>备份份数/体积触顶：只提示位置，绝不为了腾地方删用户的文件。</summary>
    public static string CorruptBackupLimitReached(string folder)
        => $"密码库文件结构损坏，但备份份数或体积已达上限，未再生成新的备份。原文件保持不变：{folder}";

    // ---------------- 生成器熵标签 ----------------

    /// <summary>
    /// 密码短语熵标签。**模板化**：bit 数由实际词表大小实算，不允许写死常量。
    /// 7776 词 × 6 词 = 77.5 bit ⇒ 显示 77（向下取整，宁可少报不多报）。
    /// </summary>
    public static string PassphraseEntropy(int words, int bits) => $"密码短语（{words} 词 ≈ {bits} bit）";

    // ---------------- 状态提示 ----------------

    public const string SaveFailed = "保存失败，已回退到上一个版本";

    public const string VaultLocked = "密码库已锁定。";
}
