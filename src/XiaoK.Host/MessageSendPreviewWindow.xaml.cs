using System.Windows;
using XiaoK.Core;

namespace XiaoK.Host;

public partial class MessageSendPreviewWindow : Window
{
    public MessageSendPreviewWindow(MessageSendPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        InitializeComponent();
        ApplicationText.Text = preview.ApplicationId switch
        {
            "wechat" => "微信",
            "qq" => "QQ",
            _ => "未知应用"
        };
        RecipientText.Text = preview.Recipient;
        BodyText.Text = preview.Text;
        AttachmentsText.Text = preview.Attachments.Count == 0
            ? "无"
            : string.Join(Environment.NewLine, preview.Attachments.Select(attachment =>
                $"{attachment.DisplayName} · {attachment.SizeBytes} 字节 · SHA-256 {attachment.Sha256}"));
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
