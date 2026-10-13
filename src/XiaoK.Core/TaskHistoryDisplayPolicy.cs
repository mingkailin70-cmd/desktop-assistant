namespace XiaoK.Core;

/// <summary>为任务中心提供不含请求正文的目标范围、执行模式和下一步提示。</summary>
public static class TaskHistoryDisplayPolicy
{
    public const string CancellationRequestedLabel = "正在停止";
    public const string CancellationRequestedStep = "已请求安全停止；等待当前操作响应。";

    public static string ApprovalTabHeader(int pendingCount) => pendingCount <= 0
        ? "待办确认"
        : $"待办确认（{pendingCount}）";

    public static TaskLifecycleState EffectiveState(TaskLifecycleState storedState, bool hasPendingActionConfirmation) =>
        storedState == TaskLifecycleState.Running && hasPendingActionConfirmation
            ? TaskLifecycleState.AwaitingApproval
            : storedState;

    public static string TargetScope(string taskKind) => taskKind switch
    {
        "app" => "固定允许列表中的应用",
        "app-status" => "允许列表应用的进程/可见窗口状态（不含标题或正文）",
        "app-ui-snapshot" => "允许列表应用中唯一可见窗口的控件结构（只读）",
        "app-ui-wait-enabled" => "允许列表应用中指定的唯一可见控件（只等待状态，不点击）",
        "app-ui-set-text" => "允许列表应用中的唯一普通文本框（逐次确认完整文本）",
        "window" => "已打开且唯一匹配的允许列表窗口",
        "file" => "用户配置的本机搜索根",
        "file-create-text" or "file-create-csv" or "file-create-xlsx" or "file-create-docx" or "file-create-pptx" =>
            "固定小K导出目录中的新建文件（不覆盖现有文件）",
        "file-copy" => "配置搜索根中的源文件与小K导出目录",
        "file-archive" => "配置搜索根中的源文件与小K导出目录",
        "file-move" => "配置搜索根中的源文件和同卷目标目录",
        "file-rename" => "配置搜索根中的单个文件",
        "file-delete" => "配置搜索根中的单个文件；批准后移入 Windows 回收站",
        "file-classify" => "用户指定的本机目录（只读）",
        "file-content-search" => "用户指定的文本查询（只返回路径与行号）",
        "file-summary" => "用户指定的本机文档（只读，本地模型摘要）",
        "web-read" => "用户提供的公开 HTTPS 页面",
        "web-read-dynamic" => "用户提供的公开 HTTPS 页面（启用内联脚本，只读提取）",
        "browser-session-open" => "用户提供的 HTTPS 公网页面与临时隔离会话",
        "browser-session-navigate" => "当前隔离会话中由用户提供的 HTTPS 公网页面",
        "browser-session-snapshot" => "当前隔离会话中的页面可访问结构（只读）",
        "browser-session-follow-link" => "当前快照中唯一可见的同源 HTTPS 链接",
        "browser-session-click" => "当前快照中唯一可见的非提交按钮",
        "browser-session-fill" => "当前快照中唯一的普通 textbox/searchbox（拒绝敏感字段）",
        "browser-session-close" => "当前运行时生成的小K隔离网页会话",
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
        "app-status" => "后台只读进程/窗口信息；不读标题或控件值，不改变前台",
        "app-ui-snapshot" => "后台只读界面结构；不点击、不输入、不改变前台窗口",
        "app-ui-wait-enabled" => "后台等待控件状态；不点击、不输入、不改变前台窗口",
        "app-ui-set-text" => "后台写入唯一普通文本框；逐次确认完整文本，不模拟按键或改变前台",
        "file-create-text" or "file-create-csv" or "file-create-xlsx" or "file-create-docx" or "file-create-pptx" =>
            "后台在固定导出目录中新建文件；不覆盖现有文件或改变前台窗口",
        "file-delete" => "后台执行；任务中心逐项确认，文件目标变化时停止",
        "browser-session-open" or "browser-session-navigate" or "browser-session-follow-link" =>
            "后台使用无登录态隔离会话；不操作用户 Edge 或桌面前台",
        "browser-session-snapshot" => "后台读取隔离页面结构；不访问用户登录会话或改变前台",
        "browser-session-click" => "后台点击隔离网页中的非提交按钮；不发送页面网络请求或控制用户 Edge",
        "browser-session-fill" => "后台填写隔离网页普通文本框；不提交表单或控制用户 Edge",
        "browser-session-close" => "只关闭小K隔离会话；不影响用户浏览器窗口",
        "send" => "需要检查预览；当前未接入真实发送适配器",
        "code" => "后台隔离执行；补丁写回或命令需人工审批",
        _ => "后台处理：不发送键鼠输入，不改变前台窗口"
    };

