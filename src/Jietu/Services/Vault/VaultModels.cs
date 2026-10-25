namespace Jietu;

/// <summary>密码库条目。Kind 目前只有 login（登录凭据）与 note（安全笔记）。</summary>
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
}

/// <summary>解密后的库明文载荷。只在内存中存在，锁定时随密钥一起清场。</summary>
public sealed class VaultData
{
    public List<VaultEntry> Entries { get; set; } = [];
    public List<VaultTrustedBrowser> TrustedBrowsers { get; set; } = [];
    /// <summary>复制密码后自动清空剪贴板的秒数；0 表示不清空。</summary>
    public int ClipboardClearSeconds { get; set; } = 30;
}

/// <summary>新建库时生成的初始载荷。</summary>
public static class VaultDefaults
{
    public static VaultData NewData() => new();
}
