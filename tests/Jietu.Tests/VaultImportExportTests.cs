namespace Jietu.Tests;

public sealed class VaultImportExportTests
{
    [Fact]
    public void ParseCsv_ChromeExportFormat()
    {
        var csv = "name,url,username,password,note\nGitHub,https://github.com,alice,s3cret,work\n,https://example.com,bob,p2,\n";
        var rows = VaultImportExport.ParseCsv(csv);
        Assert.Equal(2, rows.Count);
        Assert.Equal("GitHub", rows[0].Name);
        Assert.Equal("s3cret", rows[0].Password);
        // 无 name 行用 url 兜底当标题。
        Assert.Equal("https://example.com", rows[1].Name);
    }

    [Fact]
    public void ParseCsv_BitwardenExportFormat()
    {
        var csv = "folder,favorite,type,name,notes,fields,reprompt,login_uri,login_username,login_password,totp\n"
                  + ",,login,GitHub,\"has, comma\",,,https://github.com,alice,pw1,JBSWY3DPEHPK3PXP\n";
        var rows = VaultImportExport.ParseCsv(csv);
        Assert.Single(rows);
        Assert.Equal("GitHub", rows[0].Name);
        Assert.Equal("alice", rows[0].Username);
        Assert.Equal("pw1", rows[0].Password);
        Assert.Equal("has, comma", rows[0].Notes);
        Assert.Equal("JBSWY3DPEHPK3PXP", rows[0].Totp);
    }

    [Fact]
    public void ParseCsv_MissingPasswordColumnThrows()
    {
        Assert.Throws<InvalidOperationException>(() => VaultImportExport.ParseCsv("name,username\na,b\n"));
    }

    [Fact]
    public void ParseCsv_HandlesQuotedNewlines()
    {
        var csv = "name,username,password,note\nsite,u1,p1,\"line1\nline2\"\n";
        var rows = VaultImportExport.ParseCsv(csv);
        Assert.Single(rows);
        Assert.Equal("line1\nline2", rows[0].Notes);
    }

    [Fact]
    public void ToEntries_ResolvesOtpauthUriAndTagsImport()
    {
        var rows = VaultImportExport.ParseCsv("name,url,username,password,totp\nx,https://x,u,p,otpauth://totp/x?secret=JBSWY3DPEHPK3PXP\n");
        var entries = VaultImportExport.ToEntries(rows);
        Assert.Single(entries);
        Assert.Equal("JBSWY3DPEHPK3PXP", entries[0].TotpSecret);
        Assert.Contains("导入", entries[0].Tags);
    }

    [Fact]
    public void BuildCsv_RoundTripsThroughParser()
    {
        var entries = new List<VaultEntry>
        {
            new()
            {
                Title = "带,逗号",
                Username = "alice",
                Password = "p\"q\"r",
                Url = "https://x.test",
                Notes = "第一行\n第二行",
            },
        };
        var csv = VaultImportExport.BuildCsv(entries);
        var rows = VaultImportExport.ParseCsv(csv);
        Assert.Single(rows);
        Assert.Equal("带,逗号", rows[0].Name);
        Assert.Equal("p\"q\"r", rows[0].Password);
        Assert.Equal("第一行\n第二行", rows[0].Notes);
    }

    [Fact]
    public void MergeInto_SkipsDuplicatesByTitleAndUsername()
    {
        var data = new VaultData
        {
            Entries = [new VaultEntry { Title = "GitHub", Username = "alice", Password = "old" }],
        };
        var incoming = new[]
        {
            new VaultEntry { Title = "github", Username = "Alice", Password = "new" },   // 标题+用户名忽略大小写重复
            new VaultEntry { Title = "GitHub", Username = "bob", Password = "p2" },      // 同标题不同用户名 → 新增
            new VaultEntry { Title = "Example", Username = "", Password = "p3" },        // 全新条目
        };

        Assert.Equal(2, VaultImportExport.MergeInto(data, incoming));
        Assert.Equal(3, data.Entries.Count);
        // 重复条目不得覆盖已有密码。
        Assert.Equal("old", data.Entries[0].Password);

        // 再次合并同一批全部跳过：导入同一份文件不会翻倍。
        Assert.Equal(0, VaultImportExport.MergeInto(data, incoming));
        Assert.Equal(3, data.Entries.Count);
    }

    [Fact]
    public async Task EncryptedBackup_RoundTripAndWrongPassword()
    {
        var data = new VaultData
        {
            Entries = [new VaultEntry { Title = "t", Username = "u", Password = "p" }],
        };
        var blob = await Task.Run(() => VaultImportExport.BuildEncryptedBackup(data, "backup pass"));

        var restored = await Task.Run(() => VaultImportExport.OpenEncryptedBackup(blob, "backup pass"));
        Assert.True(restored.Ok);
        Assert.Equal("t", restored.Data!.Entries[0].Title);

        // 备份口令错必须归到"认证失败"（可重试），不能和"文件损坏"混为一谈。
        var wrong = await Task.Run(() => VaultImportExport.OpenEncryptedBackup(blob, "wrong"));
        Assert.False(wrong.Ok);
        Assert.Equal(VaultOpenStatus.AuthenticationFailed, wrong.Status);
        Assert.Null(wrong.Data);
    }

    /// <summary>
    /// 备份与库文件共用同一套格式判定：v1 时代的加密备份必须被明确拒绝，
    /// 而不是"尽量解一下然后写回"（写回会静默丢掉新版本不认识的字段）。
    /// </summary>
    [Fact]
    public void OpenEncryptedBackup_RejectsLegacyV1Backup()
    {
        var legacy = LegacyV1Fixture.CreateBackup();

        var result = VaultImportExport.OpenEncryptedBackup(legacy, LegacyV1Fixture.Password);

        Assert.False(result.Ok);
        Assert.Equal(VaultOpenStatus.LegacyFormat, result.Status);
    }

    [Fact]
    public void OpenEncryptedBackup_RejectsGarbageWithoutThrowing()
    {
        var result = VaultImportExport.OpenEncryptedBackup("not a vault"u8.ToArray(), "whatever");
        Assert.False(result.Ok);
        Assert.Equal(VaultOpenStatus.UnknownFormat, result.Status);
    }
}
