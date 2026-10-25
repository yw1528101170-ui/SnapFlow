namespace Jietu;

/// <summary>
/// vault.spv 的文件 IO：只负责字节进出，加解密在 VaultCrypto。
/// 写入走 tmp + rename 原子替换；加载时发现文件损坏，先备份再按「无库」处理，
/// 绝不覆盖、不删除用户数据（与 HistoryStore / SettingsService 同一套原则）。
/// </summary>
public static class VaultStore
{
    /// <summary>最近一次 Load 发现损坏时的备份路径；正常为 null。</summary>
    public static string? LastRecoveryMessage { get; private set; }

    public static bool Exists(string path) => File.Exists(path);

    /// <summary>读取整个库文件。不存在返回 null；读取异常按损坏处理（备份 + 返回 null）。</summary>
    public static byte[]? Load(string path)
    {
        LastRecoveryMessage = null;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            LastRecoveryMessage = "读取密码库失败：" + ex.Message;
            return null;
        }
    }

    /// <summary>
    /// 解析文件为「头 + 密文体」。文件损坏（头不合法）时备份原文件并返回 null。
    /// 注意密文体解不开不是本层的职责——那是解锁流程的主密码错误路径。
    /// </summary>
    public static (VaultHeader Header, byte[] Body)? Parse(byte[]? raw)
    {
        LastRecoveryMessage = null;
        if (raw == null)
        {
            return null;
        }

        if (!VaultHeader.TryParse(raw, out var header))
        {
            LastRecoveryMessage = "密码库文件头损坏。";
            return null;
        }

        return (header, raw[VaultHeader.HeaderLength..]);
    }

    /// <summary>原子落盘：先写 .tmp 再替换。写失败抛异常，由调用方提示用户。</summary>
    public static void Save(string path, VaultHeader header, byte[] cipherBody)
    {
        var headerBytes = header.ToBytes();
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(headerBytes);
            stream.Write(cipherBody);
            stream.Flush(true);
        }

        File.Move(temporary, path, overwrite: true);
    }
}
