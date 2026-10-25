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

    /// <summary>从已写入的清单里读出关联的扩展 ID；未启用或未关联（allowed_origins 为空）时返回 null。</summary>
    public static string? GetConfiguredExtensionId()
    {
        try
        {
            if (!File.Exists(ManifestPath))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(ManifestPath));
            if (!doc.RootElement.TryGetProperty("allowed_origins", out var origins)
                || origins.ValueKind != JsonValueKind.Array
                || origins.GetArrayLength() == 0)
            {
                return null;
            }

            var origin = origins[0].GetString() ?? "";
            const string prefix = "chrome-extension://";
            if (origin.StartsWith(prefix, StringComparison.Ordinal))
            {
                origin = origin[prefix.Length..];
            }

            origin = origin.TrimEnd('/');
            return origin.Length > 0 ? origin : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 计算「加载已解压的扩展程序」对应的扩展 ID：Chromium 用扩展文件夹绝对路径的
    /// SHA-256（UTF-16LE 编码）前 128 位，把十六进制字符映射到 a–p 字母表。
    /// 桌面端和浏览器加载的是同一个文件夹时结果一致，因此可以代替手工复制 ID。
    /// </summary>
    public static string ComputeUnpackedExtensionId(string folderPath)
    {
        var fullPath = Path.GetFullPath(folderPath.TrimEnd('\\', '/'));
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.Unicode.GetBytes(fullPath));
        var chars = new char[32];
        for (var i = 0; i < 32; i++)
        {
            var nibble = (hash[i / 2] >> (i % 2 == 0 ? 4 : 0)) & 0xF;
            chars[i] = (char)('a' + nibble);
        }

        return new string(chars);
    }

    /// <summary>
    /// 定位扩展文件夹：安装目录或开发仓库根下的 ext/snapflow-vault。
    /// 从 exe 所在目录逐级向上找，两种布局都能覆盖；找不到返回 null。
    /// </summary>
    public static string? FindExtensionFolder()
    {
        for (var dir = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)); dir != null; dir = dir.Parent)
        {
            try
            {
                var candidate = Path.Combine(dir.FullName, "ext", "snapflow-vault");
                if (File.Exists(Path.Combine(candidate, "manifest.json")))
                {
                    return candidate;
                }
            }
            catch
            {
                // 逐级向上探测遇到无权限目录就跳过。
            }
        }

        return null;
    }

    /// <summary>本机已安装的 Chromium 浏览器（Chrome / Edge），供一键安装引导。</summary>
    public sealed record DetectedBrowser(string Id, string Name, string ExePath, string ExtensionsUrl);

    public static DetectedBrowser[] DetectBrowsers()
    {
        var list = new List<DetectedBrowser>();
        var chrome = FindBrowserExe("chrome.exe");
        if (chrome != null)
        {
            list.Add(new DetectedBrowser("chrome", "Google Chrome", chrome, "chrome://extensions"));
        }

        var edge = FindBrowserExe("msedge.exe");
        if (edge != null)
        {
            list.Add(new DetectedBrowser("edge", "Microsoft Edge", edge, "edge://extensions"));
        }

        return [.. list];
    }

    private static string? FindBrowserExe(string exeName)
    {
        foreach (var root in new[]
                 {
                     @"Software\Microsoft\Windows\CurrentVersion\App Paths\" + exeName,
                     @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\" + exeName,
                 })
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(root)
                                ?? Microsoft.Win32.Registry.LocalMachine.OpenSubKey(root);
                if (key?.GetValue(null) is string path && File.Exists(path))
                {
                    return path;
                }
            }
            catch
            {
                // 注册表探测失败就换下一个来源。
            }
        }

        // Edge 是 Windows 系统组件，注册表缺失时兜底常规安装路径。
        if (exeName.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var path in new[]
                     {
                         @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                         @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
                     })
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }
}
