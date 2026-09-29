using System.Security;
using Microsoft.Win32;

namespace XiaoK.Host;

internal static class LoginStartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "XiaoK.DesktopAssistant";
    public static bool IsSupported => !HasPackageIdentity();

    public static bool IsEnabled
    {
        get
        {
            if (!IsSupported) return false;
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return runKey?.GetValue(ValueName) is string;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        if (!IsSupported)
            throw new NotSupportedException("MSIX 登录启动任务尚未接入；当前版本未更改系统启动设置。");

        using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new UnauthorizedAccessException("无法打开当前用户的登录启动设置。");

        if (!enabled)
        {
            runKey.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
            throw new InvalidOperationException("无法确定小K程序路径，未创建登录启动项。");

        runKey.SetValue(ValueName, $"\"{executable}\" --background", RegistryValueKind.String);
    }

    private static bool HasPackageIdentity() => WindowsPackageIdentity.IsPresent;
}
