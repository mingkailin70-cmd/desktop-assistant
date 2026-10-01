using XiaoK.Core;

namespace XiaoK.Adapters.Windows;

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
