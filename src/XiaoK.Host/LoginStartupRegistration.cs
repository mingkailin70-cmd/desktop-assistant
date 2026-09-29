using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace XiaoK.Host;

internal static class LoginStartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "XiaoK.DesktopAssistant";
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;

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

    private static bool HasPackageIdentity()
    {
        uint bufferLength = 0;
        var result = GetCurrentPackageFullName(ref bufferLength, null);
        if (result == AppModelErrorNoPackage) return false;
        if (result is ErrorInsufficientBuffer or 0) return true;

        // Unknown package-identity state fails closed: do not write an autostart entry
        // that may be virtualized or unsupported by the current deployment type.
        throw new InvalidOperationException($"无法确认小K的打包状态（Windows 错误 {result}），未更改登录启动设置。");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetCurrentPackageFullName")]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, StringBuilder? packageFullName);
}
