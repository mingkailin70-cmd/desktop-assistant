using System.Security.Cryptography;
using System.Text;

namespace XiaoK.Core;

/// <summary>Filters publisher identity before invoking the lazy body reader; never persists notice bodies.</summary>
public sealed class MessageNoticePolicy
{
    private const int MaximumRememberedDedupeKeys = 4096;
    private const int MaximumRateLimitedConversations = 512;
    private readonly HashSet<string> _wechatAppIds;
    private readonly HashSet<string> _qqAppIds;
    private readonly Dictionary<string, DateTimeOffset> _recent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _conversationRates = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly TimeSpan _dedupeWindow;
    private readonly TimeSpan _rateWindow;
    private readonly int _maximumPrivateNoticesPerWindow;

    public MessageNoticePolicy(IEnumerable<string> wechatAppIds, IEnumerable<string> qqAppIds, TimeSpan? dedupeWindow = null,
        int maximumPrivateNoticesPerWindow = 10, TimeSpan? rateWindow = null)
    {
        _wechatAppIds = new((wechatAppIds ?? []).Where(IsConfiguredId), StringComparer.OrdinalIgnoreCase);
        _qqAppIds = new((qqAppIds ?? []).Where(IsConfiguredId), StringComparer.OrdinalIgnoreCase);
        _dedupeWindow = dedupeWindow ?? TimeSpan.FromMinutes(2);
        _rateWindow = rateWindow ?? TimeSpan.FromMinutes(1);
        if (_dedupeWindow <= TimeSpan.Zero || _dedupeWindow > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(dedupeWindow), "去重窗口必须大于零且不超过一天。");
        if (_rateWindow <= TimeSpan.Zero || _rateWindow > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(rateWindow), "限速窗口必须大于零且不超过一小时。");
        if (maximumPrivateNoticesPerWindow <= 0 || maximumPrivateNoticesPerWindow > MaximumRememberedDedupeKeys)
            throw new ArgumentOutOfRangeException(nameof(maximumPrivateNoticesPerWindow), "会话限速数量超出支持范围。");
        _maximumPrivateNoticesPerWindow = maximumPrivateNoticesPerWindow;
    }

    public NoticeDecision Inspect(string expectedApplicationId, string? sourceAppId, string? conversationId, string? sender, bool? isPrivateConversation,
        Func<string?> visibleBodyReader, DateTimeOffset receivedAtUtc, string deduplicationKey,
        bool userSessionUnlocked, bool permissionGranted)
    {
        var appId = ResolveApplication(sourceAppId);
        if (appId is null) return new(false, false, "已忽略非微信/QQ通知。");
        if (!appId.Equals(expectedApplicationId, StringComparison.OrdinalIgnoreCase))
            return new(false, false, "通知来源与当前应用适配器不匹配。");
        if (!permissionGranted) return new(false, false, "Windows 通知访问权限未授予或已撤销。");
        if (!userSessionUnlocked) return new(false, false, "Windows 会话已锁定，暂停消息分析。");
        if (string.IsNullOrWhiteSpace(deduplicationKey) || deduplicationKey.Length > 512)
            return new(false, false, "通知缺少有效去重标识，已跳过自动分析。");

        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var cutoff = now - _dedupeWindow;
            foreach (var stale in _recent.Where(pair => pair.Value < cutoff).Select(pair => pair.Key).ToArray()) _recent.Remove(stale);
            var dedupeId = HashKey($"{appId}\0{deduplicationKey}");
            if (_recent.ContainsKey(dedupeId))
                return new(false, false, "重复通知已折叠。");
            if (_recent.Count >= MaximumRememberedDedupeKeys)
                return new(true, false, "通知过于频繁，已暂缓自动处理；请手动查看会话。");
            _recent[dedupeId] = now;
        }

        var safeConversationId = LimitMetadata(conversationId, 256);
        var safeSender = LimitMetadata(sender, 256);
        var notice = new MessageNotice(appId, sourceAppId!, safeConversationId, safeSender, isPrivateConversation == true,
            null, receivedAtUtc, HashKey($"{appId}\0{deduplicationKey}"));
        if (isPrivateConversation != true)
            return new(true, false, "无法确定这是私聊，已保留为普通提示，不做自动分析。", notice);

        if (visibleBodyReader is null)
            return new(false, false, "通知正文读取器无效，已跳过自动分析。");
        string? visibleBody;
        try { visibleBody = visibleBodyReader(); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException)
        { return new(true, false, "无法读取通知正文，已跳过自动分析。", notice); }

        if (string.IsNullOrWhiteSpace(visibleBody))
            return new(true, false, "通知没有正文。请手动打开对应会话；小K不会切换窗口。", notice);
        if (visibleBody.Length > 20_000)
            return new(true, false, "通知正文超过本地处理长度上限，已跳过自动分析。", notice with { Body = null });
        notice = notice with { Body = visibleBody };

        var rateIdentity = safeConversationId ?? safeSender ?? "unknown-conversation";
        var rateKey = HashKey($"{appId}\0{rateIdentity}");
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var cutoff = now - _rateWindow;
            foreach (var key in _conversationRates.Keys.ToArray())
            {
                var queue = _conversationRates[key];
                while (queue.Count > 0 && queue.Peek() < cutoff) queue.Dequeue();
                if (queue.Count == 0) _conversationRates.Remove(key);
            }

            if (!_conversationRates.TryGetValue(rateKey, out var timestamps))
            {
                if (_conversationRates.Count >= MaximumRateLimitedConversations)
                    return new(true, false, "通知涉及的会话过多，已暂缓自动分析；请手动查看会话。", notice with { Body = null });
                timestamps = new Queue<DateTimeOffset>();
                _conversationRates.Add(rateKey, timestamps);
            }
            if (timestamps.Count >= _maximumPrivateNoticesPerWindow)
                return new(true, false, "该会话通知过于频繁，已暂缓自动分析；请手动查看会话。", notice with { Body = null });
            timestamps.Enqueue(now);
        }

        return new(true, true, "收到微信/QQ私聊通知，正在本地分析可见正文。", notice);
    }

    private string? ResolveApplication(string? sourceAppId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppId)) return null;
        if (_wechatAppIds.Contains(sourceAppId)) return "wechat";
        if (_qqAppIds.Contains(sourceAppId)) return "qq";
        return null;
    }

    private static bool IsConfiguredId(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && value.Contains('!');

    private static string? LimitMetadata(string? value, int maximumLength) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ? null : value;

    private static string HashKey(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
