using System.Text.Json;

namespace Jietu.Tests;

/// <summary>
/// 载荷序列化的双路径一致性：VaultPayloadWriter（手写 Utf8JsonWriter，保存/改密路径）与
/// VaultJsonContext（源生成，解密读取路径）读写同一份文件。任何字段名/语义偏差都会变成
/// "写得进、读不出"，这里用"写→读→逐字段比对"锁死两套路径。
/// </summary>
public sealed class VaultPayloadWriterTests
{
    [Fact]
    public void WriteThenDeserialize_RoundTripsEveryField()
    {
        var data = SampleData();
        var json = VaultPayloadWriter.Write(data);

        var read = JsonSerializer.Deserialize<VaultData>(json, VaultJsonContext.Default.VaultData);
        Assert.NotNull(read);

        Assert.Equal(data.SchemaVersion, read.SchemaVersion);
        Assert.Equal(data.ClipboardClearSeconds, read.ClipboardClearSeconds);
        Assert.Equal(data.Entries.Count, read.Entries.Count);
        Assert.Equal(data.RecycleBin.Count, read.RecycleBin.Count);
        Assert.Equal(data.TrustedBrowsers.Count, read.TrustedBrowsers.Count);
        Assert.Equal(data.HealthIgnores.Count, read.HealthIgnores.Count);

        var source = data.Entries[0];
        var target = read.Entries[0];
        Assert.Equal(source.Id, target.Id);
        Assert.Equal(source.Kind, target.Kind);
        Assert.Equal(source.Title, target.Title);
        Assert.Equal(source.Username, target.Username);
        Assert.Equal(source.Password, target.Password);
        Assert.Equal(source.Url, target.Url);
        Assert.Equal(source.Notes, target.Notes);
        Assert.Equal(source.TotpSecret, target.TotpSecret);
        Assert.Equal(source.Tags, target.Tags);
        Assert.Equal(source.CreatedUtc, target.CreatedUtc);
        Assert.Equal(source.UpdatedUtc, target.UpdatedUtc);
        Assert.Null(target.DeletedUtc);
        Assert.Equal(source.CriticalOverride, target.CriticalOverride);
        Assert.Equal(source.History.Count, target.History.Count);
        Assert.Equal(source.History[0].TotpSecret, target.History[0].TotpSecret);
        Assert.Equal(source.History[0].Tags, target.History[0].Tags);
        Assert.Equal(source.History[0].ChangedFields, target.History[0].ChangedFields);

        // 回收站条目：deletedUtc 非 null 是不变式，序列化必须原样带出。
        Assert.Equal(data.RecycleBin[0].DeletedUtc, read.RecycleBin[0].DeletedUtc);

        // 关联浏览器与体检忽略项同样逐字段比对。
        Assert.Equal(data.TrustedBrowsers[0].TokenHash, read.TrustedBrowsers[0].TokenHash);
        Assert.Equal(data.HealthIgnores[0].Dimension, read.HealthIgnores[0].Dimension);
        Assert.Equal(data.HealthIgnores[0].GroupKey, read.HealthIgnores[0].GroupKey);
    }

    [Fact]
    public void Write_EmptyVault_StillReadableAndComplete()
    {
        var json = VaultPayloadWriter.Write(new VaultData());
        var read = JsonSerializer.Deserialize<VaultData>(json, VaultJsonContext.Default.VaultData);

        Assert.NotNull(read);
        Assert.Empty(read.Entries);
        Assert.Empty(read.RecycleBin);
        Assert.Empty(read.TrustedBrowsers);
        Assert.Empty(read.HealthIgnores);
        Assert.Equal(VaultLimits.DefaultClipboardClearSeconds, read.ClipboardClearSeconds);
    }

    [Fact]
    public void Write_NullCriticalOverrideAndEmptyGroupKey_PreserveNullSemantics()
    {
        // criticalOverride 的 null 表示"由域名表自动判定"，groupKey 的 null 与空串语义不同：
        // 源生成上下文配置了不忽略 null，这里锁死"手写路径也不丢 null"。
        var data = new VaultData();
        data.Entries.Add(new VaultEntry { Id = "e1", Title = "t", Password = "p", CriticalOverride = null });
        data.HealthIgnores.Add(new VaultHealthIgnore { EntryId = "e1", Dimension = "weak", GroupKey = null });

        var json = VaultPayloadWriter.Write(data);
        var jsonText = System.Text.Encoding.UTF8.GetString(json);
        Assert.Contains("\"criticalOverride\":null", jsonText);
        Assert.Contains("\"groupKey\":null", jsonText);

        var read = JsonSerializer.Deserialize<VaultData>(json, VaultJsonContext.Default.VaultData);
        Assert.NotNull(read);
        Assert.Null(read.Entries[0].CriticalOverride);
        Assert.Null(read.HealthIgnores[0].GroupKey);
    }

    private static VaultData SampleData()
    {
        var created = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var data = new VaultData
        {
            ClipboardClearSeconds = 45,
            Entries =
            [
                new VaultEntry
                {
                    Id = "entry-1",
                    Kind = "login",
                    Title = "示例站",
                    Username = "user@example.com",
                    Password = "p@ssw0rd!",
                    Url = "https://example.com/login",
                    Notes = "备注",
                    TotpSecret = "JBSWY3DPEHPK3PXP",
                    Tags = ["工作", "导入"],
                    CreatedUtc = created,
                    UpdatedUtc = created.AddDays(1),
                    CriticalOverride = true,
                    History =
                    [
                        new VaultEntryVersion
                        {
                            ChangedUtc = created,
                            ChangedFields = ["title", "totpSecret"],
                            Title = "旧标题",
                            TotpSecret = "OLD",
                            Tags = ["旧"],
                        },
                    ],
                },
            ],
            RecycleBin =
            [
                new VaultEntry
                {
                    Id = "trash-1",
                    Title = "已删",
                    DeletedUtc = created.AddDays(2),
                },
            ],
            TrustedBrowsers =
            [
                new VaultTrustedBrowser
                {
                    Id = "b1",
                    DisplayName = "Chrome · SnapFlow 扩展",
                    TokenHash = new string('a', 64),
                    AddedUtc = created,
                },
            ],
            HealthIgnores =
            [
                new VaultHealthIgnore { EntryId = "entry-1", Dimension = "reused", GroupKey = "g1", IgnoredUtc = created },
            ],
        };
        return data;
    }
}