    /// <summary>把固定错误码转换成不含任务正文或本机路径的恢复指引。</summary>
    public static string FailureDetail(string? errorCode) => errorCode switch
    {
        "APP_NOT_ALLOWLISTED" => "目标应用未登记在本机允许列表；小K没有启动、切换窗口或读取其界面。请检查应用设置后再决定是否允许。",
        "APP_LAUNCH_PRECHECK_FAILED" => "启动前无法安全核验目标窗口；小K没有发出启动请求。稍后可重试，也可手动查看应用状态。",
        "APP_LAUNCH_FAILED" => "未能启动允许列表中的目标应用；小K没有确认应用已打开。请检查本地程序是否存在及设置路径是否有效后重试。",
        "APP_LAUNCH_TARGET_AMBIGUOUS" or "WINDOW_TARGET_AMBIGUOUS" =>
            "发现多个匹配窗口；小K没有启动新窗口或切换焦点。请手动选择目标窗口后再提交。",
        "APP_WINDOW_AMBIGUOUS" =>
            "目标应用有多个可见窗口；小K没有读取或修改界面。请手动确认唯一窗口后再提交。",
        "APP_WINDOW_UNAVAILABLE" or "UIA_TARGET_CHANGED" =>
            "目标窗口在检查过程中已关闭或变化；小K停止读取或写入，没有切换焦点。请确认应用状态后再提交。",
        "WINDOW_ACTIVATION_FAILED" => "无法安全核验目标窗口；请手动确认当前前台和目标应用，再决定是否重新提交。",
        "WINDOW_ACTIVATION_DENIED" => "Windows 未允许或未确认目标窗口切换；请手动核对前台窗口后再决定下一步。",
        "APP_STATUS_UNAVAILABLE" => "无法安全读取允许列表应用的进程或窗口状态；没有读取标题或内容，也没有改变前台。稍后可重试或手动核对应用。",
        "UIA_AUTOMATION_ID_AMBIGUOUS" => "同一窗口中有多个相同控件标识；小K没有读取或写入。请指定唯一控件后再提交。",
        "UIA_CONDITION_NOT_MET" => "等待期间没有找到唯一且已启用的目标控件；没有点击或填写。请检查应用界面后再提交。",
        "UIA_TARGET_IN_USE" => "目标应用正处于前台；为避免干扰当前编辑，小K没有写入文本。稍后再提交。",
        "UIA_FOREGROUND_CHANGED_BEFORE_SET" => "写入前检测到前台窗口变化；小K没有写入文本。请确认目标应用状态后再提交。",
        "UIA_PROTECTED_APP_BLOCKED" => "该应用类型不允许使用通用后台填写；小K没有访问或修改界面。",
        "FILE_RECYCLE_NOT_CONFIRMED" => "Windows 未确认回收站操作，且原文件仍在原位置。先检查回收站和原文件状态，再决定是否重新提交。",
        "ARCHIVE_OUTPUT_UNVERIFIABLE" => "压缩文件未能通过独立核验，没有发布到目标目录。请检查空间和目录权限后再重新提交。",
        "FOREGROUND_REQUIRED" => "该操作需要桌面前台；小K没有执行，也没有排队。你可以在方便时切到前台再重新提交。",
        "WINDOW_NOT_FOUND" or "APP_WINDOW_NOT_FOUND" => "没有找到唯一且允许的目标窗口；小K没有切换焦点。请确认目标应用已打开，并提供更明确的目标。",
        "SEARCH_ROOT_UNAVAILABLE" or "INVALID_SEARCH_ROOT" => "没有可用的已配置搜索目录；小K没有读取文件。请在设置中检查搜索目录后重试。",
        "EXPORT_ROOT_UNAVAILABLE" => "小K导出目录不可用；没有写入文件。请在设置中检查导出目录后重试。",
        "BROWSER_ENGINE_UNAVAILABLE" => "隔离浏览器不可用；没有使用你已登录的 Edge 窗口。请检查本机 Edge 安装后重试。",
        "BROWSER_SESSION_NOT_FOUND" => "没有可用的隔离网页会话；请重新打开公开网页后再提交操作。",
        "BROWSER_SESSION_BUSY" => "已有一个隔离网页会话正在使用；先结束或关闭该会话，再重新提交。",
        "LOW_VRAM" or "LOW_SYSTEM_MEMORY" => "本机可用资源不足或无法确认；小K没有继续启动模型。关闭不需要的负载后可以重试。",
        "LOCAL_MODEL_OFFLINE" or "MODEL_RUNTIME_UNAVAILABLE" => "本地模型运行环境不可用；没有切换到云端。请检查本机模型与运行时配置后重试。",
        "LOCAL_MODEL_TIMEOUT" => "本地模型没有在时限内完成；没有调用云端服务。请缩小任务范围后重试。",
        "RESOURCE_BUSY" => "本地模型正忙；本次任务没有排队。稍后可以重新提交。",
        "SEND_ADAPTER_UNAVAILABLE" => "当前版本没有微信/QQ发送适配器；消息没有发送。请勿把这条任务当作已发送。",
        "STATE_WRITE_FAILED" => "无法保存任务状态；为避免状态不明，小K没有开始操作。请检查本机数据目录后重试。",
        "NO_CODE_FILES" => "所选项目中没有可读取的支持类型源文件；请确认项目位置和文件类型后重试。",
        "PROJECT_TOO_LARGE" => "所选项目超出当前单任务读取上限；请缩小项目范围后重试。",
        "NO_VALID_FILES_SELECTED" => "本地模型没有选出有效项目文件；原项目未修改。请缩小任务范围后重试。",
        _ => "任务失败。为保护隐私，历史记录不保存请求正文、模型回答或本机路径；请从小K当前反馈查看下一步。"
    };

