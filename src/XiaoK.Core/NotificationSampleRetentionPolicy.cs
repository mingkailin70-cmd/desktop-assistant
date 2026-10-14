namespace XiaoK.Core;

/// <summary>Defines the hard lifetime for user-approved, in-memory notification samples.</summary>
public static class NotificationSampleRetentionPolicy
{
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromMinutes(15);

    public static bool HasExpired(TimeSpan elapsed) => elapsed >= MaximumDuration;
}
