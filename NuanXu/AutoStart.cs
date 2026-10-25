using System.Diagnostics;
using Microsoft.Win32;

namespace NuanXu;

/// <summary>
/// 开机自启：只写 HKCU 的 Run 键，不需要管理员权限。
///
/// 设计要点：**注册表是唯一真相**，settings.json 里不存这个开关。
/// 存两份必然有一天对不上（用户手工删了注册表项、或安全软件清理过启动项），
/// 那时界面显示「已开启」但开机根本不启动，比没有这个开关更糟。
/// </summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NuanXu";

    // 任务管理器「启动」页的禁用**不改** Run 值，而是往这里写一条二进制标记。
    // 不清理它，用户会看到我们勾着「已开启」、实际却被系统拦掉。
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>当前 exe 的完整路径。自包含单目录发布下两者一致，ProcessPath 更通用。</summary>
    private static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;

    /// <summary>
    /// 注册表里应该躺着的命令行。**必须带引号**：安装目录是
    /// %LOCALAPPDATA%\Programs\暖序，含空格与中文，不加引号会被 CreateProcess 在空格处截断。
    /// </summary>
    public static string Expected => $"\"{ExePath}\" --autostart";

    /// <summary>是否已登记自启。注意这里只判断「值存在」，路径是否过期交给 RepairIfStale。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return false;
        }
    }

    /// <summary>开启自启。失败（安全软件、组策略禁写 Run 键）返回 false，由调用方回滚勾选并提示。</summary>
    public static bool Enable()
    {
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true))
            {
                if (key is null) return false;
                key.SetValue(ValueName, Expected, RegistryValueKind.String);
            }
            ClearStartupApproved();
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return false;
        }
    }

    /// <summary>关闭自启。值本来就不存在也算成功（幂等）。</summary>
    public static bool Disable()
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
            ClearStartupApproved();
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return false;
        }
    }

    /// <summary>
    /// 路径漂移修复：用户把程序搬到别的目录、或从 per-user 改装到 Program Files 之后，
    /// 注册表里还是旧路径，开机会启动旧版本（甚至启动失败）。
    /// 这是**静默修复**——搬目录是正常操作，弹框只会让人莫名其妙。
    /// 安装器在 ssPostInstall 里也会修一次，这里是第二道防线（覆盖手工搬目录）。
    /// </summary>
    public static void RepairIfStale()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key?.GetValue(ValueName) is not string current || current.Length == 0) return;
            if (string.Equals(current, Expected, StringComparison.OrdinalIgnoreCase)) return;
            Enable();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    /// <summary>尽力而为：键可能不存在、也可能没有这个值，失败无所谓。</summary>
    private static void ClearStartupApproved()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }
}
