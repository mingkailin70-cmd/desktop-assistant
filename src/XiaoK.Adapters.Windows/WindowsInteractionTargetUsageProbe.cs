using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using XiaoK.Core;

namespace XiaoK.Adapters.Windows;

public sealed partial class WindowsDesktopTools : IInteractionTargetUsageProbe
{
    private const string ApplicationFrameHostExecutable = "ApplicationFrameHost.exe";

    /// <summary>
    /// Read-only foreground ownership check. It does not inspect window titles/content or activate a window.
    /// Unknown or packaged-hosted foreground ownership fails closed for target UI state changes.
    /// </summary>
    public ValueTask<InteractionTargetUsage> GetUsageAsync(string targetId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(targetId)
            || !_apps.TryGetValue(targetId, out var target))
            return ValueTask.FromResult(InteractionTargetUsage.Unknown);

        try
        {
            var targetPath = Path.GetFullPath(target.Executable);
            if (IsWindowsAppsPath(targetPath))
                return ValueTask.FromResult(InteractionTargetUsage.Unknown);

            var foregroundWindow = GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero
                || GetWindowThreadProcessId(foregroundWindow, out var foregroundProcessId) == 0
                || foregroundProcessId == 0)
                return ValueTask.FromResult(InteractionTargetUsage.Unknown);

            using var foregroundProcess = Process.GetProcessById(unchecked((int)foregroundProcessId));
            using var currentProcess = Process.GetCurrentProcess();
            if (foregroundProcess.SessionId != currentProcess.SessionId)
                return ValueTask.FromResult(InteractionTargetUsage.Unknown);

            var foregroundPath = foregroundProcess.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(foregroundPath)
                || Path.GetFileName(foregroundPath).Equals(ApplicationFrameHostExecutable, StringComparison.OrdinalIgnoreCase)
                || IsWindowsAppsPath(foregroundPath))
                return ValueTask.FromResult(InteractionTargetUsage.Unknown);

            var normalizedForegroundPath = Path.GetFullPath(foregroundPath);
            return ValueTask.FromResult(string.Equals(targetPath, normalizedForegroundPath, StringComparison.OrdinalIgnoreCase)
                ? InteractionTargetUsage.InUse
                : InteractionTargetUsage.NotInUse);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or ArgumentException
            or SecurityException or UnauthorizedAccessException or IOException or NotSupportedException)
        {
            return ValueTask.FromResult(InteractionTargetUsage.Unknown);
        }
    }

    private static bool IsWindowsAppsPath(string path)
    {
        var segments = Path.GetFullPath(path).Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(static segment => segment.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase));
    }

}
