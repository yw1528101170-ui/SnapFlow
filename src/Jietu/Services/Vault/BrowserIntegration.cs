using System.Text;
using System.Text.Json;

namespace Jietu;

/// <summary>
/// 浏览器原生消息集成：写/删 HKCU 下 Chrome / Edge 的 NativeMessagingHosts 清单。
/// 清单指向本 exe（--native-messaging 模式），allowed_origins 限定扩展 ID。
/// 只动当前用户的 HKCU，不碰 HKLM，不弹 UAC。
/// </summary>
public static class BrowserIntegration
{
    public const string HostName = "com.snapflow.vault";

    private static readonly (string Root, string Name)[] Targets =
    [
        (@"Software\Google\Chrome\NativeMessagingHosts", "Google Chrome"),
        (@"Software\Microsoft\Edge\NativeMessagingHosts", "Microsoft Edge"),
    ];

    public static string ManifestPath => Path.Combine(AppPaths.Root, "native-messaging.json");

    /// <summary>
    /// 启用集成：写清单文件 + 两个浏览器注册表键。
    /// extensionId 为空时仍写清单（不带 allowed_origins，Chromium 会拒绝连接——
    /// 保留空 ID 写入是为了让用户先完成文件侧配置，填 ID 后无需重写清单）。
    /// 返回人类可读的结果或错误。
    /// </summary>
    public static string? Enable(string extensionId)
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                return "无法定位 SnapFlow.exe。";
            }

            var manifest = new
            {
                name = HostName,
                description = "SnapFlow 瞬流密码库本机集成",
                path = exePath,
                type = "stdio",
                allowed_origins = string.IsNullOrWhiteSpace(extensionId)
                    ? Array.Empty<string>()
                    : new[] { $"chrome-extension://{extensionId.Trim()}/" },
            };
            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(ManifestPath, json, new UTF8Encoding(false));

            var registryError = new List<string>();
            foreach (var (root, name) in Targets)
            {
                try
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(root + "\\" + HostName);
                    key.SetValue(null, ManifestPath);
                }
                catch (Exception ex)
                {
                    registryError.Add($"{name}：{ex.Message}");
                }
            }

            return registryError.Count == 0 ? null : "部分浏览器注册失败：" + string.Join("；", registryError);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>禁用集成：删除注册表键与清单文件。返回人类可读的错误或 null。</summary>
    public static string? Disable()
    {
        var errors = new List<string>();
        foreach (var (root, _) in Targets)
        {
            try
            {
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(root + "\\" + HostName);
            }
            catch
            {
                // 键不存在视为已禁用。
            }
        }

        try
        {
            if (File.Exists(ManifestPath))
            {
                File.Delete(ManifestPath);
            }
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
        }

        return errors.Count == 0 ? null : string.Join("；", errors);
    }

    public static bool IsEnabled()
    {
        foreach (var (root, _) in Targets)
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(root + "\\" + HostName);
            if (key != null)
            {
                return true;
            }
        }

        return false;
    }
}
