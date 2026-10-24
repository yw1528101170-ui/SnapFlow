using Microsoft.Win32;

namespace Jietu;

internal static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // 与安装包（installer\Jietu.iss）保持一致，否则应用内关闭自启动删不掉安装包写的值。
    private const string ValueName = "SnapFlow";
    // 旧版本（应用与安装包都用过）的值名，任何一次 Apply 都顺手清掉，避免登录时启动两份。
    private const string LegacyValueName = "Jietu";

    /// <summary>
    /// 启动时把 settings.json 里的自启开关与注册表实际情况对账，返回真正生效的状态。
    /// 安装向导里勾选的「开机自启」只会写 HKCU\...\Run（安装程序碰不到当前用户的设置文件），
    /// 因此只在首次初始化（设置文件尚不存在、还没有任何用户偏好，见 SettingsService.IsFirstRun）
    /// 时采纳注册表里已有的自启项；之后严格以设置为准——设置开则注册、关则删除，
    /// 绝不因注册表残留值而复活，否则用户手动关掉自启后每次启动又会被写回。
    /// </summary>
    public static bool Reconcile(bool desired, bool adoptExisting)
    {
        var enabled = desired || (adoptExisting && IsRegistered());
        Apply(enabled);
        return enabled;
    }

    /// <summary>
    /// 注册表 Run 键里是否已有本程序的自启项（兼容旧值名 Jietu）。
    /// 读不到（组策略 / 键不存在 / 被安全软件拦截）时按「未开启」处理，让调用方回退到 settings 的值。
    /// </summary>
    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            if (key == null)
            {
                return false;
            }

            return HasCommand(key, ValueName) || HasCommand(key, LegacyValueName);
        }
        catch (Exception ex)
        {
            Log.Info("读取开机自启动项失败：" + ex.Message);
            return false;
        }
    }

    private static bool HasCommand(RegistryKey key, string name)
        => key.GetValue(name) is string command && !string.IsNullOrWhiteSpace(command);

    public static void Apply(bool enabled)
    {
        try
        {
            // 不存在时自动创建，避免全新用户第一次开启自启动因 Run 键缺失而静默失败。
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key == null)
            {
                Log.Error("无法打开启动项。");
                return;
            }

            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return;
            }

            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path))
            {
                // 拿不到当前进程路径时只清理旧值，不写入错误的启动命令。
                Log.Info("未取到当前进程路径，已跳过写入开机自启动。");
                return;
            }

            key.SetValue(ValueName, $"\"{path}\"");
        }
        catch (Exception ex)
        {
            // 组策略 / HKCU 只读 / 安全软件拦截都不能把异常抛给调用方（Start 与设置页）。
            Log.Error("设置开机自启动失败：" + ex.Message);
        }
    }
}
