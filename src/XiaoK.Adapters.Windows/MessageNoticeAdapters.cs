using XiaoK.Core;

namespace XiaoK.Adapters.Windows;

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
