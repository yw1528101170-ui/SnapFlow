namespace Jietu;

/// <summary>
/// 密码库的阈值常量集中处（唯一来源，禁止在业务代码里散落魔数）。
/// 本批次只放 A 批（安全地基）用到的项；B/C 批新增的阈值也应追加到本类，而不是就近定义。
/// </summary>
public static class VaultLimits
{
    /// <summary>条目历史保留版本数（FIFO 丢最老）。</summary>
    public const int HistoryVersions = 5;

    /// <summary>回收站保留天数与条数上限（先进先出）。</summary>
    public const int TrashRetainDays = 30;

    public const int TrashMaxEntries = 500;

    /// <summary>单个历史版本里单字段的最大字符数；超出截断（只可能来自异常数据）。</summary>
    public const int MaxHistoryFieldChars = 8192;

    /// <summary>损坏备份最多保留份数（同目录内）。</summary>
    public const int MaxCorruptBackups = 3;

    /// <summary>损坏备份单份与总量的上限（50MB）。超限只提示位置，不写盘、不删用户文件。</summary>
    public const long MaxCorruptBackupBytes = 50L * 1024 * 1024;

    /// <summary>损坏备份文件名里的时间戳格式（定长，按字典序即时间序）。</summary>
    public const string BackupTimestampFormat = "yyyyMMdd-HHmmss";

    /// <summary>密码短语默认词数（6 词 ≈ 77 bit，见 PasswordGenerator）。</summary>
    public const int DefaultPassphraseWords = 6;

    public const int MinPassphraseWords = 3;
    public const int MaxPassphraseWords = 12;

    /// <summary>剪贴板自动清除的默认秒数与上限（0 表示不清空）。</summary>
    public const int DefaultClipboardClearSeconds = 30;

    public const int MaxClipboardClearSeconds = 600;

    // ---------------- 导入上限（ROB-07） ----------------

    /// <summary>单次导入条目上限。超限整体拒绝，绝不截断（截断会让用户以为导进去了）。</summary>
    public const int MaxImportEntries = 5000;

    /// <summary>单字段上限（标题/网址/用户名/密码/备注各自独立计算）。</summary>
    public const int MaxFieldChars = 8192;

    /// <summary>
    /// CSV 原始文本上限（64 MB）。这是一道**内存闸门**，必须在解析之前判：
    /// 否则一个超大文件在解析阶段就把内存吃掉，条目数上限根本走不到。
    /// </summary>
    public const long MaxCsvTextBytes = 64L * 1024 * 1024;
}
