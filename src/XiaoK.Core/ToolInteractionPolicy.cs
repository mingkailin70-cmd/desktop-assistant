namespace XiaoK.Core;

public enum ToolInteractionMode { Unknown, Background, Foreground }
public enum ToolExecutionAccess { BackgroundOnly, ExplicitUserInteraction }

/// <summary>按真实适配器能力登记交互模式；模型不能自行声明支持后台。</summary>
public static class ToolInteractionPolicy
{
    public static ToolInteractionMode GetMode(string toolId) => toolId switch
    {
        "file.search.v1" or "file.search.content.v1" or "file.copy.v1" or "file.rename.v1" or "file.move.v1"
            or "file.delete.recycle-bin.v1"
            or "file.classify.preview.v1" or "file.archive.single.v1"
            or "browser.read.public.v1" or "browser.download.public.v1"
            or "code.inspect.v1" or "code.task.create.v1" or "message.analyze.v1"
            or "message.notice.analyze.v1" or "message.draft.v1" or "message.send.v1" => ToolInteractionMode.Background,
        // 启动程序和切换窗口会改变用户当前桌面状态，只能由明确用户交互触发。
        "app.launch.v1" or "window.activate.v1" => ToolInteractionMode.Foreground,
        _ => ToolInteractionMode.Unknown
    };

    public static ToolResult? Check(string toolId, ToolExecutionAccess access)
    {
        if (access is not (ToolExecutionAccess.BackgroundOnly or ToolExecutionAccess.ExplicitUserInteraction))
            return new(false, "执行上下文无效；未执行。", "INVALID_EXECUTION_CONTEXT");
        return GetMode(toolId) switch
        {
            ToolInteractionMode.Unknown => new(false, "未知工具已拒绝。", "UNKNOWN_TOOL"),
            ToolInteractionMode.Foreground when access == ToolExecutionAccess.BackgroundOnly =>
                new(false, "该工具当前需要前台交互；为避免打断你的工作，本次未执行。", "FOREGROUND_REQUIRED"),
            _ => null
        };
    }
}