    /// <summary>将明确报告为结果不确定的工具错误映射为安全、可执行且不含路径的核对步骤。</summary>
    public static string UncertainOutcomeDetail(string? errorCode) => errorCode switch
    {
        "APP_LAUNCH_OUTCOME_UNCERTAIN" => "应用启动请求已提交，但是否成功打开无法确认。先检查目标应用当前状态，再决定是否重新提交；小K不会自动重试。",
        "FILE_COPY_OUTCOME_UNCERTAIN" => "复制结果无法确认。先检查源文件和导出目录中的目标文件，再决定是否重新提交；小K不会自动重试。",
        "FILE_DOWNLOAD_OUTCOME_UNCERTAIN" => "下载结果无法确认。先检查导出目录中是否已有完整文件，再决定是否重新提交；小K不会自动重试。",
        "FILE_ARCHIVE_OUTCOME_UNCERTAIN" => "压缩结果无法确认。先检查源目录和导出目录中的压缩文件，再决定是否重新提交；小K不会自动重试。",
        "FILE_MOVE_OUTCOME_UNCERTAIN" => "移动结果无法确认。先检查源文件和目标目录中的实际位置，再决定是否重新提交；小K不会自动重试。",
        "FILE_RENAME_OUTCOME_UNCERTAIN" => "重命名结果无法确认。先检查当前目录中的原名称和新名称，再决定是否重新提交；小K不会自动重试。",
        "FILE_RECYCLE_OUTCOME_UNCERTAIN" => "回收站操作结果无法确认。先检查原文件和回收站，再决定是否重新提交；小K不会自动重试。",
        "TEXT_FILE_CREATE_OUTCOME_UNCERTAIN" or "DOCX_CREATE_OUTCOME_UNCERTAIN"
            or "PPTX_CREATE_OUTCOME_UNCERTAIN" or "XLSX_CREATE_OUTCOME_UNCERTAIN" =>
            "文件可能已创建，但独立核验未完成。先检查导出目录中的目标文件，再决定是否重新提交；小K不会自动重试。",
        "CODE_PATCH_OUTCOME_UNCERTAIN" => "补丁应用结果无法确认。先检查隔离工作区和补丁状态，再决定下一步；小K不会自动重放。",
        "UIA_VALUE_VERIFY_FAILED" or "UIA_FOREGROUND_CHANGED_AFTER_SET" =>
            "目标应用中的文本写入结果无法确认。先检查相关文本是否已完整或部分更新，再决定是否重新操作；小K不会自动重试。",
        _ => "操作结果无法确认，可能已经产生副作用。先核对相关应用、文件或隔离工作区的实际状态，再决定下一步；小K不会自动重试。"
    };

