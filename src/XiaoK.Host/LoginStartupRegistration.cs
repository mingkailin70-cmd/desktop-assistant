using System.Security;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace XiaoK.Host;

internal sealed record LoginStartupSnapshot(bool IsSupported, bool IsEnabled, string Status);

internal static class LoginStartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "XiaoK.DesktopAssistant";
    private const string PackagedTaskId = "XiaoKStartup";

    public static async Task<LoginStartupSnapshot> ReadAsync()
    {
        if (!WindowsPackageIdentity.IsPresent)
        {
            var enabled = ReadDevelopmentRegistration();
            return new(true, enabled, "仅为当前用户创建或移除登录启动项，不需要管理员权限。");
        }

        var task = await StartupTask.GetAsync(PackagedTaskId);
        return Describe(task.State);
    }

    public static async Task<LoginStartupSnapshot> SetEnabledAsync(bool enabled)
    {
        if (!WindowsPackageIdentity.IsPresent)
        {
            WriteDevelopmentRegistration(enabled);
            return await ReadAsync();
        }

        var task = await StartupTask.GetAsync(PackagedTaskId);
        if (enabled)
        {
            var state = await task.RequestEnableAsync();
            return Describe(state);
        }

        task.Disable();
        return await ReadAsync();
    }

    private static bool ReadDevelopmentRegistration()
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return runKey?.GetValue(ValueName) is string;
    }

    private static void WriteDevelopmentRegistration(bool enabled)
    {
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

    private static LoginStartupSnapshot Describe(StartupTaskState state) => state switch
    {
        StartupTaskState.Enabled => new(true, true, "Windows 登录启动任务已启用；可以在此关闭。"),
        StartupTaskState.EnabledByPolicy => new(false, true, "登录启动由 Windows 管理策略启用；小K不能更改此状态。"),
        StartupTaskState.Disabled => new(true, false, "MSIX 登录启动任务当前关闭；启用时 Windows 会显示系统确认。"),
        StartupTaskState.DisabledByUser => new(false, false, "Windows 已禁用此启动任务。请在任务管理器的“启动应用”中手动重新启用。"),
        StartupTaskState.DisabledByPolicy => new(false, false, "Windows 或组织策略禁止此启动任务。"),
        _ => new(false, false, "Windows 返回了未知的登录启动状态；为避免误改系统设置，当前不可更改。")
    };
}
