using Microsoft.Win32;

namespace UsageTrackerWeb;

/// <summary>「登录后启动」注册表项（HKCU\...\Run 的「时迹Web」值）读写与旧 Native 项迁移。</summary>
internal static class StartupRegistration
{
    private const string RegistryRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "时迹Web";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, false);
            var value = key?.GetValue(StartupValueName) as string;
            return !string.IsNullOrWhiteSpace(value)
                && value.Contains("时迹Web.exe", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryRunKey);
            if (key is null) return;
            if (enabled)
            {
                key.SetValue(StartupValueName, $"\"{Environment.ProcessPath}\"");
            }
            else
            {
                key.DeleteValue(StartupValueName, false);
            }
        }
        catch
        {
        }
    }

    /// <summary>删除旧 Native 程序遗留的「时迹」自启项（一次性迁移）。</summary>
    public static void MigrateLegacyNativeEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, writable: true);
            key?.DeleteValue("时迹", false);
        }
        catch
        {
        }
    }
}
