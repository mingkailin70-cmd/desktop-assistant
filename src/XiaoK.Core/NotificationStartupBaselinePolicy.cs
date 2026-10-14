namespace XiaoK.Core;

/// <summary>Separates notifications already present at startup from notifications arriving during enumeration.</summary>
public static class NotificationStartupBaselinePolicy
{
    public const int MaximumNotificationsToBaseline = 512;

    public static bool IsSafeBaselineCount(int notificationCount) =>
        notificationCount >= 0 && notificationCount <= MaximumNotificationsToBaseline;

    public static bool ShouldAddToBaseline(DateTimeOffset notificationCreatedAt,
        DateTimeOffset listeningStartedAt) => notificationCreatedAt < listeningStartedAt;
}
