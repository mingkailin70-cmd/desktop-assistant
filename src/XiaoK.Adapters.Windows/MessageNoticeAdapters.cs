using XiaoK.Core;

namespace XiaoK.Adapters.Windows;

public enum NotificationIdEnqueueResult { Added, AlreadyTracked, CapacityReached }

/// <summary>Bounded single-dispatcher queue that retains only Windows notification IDs, never toast content.</summary>
public sealed class BoundedNotificationIdQueue
{
    private readonly int _capacity;
    private readonly Queue<uint> _pending = [];
    private readonly HashSet<uint> _tracked = [];

    public BoundedNotificationIdQueue(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Count => _tracked.Count;

    public NotificationIdEnqueueResult TryEnqueue(uint notificationId)
    {
        if (_tracked.Contains(notificationId)) return NotificationIdEnqueueResult.AlreadyTracked;
        if (_tracked.Count >= _capacity) return NotificationIdEnqueueResult.CapacityReached;
        _tracked.Add(notificationId);
        _pending.Enqueue(notificationId);
        return NotificationIdEnqueueResult.Added;
    }

    public bool TryDequeue(out uint notificationId) => _pending.TryDequeue(out notificationId);

    /// <summary>Release a notification ID after processing; dequeued IDs remain tracked while in flight.</summary>
    public void Complete(uint notificationId) => _tracked.Remove(notificationId);

    public void Clear()
    {
        _pending.Clear();
        _tracked.Clear();
    }
}

public static class MessageNoticePublisherAssignments
{
    public static bool HasOverlap(IEnumerable<string> wechatPublisherIds, IEnumerable<string> qqPublisherIds)
    {
        ArgumentNullException.ThrowIfNull(wechatPublisherIds);
        ArgumentNullException.ThrowIfNull(qqPublisherIds);
        var wechatIds = new HashSet<string>(wechatPublisherIds.Where(id => !string.IsNullOrWhiteSpace(id)),
            StringComparer.OrdinalIgnoreCase);
        return qqPublisherIds.Any(wechatIds.Contains);
    }
}

public sealed record NotificationPublisherCandidate(
    string DisplayName,
    string AppUserModelId,
    int NotificationCount,
    DateTimeOffset LatestNotificationTimeUtc);

/// <summary>Projects a bounded, ephemeral source-identity diagnostic. No toast body or sender data is accepted here.</summary>
public static class NotificationPublisherDiagnosticPolicy
{
    public const int MaximumInspectedNotifications = 512;
    public const int MaximumCandidates = 20;
    private const int MaximumDisplayNameLength = 80;

    public static IReadOnlyList<NotificationPublisherCandidate> Project(
        IEnumerable<(string? DisplayName, string? AppUserModelId, DateTimeOffset CreationTimeUtc)> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var candidates = new Dictionary<string, (string DisplayName, int Count, DateTimeOffset LatestNotificationTimeUtc)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (displayName, appUserModelId, creationTimeUtc) in sources.Take(MaximumInspectedNotifications))
        {
            if (!IsLikelyTargetClientName(displayName) || !AppUserModelIdPolicy.IsValid(appUserModelId)) continue;

            var safeName = new string(displayName!.Where(character => !char.IsControl(character)).Take(MaximumDisplayNameLength).ToArray()).Trim();
            if (safeName.Length == 0) continue;
            if (candidates.TryGetValue(appUserModelId!, out var existing))
                candidates[appUserModelId!] = (existing.DisplayName, existing.Count + 1,
                    creationTimeUtc > existing.LatestNotificationTimeUtc ? creationTimeUtc : existing.LatestNotificationTimeUtc);
            else if (candidates.Count < MaximumCandidates)
                candidates.Add(appUserModelId!, (safeName, 1, creationTimeUtc));
        }

        return candidates.Select(pair => new NotificationPublisherCandidate(
                pair.Value.DisplayName, pair.Key, pair.Value.Count, pair.Value.LatestNotificationTimeUtc))
            .OrderBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.AppUserModelId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool IsLikelyTargetClientName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 512) return false;
        return displayName.Contains("微信", StringComparison.OrdinalIgnoreCase)
            || displayName.Contains("WeChat", StringComparison.OrdinalIgnoreCase)
            || displayName.Contains("Weixin", StringComparison.OrdinalIgnoreCase)
            || displayName.Contains("QQ", StringComparison.OrdinalIgnoreCase);
    }
}

public enum NoticeBodyAccessFailure { None, PermissionUnavailable, SessionLocked }

/// <summary>Rechecks volatile Windows authorization and lock state immediately before materializing a toast body.</summary>
public static class NoticeBodyReadGate
{
    public static string? ReadIfAllowed(Func<bool> permissionCheck, Func<bool> unlockedCheck,
        Func<string?> bodyReader, out NoticeBodyAccessFailure failure)
    {
        if (!Check(permissionCheck))
        {
            failure = NoticeBodyAccessFailure.PermissionUnavailable;
            return null;
        }
        if (!Check(unlockedCheck))
        {
            failure = NoticeBodyAccessFailure.SessionLocked;
            return null;
        }

        failure = NoticeBodyAccessFailure.None;
        return bodyReader();
    }

    private static bool Check(Func<bool> check)
    {
        try { return check(); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException) { return false; }
    }
}

/// <summary>Application-specific policy adapters. The packaged Windows listener must supply app identity, private-chat metadata and visible text.</summary>
public interface IMessageNoticeAdapter
{
    string ApplicationId { get; }
    NoticeDecision Inspect(string? sourceAppId, string? conversationId, string? sender, bool? isPrivate,
        Func<string?> visibleBodyReader, DateTimeOffset receivedAtUtc, string deduplicationKey, bool sessionUnlocked, bool permissionGranted);
}

public sealed class WeChatNoticeAdapter(MessageNoticePolicy policy) : IMessageNoticeAdapter
{
    public string ApplicationId => "wechat";
    public NoticeDecision Inspect(string? sourceAppId, string? conversationId, string? sender, bool? isPrivate,
        Func<string?> visibleBodyReader, DateTimeOffset receivedAtUtc, string deduplicationKey, bool sessionUnlocked, bool permissionGranted) =>
        policy.Inspect(ApplicationId, sourceAppId, conversationId, sender, isPrivate, visibleBodyReader, receivedAtUtc, deduplicationKey, sessionUnlocked, permissionGranted)
            is var result && (result.Notice?.ApplicationId == ApplicationId || !result.Accepted) ? result :
            new(false, false, "通知来源与微信适配器不匹配。");
}

public sealed class QQNoticeAdapter(MessageNoticePolicy policy) : IMessageNoticeAdapter
{
    public string ApplicationId => "qq";
    public NoticeDecision Inspect(string? sourceAppId, string? conversationId, string? sender, bool? isPrivate,
        Func<string?> visibleBodyReader, DateTimeOffset receivedAtUtc, string deduplicationKey, bool sessionUnlocked, bool permissionGranted) =>
        policy.Inspect(ApplicationId, sourceAppId, conversationId, sender, isPrivate, visibleBodyReader, receivedAtUtc, deduplicationKey, sessionUnlocked, permissionGranted)
            is var result && (result.Notice?.ApplicationId == ApplicationId || !result.Accepted) ? result :
            new(false, false, "通知来源与 QQ 适配器不匹配。");
}
