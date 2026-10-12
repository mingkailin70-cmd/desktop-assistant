namespace XiaoK.Core;

/// <summary>Separates notifications already present at startup from notifications arriving during enumeration.</summary>
public static class NotificationStartupBaselinePolicy
{
    public static bool ShouldAddToBaseline(DateTimeOffset notificationCreatedAt,
        DateTimeOffset listeningStartedAt) => notificationCreatedAt < listeningStartedAt;
}
