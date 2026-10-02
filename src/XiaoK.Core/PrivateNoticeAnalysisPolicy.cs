using System.Collections.Immutable;

namespace XiaoK.Core;

/// <summary>Builds analysis proposals only for fresh, explicitly verified private-message notices.</summary>
public static class PrivateNoticeAnalysisPolicy
{
    private static readonly TimeSpan MaximumAge = TimeSpan.FromHours(24);

    public static bool TryCreateProposal(MessageNotice notice, IEnumerable<string> allowedPublisherIds,
        DateTimeOffset nowUtc, out ToolProposal? proposal)
    {
        ArgumentNullException.ThrowIfNull(notice);
        ArgumentNullException.ThrowIfNull(allowedPublisherIds);
        proposal = null;

        var applicationId = notice.ApplicationId?.ToLowerInvariant();
        if (applicationId is not ("wechat" or "qq")
            || !AppUserModelIdPolicy.IsValid(notice.SourceAppId)
            || !allowedPublisherIds.Any(value => string.Equals(value, notice.SourceAppId, StringComparison.OrdinalIgnoreCase))
            || !notice.IsPrivateConversation
            || (string.IsNullOrWhiteSpace(notice.ConversationId) && string.IsNullOrWhiteSpace(notice.SenderDisplayName))
            || notice.ConversationId?.Length > 256 || notice.SenderDisplayName?.Length > 256
            || string.IsNullOrWhiteSpace(notice.Body) || notice.Body.Length > 20_000
            || notice.ReceivedAtUtc > nowUtc || notice.ReceivedAtUtc < nowUtc - MaximumAge
            || !IsSha256(notice.DeduplicationKey))
            return false;

        var arguments = ImmutableDictionary<string, string>.Empty
            .Add("application_id", applicationId)
            .Add("source_app_id", notice.SourceAppId)
            .Add("is_private_conversation", "true")
            .Add("conversation_id", notice.ConversationId ?? string.Empty)
            .Add("sender", notice.SenderDisplayName ?? string.Empty)
            .Add("body", notice.Body)
            .Add("received_at_utc", notice.ReceivedAtUtc.UtcDateTime.ToString("O"))
            .Add("deduplication_key", notice.DeduplicationKey);

        proposal = new ToolProposal("message.notice.analyze.v1", arguments, "verified-private-notice",
            ToolPrecondition.VerifiedPrivateNotice, ToolExpectedOutcome.LocalMessageAnalysis);
        return true;
    }

    private static bool IsSha256(string? value) => value is { Length: 64 }
        && value.All(Uri.IsHexDigit);
}
