namespace XiaoK.Core;

public static class MessageSendIntentResolver
{
    private static readonly (string Prefix, string ApplicationId)[] Prefixes =
    [
        ("发送微信给", "wechat"),
        ("发微信给", "wechat"),
        ("发送QQ给", "qq"),
        ("发QQ给", "qq")
    ];

    public static bool TryResolve(string request, out MessageSendIntent? intent, out string? errorCode)
    {
        intent = null;
        errorCode = "SEND_FORMAT_INVALID";
        if (string.IsNullOrWhiteSpace(request)) return false;

        foreach (var (prefix, applicationId) in Prefixes)
        {
            if (!request.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var payload = request[prefix.Length..].TrimStart();
            var separator = payload.IndexOfAny(['：', ':']);
            if (separator <= 0 || separator == payload.Length - 1) return false;

            var recipient = payload[..separator].Trim();
            var text = payload[(separator + 1)..].Trim();
            if (recipient.Length is 0 or > 256 || recipient.Any(char.IsControl)
                || text.Length is 0 or > 20_000 || text.Contains('\0'))
                return false;

            if (!MessageSendRecipientPolicy.IsAllowed(applicationId, recipient))
            {
                errorCode = "SEND_RECIPIENT_NOT_ALLOWED";
                return false;
            }

            if (ContainsAttachmentDirective(text))
            {
                errorCode = "SEND_ATTACHMENTS_UNSUPPORTED";
                return false;
            }

            intent = new MessageSendIntent(applicationId, recipient, text);
            errorCode = null;
            return true;
        }

        return false;
    }

    private static bool ContainsAttachmentDirective(string text) =>
        text.Contains("；附件：", StringComparison.OrdinalIgnoreCase)
        || text.Contains(";附件:", StringComparison.OrdinalIgnoreCase)
        || text.Contains("；附件=", StringComparison.OrdinalIgnoreCase)
        || text.Contains(";附件=", StringComparison.OrdinalIgnoreCase);
}
