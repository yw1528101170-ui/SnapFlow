using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jietu;

public static class SettingsService
{
    /// <summary>JSON 里 AI Key 的密文前缀；没有这个前缀的值按旧版明文处理。</summary>
    private const string DpapiPrefix = "dpapi:";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// 最近一次 Load 的问题说明（如「设置文件已损坏，已备份并恢复默认设置」），成功加载后为 null。
    /// AppHost 在构造后立刻取一次，用于给用户可见提示。
    /// </summary>
    public static string? LastLoadError { get; private set; }

    /// <summary>最近一次 Save 的问题说明，成功保存后为 null。AppHost 据此提示「设置没能写进磁盘」。</summary>
    public static string? LastSaveError { get; private set; }

    /// <summary>
    /// 最近一次 Load 时设置文件尚不存在（或已损坏重建），即还没有任何用户偏好。
    /// 供启动对账（StartupService.Reconcile）判断是否采纳安装包写入的自启项；
    /// 用户有过偏好之后就严格以设置为准，不再看注册表。
    /// </summary>
    public static bool IsFirstRun { get; private set; }

    public static AppSettings Load(string path)
    {
        LastLoadError = null;
        IsFirstRun = false;
        try
        {
            if (!File.Exists(path))
            {
                IsFirstRun = true;
                return new AppSettings();
            }

            AppSettings? settings;
            try
            {
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options);
            }
            catch (Exception ex)
            {
                return ResetBroken(path, ex);
            }

            if (settings == null)
            {
                return ResetBroken(path, null);
            }

            Normalize(settings);
            settings.AiApiKey = DecryptApiKey(settings.AiApiKey);
            return settings;
        }
        catch (Exception ex)
        {
            // 读盘等非解析失败不备份源文件，直接把默认值交出去。
            Log.Error(ex);
            LastLoadError = "读取设置失败，已使用默认设置：" + ex.Message;
            return new AppSettings();
        }
    }

    public static bool Save(string path, AppSettings settings)
    {
        LastSaveError = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = SerializeForStorage(settings);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, overwrite: true);
            LastLoadError = null;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            LastSaveError = "保存设置失败（重启后会回退到上次设置）：" + ex.Message;
            try
            {
                File.Delete(path + ".tmp");
            }
            catch
            {
                // 临时文件删不掉不影响主流程。
            }

            return false;
        }
    }

    /// <summary>
    /// 把坏掉的设置文件改名成 &lt;path&gt;.bad-yyyyMMdd-HHmmss 备份，再回退默认值。
    /// 备份本身失败也不影响回退，只是会丢失现场。
    /// </summary>
    private static AppSettings ResetBroken(string path, Exception? exception)
    {
        // 损坏重建后同样等于「尚无用户偏好」，让启动对账可以采纳安装包写入的自启项。
        IsFirstRun = true;
        if (exception == null)
        {
            Log.Info("设置文件内容为空，按损坏处理。");
        }
        else
        {
            Log.Error(exception);
        }

        var backup = BackupBroken(path);
        LastLoadError = backup == null
            ? "设置文件已损坏，已恢复默认设置。"
            : $"设置文件已损坏，已备份为 {Path.GetFileName(backup)} 并恢复默认设置。";
        return new AppSettings();
    }

    private static string? BackupBroken(string path)
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var backup = $"{path}.bad-{stamp}";
            for (var attempt = 1; File.Exists(backup); attempt++)
            {
                backup = $"{path}.bad-{stamp}-{attempt}";
            }

            File.Move(path, backup);
            Log.Info("设置文件已备份到 " + backup);
            return backup;
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            return null;
        }
    }

    /// <summary>
    /// 逐字段兜底：手改或跨版本残留的 null / 非法值一律归一，避免后续功能拿到脏数据。
    /// </summary>
    private static void Normalize(AppSettings settings)
    {
        var defaults = new AppSettings();
        if (string.IsNullOrWhiteSpace(settings.RegionHotkey))
        {
            settings.RegionHotkey = defaults.RegionHotkey;
        }

        if (string.IsNullOrWhiteSpace(settings.HistoryHotkey))
        {
            settings.HistoryHotkey = defaults.HistoryHotkey;
        }

        settings.SaveFolder ??= "";
        settings.IgnoredProcesses = ProcessNames.Normalize(settings.IgnoredProcesses);
        settings.OsdPosition = NormalizeOsdPosition(settings.OsdPosition);
        settings.OsdDisplay = NormalizeOsdDisplay(settings.OsdDisplay);
        settings.MultiMonitorCaptureMode = NormalizeCaptureMode(settings.MultiMonitorCaptureMode);
        if (string.IsNullOrWhiteSpace(settings.AiApiUrl))
        {
            settings.AiApiUrl = defaults.AiApiUrl;
        }

        if (string.IsNullOrWhiteSpace(settings.AiModel))
        {
            settings.AiModel = defaults.AiModel;
        }

        if (string.IsNullOrWhiteSpace(settings.AiPrompt))
        {
            settings.AiPrompt = defaults.AiPrompt;
        }

        settings.AiApiKey ??= "";
        settings.RetentionDays = Math.Clamp(settings.RetentionDays, 1, 3650);
    }

    private static string NormalizeOsdPosition(string? value)
        => string.Equals(value?.Trim(), "TopLeft", StringComparison.OrdinalIgnoreCase) ? "TopLeft" : "TopRight";

    private static string NormalizeOsdDisplay(string? value)
        => string.Equals(value?.Trim(), "FollowCursor", StringComparison.OrdinalIgnoreCase) ? "FollowCursor" : "Primary";

    private static string NormalizeCaptureMode(string? value)
    {
        var text = value?.Trim();
        if (string.Equals(text, "AllMonitors", StringComparison.OrdinalIgnoreCase))
        {
            return "AllMonitors";
        }

        return string.Equals(text, "ActiveMonitor", StringComparison.OrdinalIgnoreCase) ? "ActiveMonitor" : "Auto";
    }

    /// <summary>
    /// 落盘前把内存里的明文 AI Key 换成 DPAPI 密文。
    /// 不能直接改传入的 settings：AppHost 还要拿着它继续用明文调 AI 接口。
    /// </summary>
    private static string SerializeForStorage(AppSettings settings)
    {
        if (JsonSerializer.SerializeToNode(settings, Options) is not JsonObject node)
        {
            return JsonSerializer.Serialize(settings, Options);
        }

        var key = Options.PropertyNamingPolicy?.ConvertName(nameof(AppSettings.AiApiKey)) ?? nameof(AppSettings.AiApiKey);
        node[key] = EncryptApiKey(settings.AiApiKey);
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string EncryptApiKey(string plain)
    {
        if (string.IsNullOrEmpty(plain))
        {
            return "";
        }

        var cipher = NativeMethods.DpapiProtect(Encoding.UTF8.GetBytes(plain));
        if (cipher == null)
        {
            // 加密失败绝不落盘明文：返回空串按「未填写」处理，让用户重新保存。
            // 解密端对空串与旧明文值都兼容（见 DecryptApiKey），不会崩。
            Log.Error("AI API Key 加密失败，本次保存不写入密钥，请在设置页重新保存。");
            return "";
        }

        return DpapiPrefix + Convert.ToBase64String(cipher);
    }

    private static string DecryptApiKey(string stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return "";
        }

        if (!stored.StartsWith(DpapiPrefix, StringComparison.Ordinal))
        {
            // 旧版明文：直接接受，下次保存时自动升级成密文。
            return stored;
        }

        try
        {
            var cipher = Convert.FromBase64String(stored[DpapiPrefix.Length..]);
            var plain = NativeMethods.DpapiUnprotect(cipher);
            if (plain == null)
            {
                // 换 Windows 账户 / 换机器 / 密文损坏时无法解密，按「未填写」处理并让用户重填。
                Log.Info("AI API Key 解密失败，已按未填写处理。");
                return "";
            }

            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            return "";
        }
    }
}
