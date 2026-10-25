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
    /// </summary>
    public static int MergeInto(VaultData target, IEnumerable<VaultEntry> incoming)
    {
        var added = 0;
        foreach (var entry in incoming)
        {
            var exists = target.Entries.Any(en =>
                en.Title.Equals(entry.Title, StringComparison.OrdinalIgnoreCase)
                && en.Username.Equals(entry.Username, StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                target.Entries.Add(entry);
                added++;
            }
        }

        return added;
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

    /// <summary>加密备份：与库容器同格式，但用独立 salt/nonce 和备份口令派生密钥。</summary>
    public static byte[] BuildEncryptedBackup(VaultData data, string backupPassword)
    {
        var header = VaultHeader.NewHeader();
        var key = VaultCrypto.DeriveKey(Encoding.UTF8.GetBytes(backupPassword), header);
        var body = VaultCrypto.Seal(header, key, data);
        return [.. header.ToBytes(), .. body];
    }

    /// <summary>解析加密备份；口令错误 / 文件损坏返回 null。</summary>
    public static VaultData? OpenEncryptedBackup(byte[] raw, string backupPassword)
    {
        if (!VaultHeader.TryParse(raw, out var header))
        {
            return null;
        }

        var key = VaultCrypto.DeriveKey(Encoding.UTF8.GetBytes(backupPassword), header);
        return VaultCrypto.Open(header, key, raw.AsSpan(VaultHeader.HeaderLength));
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
