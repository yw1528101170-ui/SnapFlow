using System.Buffers;
using System.Text.Json;

namespace Jietu;

/// <summary>写载荷时的进度快照（条目级）。</summary>
public readonly record struct VaultProgress(int Done, int Total)
{
    /// <summary>0..1；total 为 0 时视为已完成。</summary>
    public double Fraction => Total <= 0 ? 1d : Math.Clamp((double)Done / Total, 0d, 1d);
}

/// <summary>
/// 逐条目写库载荷 JSON。
///
/// 为什么不用 <c>JsonSerializer.SerializeToUtf8Bytes</c>：改主密码需要"每 N 条报一次进度 +
/// 条目间隙可取消"，整对象序列化是一根不可打断的黑盒，用户点取消只能等它跑完。
///
/// 字段名必须与 <see cref="VaultJsonContext"/> 的 camelCase 输出逐字一致——
/// 两套序列化路径（本类 / 源生成上下文）读写同一份文件，任何名字偏差都会变成"写得进、读不出"。
/// VaultModelsTests 用"写→读→逐字段比对"锁死这一点。
///
/// 线程：纯函数，任意线程可调用。返回的 <c>byte[]</c> 是明文，调用方负责 finally 清零。
/// </summary>
public static class VaultPayloadWriter
{
    /// <summary>进度回调的最小间隔（条）。逐条回调会把 UI 线程刷爆，按块报足够驱动进度条。</summary>
    private const int ProgressStep = 50;

    /// <summary>整库序列化为 UTF-8 JSON 字节。返回值是明文，谁分配谁清零。</summary>
    public static byte[] Write(VaultData data, IProgress<VaultProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        // 5000 条约 1MB：给个按条目数估算的初值，避免反复扩容拷贝明文。
        var capacity = Math.Max(1024, data.Entries.Count * 256 + data.RecycleBin.Count * 128);
        var buffer = new ArrayBufferWriter<byte>(capacity);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            Write(data, writer, progress, ct);
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>写入调用方提供的 writer（便于测试直接比对 JSON 文本）。</summary>
    public static void Write(
        VaultData data, Utf8JsonWriter writer, IProgress<VaultProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(writer);

        var total = data.Entries.Count + data.RecycleBin.Count;
        var done = 0;
        progress?.Report(new VaultProgress(0, total));

        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", data.SchemaVersion);

        writer.WriteStartArray("entries");
        foreach (var entry in data.Entries)
        {
            ct.ThrowIfCancellationRequested();
            WriteEntry(writer, entry);
            done++;
            if (done % ProgressStep == 0)
            {
                progress?.Report(new VaultProgress(done, total));
            }
        }

        writer.WriteEndArray();

        writer.WriteStartArray("recycleBin");
        foreach (var entry in data.RecycleBin)
        {
            ct.ThrowIfCancellationRequested();
            WriteEntry(writer, entry);
            done++;
            if (done % ProgressStep == 0)
            {
                progress?.Report(new VaultProgress(done, total));
            }
        }

        writer.WriteEndArray();

        writer.WriteStartArray("trustedBrowsers");
        foreach (var browser in data.TrustedBrowsers)
        {
            ct.ThrowIfCancellationRequested();
            WriteBrowser(writer, browser);
        }

        writer.WriteEndArray();

        writer.WriteStartArray("healthIgnores");
        foreach (var ignore in data.HealthIgnores)
        {
            ct.ThrowIfCancellationRequested();
            WriteHealthIgnore(writer, ignore);
        }

        writer.WriteEndArray();

        writer.WriteNumber("clipboardClearSeconds", data.ClipboardClearSeconds);
        writer.WriteEndObject();
        writer.Flush();

        progress?.Report(new VaultProgress(total, total));
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
