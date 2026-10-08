using System.Windows;
using XiaoK.Core;

namespace XiaoK.Host;

public partial class ApprovalAuditWindow : Window
{
    public IReadOnlyList<ApprovalAuditDisplayRecord> Records { get; }

    internal ApprovalAuditWindow(IEnumerable<ApprovalAuditRecord> records)
    {
        Records = records.Select(record => new ApprovalAuditDisplayRecord(
            record.ActionId switch
            {
                ApprovalAuditCatalog.MessageSendAction => "消息发送预览",
                ApprovalAuditCatalog.CodeTaskAction => "隔离代码验证",
                ApprovalAuditCatalog.CodePatchApplyAction => "隔离补丁应用",
                ApprovalAuditCatalog.FileRecycleAction => "文件移入回收站",
                _ => "未知动作"
            },
            record.Outcome switch
            {
                ApprovalAuditCatalog.Confirmed => "已确认",
                ApprovalAuditCatalog.Declined => "已拒绝",
                ApprovalAuditCatalog.RunDotNetTests => "批准运行 .NET 验证",
                _ => "未知结果"
            },
            record.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")))
            .ToArray();
        InitializeComponent();
        DataContext = this;
    }
}

public sealed record ApprovalAuditDisplayRecord(string Action, string Outcome, string LocalTime);
