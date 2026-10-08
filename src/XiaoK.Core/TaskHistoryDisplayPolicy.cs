namespace XiaoK.Core;

/// <summary>为任务中心提供不含请求正文的目标范围、执行模式和下一步提示。</summary>
public static class TaskHistoryDisplayPolicy
{
    public static string TargetScope(string taskKind) => taskKind switch
    {
        "app" => "固定允许列表中的应用",
        "window" => "已打开且唯一匹配的允许列表窗口",
        "file" => "用户配置的本机搜索根",
        "file-copy" => "配置搜索根中的源文件与小K导出目录",
        "file-archive" => "配置搜索根中的源文件与小K导出目录",
        "file-move" => "配置搜索根中的源文件和同卷目标目录",
        "file-rename" => "配置搜索根中的单个文件",
        "file-delete" => "配置搜索根中的单个文件；批准后移入 Windows 回收站",
        "file-classify" => "用户指定的本机目录（只读）",
        "file-content-search" => "用户指定的文本查询（只返回路径与行号）",
        "web-read" => "用户提供的公开 HTTPS 页面",
        "web-download" => "用户提供的公开 HTTPS 文件与小K导出目录",
        "analyze" => "用户提供的单条消息（本地处理）",
        "draft" => "用户提供的单条消息（本地草拟）",
        "send" => "QQ K / 微信 L 联系人预览",
        "code-inspect" => "用户选定的项目快照（只读）",
        "code" => "用户选定项目的隔离工作区",
        _ => "本地处理；未指定外部目标"
    };

    public static string ExecutionMode(string taskKind) => taskKind switch
    {
        "app" or "window" => "前台交互：可能打开或切换窗口并改变焦点",
        "file-delete" => "后台执行；任务中心逐项确认，文件目标变化时停止",
        "send" => "需要检查预览；当前未接入真实发送适配器",
        "code" => "后台隔离执行；补丁写回或命令需人工审批",
        _ => "后台处理：不发送键鼠输入，不改变前台窗口"
    };

    public static string NextAction(TaskLifecycleState state) => state switch
    {
        TaskLifecycleState.Queued => "可取消；取消先于工作线程启动时，任务不会执行。",
        TaskLifecycleState.Planning => "可请求取消；工具动作尚未开始。",
        TaskLifecycleState.AwaitingApproval => "在任务中心待办中批准或拒绝；未批准的动作不会继续。",
        TaskLifecycleState.Running => "可请求停止；已经提交的系统副作用可能无法撤回。",
        TaskLifecycleState.Verifying => "等待独立核验完成；请勿重复执行同一任务。",
        TaskLifecycleState.Completed => "任务已完成；小K不会自动重放。需要重复时请提交新任务。",
        TaskLifecycleState.Failed => "任务失败；根据错误说明修正条件后可重新提交。",
        TaskLifecycleState.Cancelled => "任务已取消且不会自动重试；若任务已开始，请核对目标状态。",
        TaskLifecycleState.OutcomeUncertain => "先检查目标状态，再决定是否重新提交；小K不会自动重试。",
        _ => "状态未知；先检查目标状态，不要重复执行。"
    };

    public static string NextActionForCodeTask(string state, bool interrupted) =>
        NextAction(interrupted ? TaskLifecycleState.OutcomeUncertain : state switch
        {
            "planning" or "running" or "applying" => TaskLifecycleState.Running,
            "awaiting_approval" => TaskLifecycleState.AwaitingApproval,
            "completed" => TaskLifecycleState.Completed,
            "failed" => TaskLifecycleState.Failed,
            "cancelled" => TaskLifecycleState.Cancelled,
            "outcome_uncertain" => TaskLifecycleState.OutcomeUncertain,
            _ => TaskLifecycleState.OutcomeUncertain
        });
}
