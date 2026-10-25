using System.Buffers;
using System.Text.Json;

namespace Jietu;

/// <summary>
/// 把 <see cref="VaultData"/> 逐条目写成载荷 JSON。
///
/// 为什么不用 <c>JsonSerializer.SerializeToUtf8Bytes</c>：这里需要按字段精确控制输出
/// （camelCase 字段名、null 语义），并保证与 <see cref="VaultJsonContext"/> 的读写路径
/// 字段名逐字一致——两套序列化路径读写同一份文件，任何名字偏差都会变成"写得进、读不出"。
/// VaultPayloadWriterTests 用"写→读→逐字段比对"锁死这一点。
///
/// 线程：纯函数，任意线程可调用。返回的 <c>byte[]</c> 是明文，调用方负责 finally 清零。
/// </summary>
public static class VaultPayloadWriter
{
    /// <summary>整库序列化为 UTF-8 JSON 字节。返回值是明文，谁分配谁清零。</summary>
    public static byte[] Write(VaultData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        // 5000 条约 1MB：给个按条目数估算的初值，避免反复扩容拷贝明文。
        var capacity = Math.Max(1024, data.Entries.Count * 256 + data.RecycleBin.Count * 128);
        var buffer = new ArrayBufferWriter<byte>(capacity);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", data.SchemaVersion);

            writer.WriteStartArray("entries");
            foreach (var entry in data.Entries)
            {
                WriteEntry(writer, entry);
            }

            writer.WriteEndArray();

            writer.WriteStartArray("recycleBin");
            foreach (var entry in data.RecycleBin)
            {
                WriteEntry(writer, entry);
            }

            writer.WriteEndArray();

            writer.WriteStartArray("trustedBrowsers");
            foreach (var browser in data.TrustedBrowsers)
            {
                WriteBrowser(writer, browser);
            }

            writer.WriteEndArray();

            writer.WriteStartArray("healthIgnores");
            foreach (var ignore in data.HealthIgnores)
            {
                WriteHealthIgnore(writer, ignore);
            }

            writer.WriteEndArray();

            writer.WriteNumber("clipboardClearSeconds", data.ClipboardClearSeconds);
            writer.WriteEndObject();
            writer.Flush();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteEntry(Utf8JsonWriter writer, VaultEntry entry)
    {
        writer.WriteStartObject();
        writer.WriteString("id", entry.Id ?? "");
        writer.WriteString("kind", entry.Kind ?? "login");
        writer.WriteString("title", entry.Title ?? "");
        writer.WriteString("username", entry.Username ?? "");
        writer.WriteString("password", entry.Password ?? "");
        writer.WriteString("url", entry.Url ?? "");
        writer.WriteString("notes", entry.Notes ?? "");

        writer.WriteStartArray("tags");
        if (entry.Tags != null)
        {
            foreach (var tag in entry.Tags)
            {
                writer.WriteStringValue(tag ?? "");
            }
        }

        writer.WriteEndArray();

        writer.WriteString("totpSecret", entry.TotpSecret ?? "");
        writer.WriteString("createdUtc", entry.CreatedUtc);
        writer.WriteString("updatedUtc", entry.UpdatedUtc);

        if (entry.DeletedUtc is { } deleted)
        {
            writer.WriteString("deletedUtc", deleted);
        }
        else
        {
            writer.WriteNull("deletedUtc");
        }

        if (entry.CriticalOverride is { } critical)
        {
            writer.WriteBoolean("criticalOverride", critical);
        }
        else
        {
            writer.WriteNull("criticalOverride");
        }

        writer.WriteStartArray("history");
        if (entry.History != null)
        {
            foreach (var version in entry.History)
            {
                WriteVersion(writer, version);
            }
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteVersion(Utf8JsonWriter writer, VaultEntryVersion version)
    {
        writer.WriteStartObject();
        writer.WriteString("changedUtc", version.ChangedUtc);
        writer.WriteStartArray("changedFields");
        if (version.ChangedFields != null)
        {
            foreach (var field in version.ChangedFields)
            {
                writer.WriteStringValue(field ?? "");
            }
        }

        writer.WriteEndArray();
        writer.WriteString("title", version.Title ?? "");
        writer.WriteString("username", version.Username ?? "");
        writer.WriteString("password", version.Password ?? "");
        writer.WriteString("url", version.Url ?? "");
        writer.WriteString("notes", version.Notes ?? "");
        writer.WriteStartArray("tags");
        if (version.Tags != null)
        {
            foreach (var tag in version.Tags)
            {
                writer.WriteStringValue(tag ?? "");
            }
        }

        writer.WriteEndArray();
        writer.WriteString("totpSecret", version.TotpSecret ?? "");
        writer.WriteEndObject();
    }

    private static void WriteBrowser(Utf8JsonWriter writer, VaultTrustedBrowser browser)
    {
        writer.WriteStartObject();
        writer.WriteString("id", browser.Id ?? "");
        writer.WriteString("displayName", browser.DisplayName ?? "");
        writer.WriteString("tokenHash", browser.TokenHash ?? "");
        writer.WriteString("addedUtc", browser.AddedUtc);
        writer.WriteEndObject();
    }

    private static void WriteHealthIgnore(Utf8JsonWriter writer, VaultHealthIgnore ignore)
    {
        writer.WriteStartObject();
        writer.WriteString("entryId", ignore.EntryId ?? "");
        writer.WriteString("dimension", ignore.Dimension ?? "");
        if (string.IsNullOrEmpty(ignore.GroupKey))
        {
            writer.WriteNull("groupKey");
        }
        else
        {
            writer.WriteString("groupKey", ignore.GroupKey);
        }

        writer.WriteString("ignoredUtc", ignore.IgnoredUtc);
        writer.WriteEndObject();
    }
}
