namespace XiaoK.Core;

/// <summary>
/// 首版消息预览白名单。K/L 来自用户提供的截图，仅是显示名，不是稳定客户端账号标识。
/// 命中此策略只允许创建本地预览，不代表已核验联系人身份，也不构成实际外发授权。
/// </summary>
public static class MessageSendRecipientPolicy
{
    public const string WeChatRecipient = "L";
    public const string QqRecipient = "K";

    public static bool IsPreviewAllowed(string applicationId, string recipient) =>
        applicationId switch
        {
            "wechat" => string.Equals(recipient, WeChatRecipient, StringComparison.Ordinal),
            "qq" => string.Equals(recipient, QqRecipient, StringComparison.Ordinal),
            _ => false
        };
}
