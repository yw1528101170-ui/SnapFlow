using System.Security.Cryptography;
using System.Text;

namespace Jietu;

/// <summary>CSV 里解析出的一行凭据。</summary>
public sealed record VaultCsvRow(string Name, string Url, string Username, string Password, string Notes, string Totp);

/// <summary>
/// 密码库导入导出：
/// - CSV 导入兼容 Chrome / Edge / Bitwarden 的密码导出格式（列名模糊匹配）。
/// - CSV 导出为明文（用户明确操作才生成，UI 层必须警告）。
/// - 加密备份复用库容器格式，用用户提供的备份口令独立派生密钥。
/// </summary>
public static class VaultImportExport
{
    public static List<VaultCsvRow> ParseCsv(string text)
    {
        // 内存闸门必须在解析**之前**：超大文件在解析阶段就会把内存吃掉，
        // 后面的条目数上限校验根本走不到（那时已经 OOM 了）。
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes > VaultLimits.MaxCsvTextBytes)
        {
            throw new InvalidOperationException(VaultMessages.ImportTextTooLarge(bytes));
        }

        var rows = ParseCsvRows(text);
        if (rows.Count == 0)
        {
            return [];
        }

        // 表头列名映射：三家浏览器的导出表头各有差异，按包含关系模糊匹配。
        var header = rows[0];
        var nameIdx = FindColumn(header, "name", "title", "账户名称");
        var urlIdx = FindColumn(header, "url", "uri", "login_uri", "网址");
        var userIdx = FindColumn(header, "username", "login_username", "user", "用户名", "用户名称", "login");
        var passIdx = FindColumn(header, "password", "login_password", "密码");
        var noteIdx = FindColumn(header, "note", "notes", "备注");
        var totpIdx = FindColumn(header, "totp", "otpauth");
        // Chrome 导出没有 name 列时用 url 当标题。
        if (nameIdx < 0 && urlIdx >= 0)
        {
            nameIdx = urlIdx;
        }

        if (userIdx < 0 || passIdx < 0)
        {
            throw new InvalidOperationException("CSV 缺少用户名或密码列，请确认是浏览器/Bitwarden 导出的密码文件。");
        }