    public static bool CanCancel(TaskLifecycleState state, bool cancellationRequested = false) =>
        !cancellationRequested && IsCancellableState(state);

    public static string NextAction(TaskLifecycleState state, bool cancellationRequested = false)
    {
        if (cancellationRequested && IsCancellableState(state))
            return "已请求安全停止；请等待状态更新，不要重复提交。已经提交的系统副作用可能无法撤回。";

        return state switch
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
    }

    public static string CheckpointStep(TaskCheckpointStage stage, TaskLifecycleState state) => stage switch
    {
        TaskCheckpointStage.Accepted => "已排队；操作尚未开始",
        TaskCheckpointStage.Planning => "正在分析请求；具体操作尚未开始",
        TaskCheckpointStage.ActionStarted => "操作已开始；结果需要核验",
        TaskCheckpointStage.ApprovalPending => "等待用户确认；确认不会跨重启恢复",
        TaskCheckpointStage.Terminal => TaskStepForState(state),
        _ => TaskStepForState(state)
    };

    private static string TaskStepForState(TaskLifecycleState state) => state switch
    {
        TaskLifecycleState.Queued => "等待本机交互任务执行权",
        TaskLifecycleState.Planning => "正在分析请求并选择本地处理路径",
        TaskLifecycleState.Running => "正在执行本地任务步骤；结束后会显示结果或待办",
        TaskLifecycleState.Verifying => "正在核验任务结果",
        TaskLifecycleState.AwaitingApproval => "等待用户确认",
        TaskLifecycleState.Completed => "任务已完成",
        TaskLifecycleState.Failed => "任务失败",
        TaskLifecycleState.Cancelled => "任务已取消",
        TaskLifecycleState.OutcomeUncertain => "结果待核对；不会自动重试",
        _ => "状态未知；请先核对目标状态"
    };

    private static bool IsCancellableState(TaskLifecycleState state) => state is
        TaskLifecycleState.Queued or TaskLifecycleState.Planning or TaskLifecycleState.Running
        or TaskLifecycleState.Verifying or TaskLifecycleState.AwaitingApproval;

    public static string CodeTaskStateLabel(string state) => state switch
    {
        "planning" => "规划中",
        "running" => "生成中",
        "verifying" => "核验中",
        "awaiting_approval" => "等待审阅",
        "applying" => "应用中",
        "outcome_uncertain" => "结果待核对",
        "completed" => "已完成",
        "failed" => "失败",
        "cancelled" => "已取消",
        _ => "未知状态"
    };

    public static string NextActionForCodeTask(string state, bool interrupted) =>
        NextAction(interrupted ? TaskLifecycleState.OutcomeUncertain : state switch
        {
            "planning" or "running" or "applying" => TaskLifecycleState.Running,
            "verifying" => TaskLifecycleState.Verifying,
            "awaiting_approval" => TaskLifecycleState.AwaitingApproval,
            "completed" => TaskLifecycleState.Completed,
            "failed" => TaskLifecycleState.Failed,
            "cancelled" => TaskLifecycleState.Cancelled,
            "outcome_uncertain" => TaskLifecycleState.OutcomeUncertain,
            _ => TaskLifecycleState.OutcomeUncertain
        });
}
