using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using XiaoK.Adapters.Windows;

namespace XiaoK.Host;

/// <summary>
/// One-shot, headless source metadata probe. It never requests notification access,
/// subscribes to events, inspects toast visuals, or persists its result.
/// </summary>
internal static class WindowsNotificationSourceDiagnostic
{
    public static async Task<NotificationSourceDiagnosticReport> RunAsync()
    {
        if (!WindowsPackageIdentity.IsPresent)
            return NotificationSourceDiagnosticReport.Failure(
                "package_identity_missing", "当前进程没有 MSIX 身份；没有枚举通知。", 2);

        try
        {
            var listener = UserNotificationListener.Current;
            var access = listener.GetAccessStatus();
            if (access != UserNotificationListenerAccessStatus.Allowed)
                return NotificationSourceDiagnosticReport.Failure(
                    "permission_unavailable", $"Windows 通知访问状态为 {access}；没有请求权限或枚举通知。", 3,
                    access.ToString());

            var notifications = await listener.GetNotificationsAsync(NotificationKinds.Toast);
            var finalAccess = listener.GetAccessStatus();
            if (finalAccess != UserNotificationListenerAccessStatus.Allowed)
                return NotificationSourceDiagnosticReport.Failure(
                    "permission_revoked", "读取期间 Windows 通知访问权限已撤销；诊断结果已丢弃。", 3,
                    finalAccess.ToString());

            var inspected = notifications
                .OrderByDescending(notification => notification.CreationTime)
                .Take(NotificationPublisherDiagnosticPolicy.MaximumInspectedNotifications)
                .ToArray();
            var candidates = NotificationPublisherDiagnosticPolicy.Project(inspected.Select(notification =>
                ((string?)notification.AppInfo.DisplayInfo.DisplayName,
                    (string?)notification.AppInfo.AppUserModelId,
                    notification.CreationTime)));

            return new NotificationSourceDiagnosticReport(
                "ok",
                access.ToString(),
                inspected.Length,
                candidates,
                "仅读取候选应用显示名、AUMID和创建时间；未读取通知正文、未保存结果、未请求权限、未启动监听。",
                0);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            return NotificationSourceDiagnosticReport.Failure(
                "diagnostic_failed", "读取通知来源元数据失败；未读取正文或保存结果。", 1);
        }
    }
}

internal sealed record NotificationSourceDiagnosticReport(
    string Status,
    string? AccessStatus,
    int InspectedNotificationCount,
    IReadOnlyList<NotificationPublisherCandidate> Candidates,
    string Note,
    int ExitCode)
{
    public static NotificationSourceDiagnosticReport Failure(string status, string note, int exitCode,
        string? accessStatus = null) => new(status, accessStatus, 0, [], note, exitCode);
}