        var result = new List<VaultCsvRow>();
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            result.Add(new VaultCsvRow(
                // Chrome 导出的 name 列常为空：此时用 url 兜底当标题。
                Name: FirstNonEmpty(At(row, nameIdx), At(row, urlIdx)),
                Url: At(row, urlIdx),
                Username: At(row, userIdx),
                Password: At(row, passIdx),
                Notes: At(row, noteIdx),
                Totp: At(row, totpIdx)));
        }

        return result;
    }

    public static List<VaultEntry> ToEntries(IEnumerable<VaultCsvRow> rows)
    {
        var now = DateTimeOffset.UtcNow;
        var entries = new List<VaultEntry>();
        foreach (var row in rows)
        {
            var secret = row.Totp.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase)
                ? Totp.SecretFromOtpAuthUri(row.Totp) ?? ""
                : row.Totp;
            entries.Add(new VaultEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = "login",
                Title = row.Name,
                Username = row.Username,
                Password = row.Password,
                Url = row.Url,
                Notes = row.Notes,
                TotpSecret = secret,
                Tags = ["导入"],
                CreatedUtc = now,
                UpdatedUtc = now,
            });
        }

        return entries;
    }

    /// <summary>
    /// 把条目合并进现有库（CSV 导入与备份恢复共用）：按「标题+用户名」忽略大小写去重，
    /// 已存在则跳过。返回新增条数。
    ///
    /// 实现改为**先建索引再比对**：原实现对每条都 `Any()` 扫全库，
    /// 导入 5000 条时是 2500 万次字符串比较（秒级卡顿 + 大量临时分配）。
    /// </summary>
    public static int MergeInto(VaultData target, IEnumerable<VaultEntry> incoming)
    {
        var index = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in target.Entries)
        {
            index.Add(DedupeKey(existing.Title, existing.Username));
        }

        var added = 0;
        foreach (var entry in incoming)
        {
            if (index.Add(DedupeKey(entry.Title, entry.Username)))
            {
                target.Entries.Add(entry);
                added++;
            }
        }

        return added;
    }

    /// <summary>
    /// 去重键。用 NUL 分隔两个字段：否则「ab + c」与「a + bc」会被误判成同一条。
    /// </summary>
    private static string DedupeKey(string title, string username) => title + "\0" + username;

    /// <summary>
    /// 导入上限校验（解析后、写入前调用）。返回 null 表示通过，否则返回可直接展示的原因。
    ///
    /// 语义是**整体拒绝**：任何一条不合格就一条都不写库。截断是最糟的实现——
    /// 用户会以为导进去了，实际丢了一半密码。
    /// </summary>
    public static string? ValidateImportRows(IReadOnlyList<VaultCsvRow> rows)
    {
        if (rows.Count > VaultLimits.MaxImportEntries)
        {
            return VaultMessages.ImportTooManyEntries(rows.Count);
        }

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            // 行号按用户在 Excel 里看到的算：+1 表头，+1 从 1 数起。
            var rowNumber = i + 2;
            if (row.Name.Length > VaultLimits.MaxFieldChars)
            {
                return VaultMessages.ImportFieldTooLong(rowNumber, "标题");
            }

            if (row.Url.Length > VaultLimits.MaxFieldChars)
            {
                return VaultMessages.ImportFieldTooLong(rowNumber, "网址");
            }

            if (row.Username.Length > VaultLimits.MaxFieldChars)
            {
                return VaultMessages.ImportFieldTooLong(rowNumber, "用户名");
            }

            if (row.Password.Length > VaultLimits.MaxFieldChars)
            {
                return VaultMessages.ImportFieldTooLong(rowNumber, "密码");
            }

            if (row.Notes.Length > VaultLimits.MaxFieldChars)
            {
                return VaultMessages.ImportFieldTooLong(rowNumber, "备注");
            }
        }

        return null;
    }

    /// <summary>明文 CSV 导出（RFC 4180 转义）。调用方必须先向用户二次确认。</summary>
    public static string BuildCsv(IEnumerable<VaultEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("name,url,username,password,note");
        foreach (var entry in entries)
        {
            sb.AppendLine(string.Join(",",
                Escape(entry.Title), Escape(entry.Url), Escape(entry.Username), Escape(entry.Password), Escape(entry.Notes)));
        }

        return sb.ToString();
    }

    /// <summary>
    /// 加密备份：与库容器**同格式**（v2，头参与 AAD），但用独立 salt/nonce 和备份口令派生密钥。
    /// 同格式的意义：恢复时不需要第二套解析逻辑，且"备份里能读出来的东西"与库里完全一致
    /// （含回收站与历史）。
    /// </summary>
    public static byte[] BuildEncryptedBackup(VaultData data, string backupPassword)
    {
        var header = VaultKdfPolicy.NewVaultHeader();
        var passwordBytes = Encoding.UTF8.GetBytes(backupPassword);
        VaultMemoryDebug.Track("backup-password-utf8", passwordBytes);
        VaultKey? key = null;
        try
        {
            key = VaultCrypto.DeriveKey(passwordBytes, header);
            return VaultCrypto.SealFile(data, header, key, out _);
        }
        finally
        {
            // 备份口令同样是口令：离开作用域必须清零（含派生/加密抛异常的路径）。
            CryptographicOperations.ZeroMemory(passwordBytes);
            key?.Dispose();
        }
    }

    /// <summary>
    /// 恢复加密备份。与库文件共用同一套分类（<see cref="VaultStore.ClassifyBytes"/>）：
    /// v1 备份与更高版本的备份都明确拒绝，而不是"尽量解一下试试"。
    /// 口令错误返回 <see cref="VaultOpenStatus.AuthenticationFailed"/>，由 UI 提示重输。
    /// </summary>
    public static VaultOpenResult OpenEncryptedBackup(byte[] raw, string backupPassword)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var read = VaultStore.ClassifyBytes(raw);
        var passwordBytes = Encoding.UTF8.GetBytes(backupPassword);
        VaultMemoryDebug.Track("backup-password-utf8-open", passwordBytes);
        try
        {
            return VaultOpenPipeline.Open(read, passwordBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    // ---------------- CSV 基础解析（RFC 4180：引号、逗号、换行转义） ----------------

    internal static List<List<string>> ParseCsvRows(string text)
    {
        var rows = new List<List<string>>();
        var field = new StringBuilder();
        var row = new List<string>();
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }
            }
            else if (ch == '"')
            {
                inQuotes = true;
            }
            else if (ch == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (ch is '\r' or '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = [];
            }
            else
            {
                field.Append(ch);
            }
        }

        // 最后一行没有换行符也要收尾；空文件不算行。
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }

    private static string Escape(string value)
    {
        value ??= "";
        return value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    private static int FindColumn(IReadOnlyList<string> header, params string[] names)
    {
        for (var i = 0; i < header.Count; i++)
        {
            var cell = header[i].Trim().Trim('"');
            foreach (var name in names)
            {
                if (cell.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        // 精确匹配失败后按包含匹配（如 Bitwarden 的 login_username）。
        for (var i = 0; i < header.Count; i++)
        {
            var cell = header[i].Trim().Trim('"');
            foreach (var name in names)
            {
                if (cell.Contains(name, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static string At(IReadOnlyList<string> row, int index)
        => index >= 0 && index < row.Count ? row[index] : "";

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
}
