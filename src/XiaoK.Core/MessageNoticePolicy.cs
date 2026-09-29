namespace XiaoK.Core;

/// <summary>Filters publisher identity before reading message text; never persists notice bodies.</summary>
public sealed class MessageNoticePolicy
{
    private readonly HashSet<string> _wechatAppIds;
    private readonly HashSet<string> _qqAppIds;
    private readonly Dictionary<string, DateTimeOffset> _recent = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly TimeSpan _dedupeWindow;

    public MessageNoticePolicy(IEnumerable<string> wechatAppIds, IEnumerable<string> qqAppIds, TimeSpan? dedupeWindow = null)
    {
        _wechatAppIds = new(wechatAppIds.Where(IsConfiguredId), StringComparer.OrdinalIgnoreCase);
        _qqAppIds = new(qqAppIds.Where(IsConfiguredId), StringComparer.OrdinalIgnoreCase);
        _dedupeWindow = dedupeWindow ?? TimeSpan.FromMinutes(2);
    }

    public NoticeDecision Inspect(string? sourceAppId, string? conversationId, string? sender, bool? isPrivateConversation,
        string? visibleBody, DateTimeOffset receivedAtUtc, string deduplicationKey, bool userSessionUnlocked, bool permissionGranted)
    {
        var appId = ResolveApplication(sourceAppId);
        if (appId is null) return new(false, false, "已忽略非微信/QQ通知。");
        if (!permissionGranted) return new(false, false, "Windows 通知访问权限未授予或已撤销。");
        if (!userSessionUnlocked) return new(false, false, "Windows 会话已锁定，暂停消息分析。");

        lock (_gate)
        {
            var cutoff = receivedAtUtc - _dedupeWindow;
            foreach (var stale in _recent.Where(pair => pair.Value < cutoff).Select(pair => pair.Key).ToArray()) _recent.Remove(stale);
            if (string.IsNullOrWhiteSpace(deduplicationKey) || _recent.ContainsKey(deduplicationKey))
                return new(false, false, "重复通知已折叠。");
            _recent[deduplicationKey] = receivedAtUtc;
        }

        var notice = new MessageNotice(appId, sourceAppId!, conversationId, sender, isPrivateConversation == true,
            string.IsNullOrWhiteSpace(visibleBody) ? null : visibleBody, receivedAtUtc, deduplicationKey);
        if (isPrivateConversation != true)
            return new(true, false, "无法确定这是私聊，已保留为普通提示，不做自动分析。", notice with { Body = null });
        if (string.IsNullOrWhiteSpace(visibleBody))
            return new(true, false, "通知没有正文。请手动打开对应会话；小K不会切换窗口。", notice);
        return new(true, true, "收到微信/QQ私聊通知，正在本地分析可见正文。", notice);
    }

    private string? ResolveApplication(string? sourceAppId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppId)) return null;
        if (_wechatAppIds.Contains(sourceAppId)) return "wechat";
        if (_qqAppIds.Contains(sourceAppId)) return "qq";
        return null;
    }

    private static bool IsConfiguredId(string value) => !string.IsNullOrWhiteSpace(value) && value.Contains('!');
}
