namespace XiaoK.Core;

/// <summary>把可能已提交副作用的工具异常保守地标记为待核对，避免用户误以为可直接重试。</summary>
public static class TaskFailureSafetyPolicy
{
    public const string CancelledOutcomeUncertainErrorCode = "TOOL_CANCEL_OUTCOME_UNCERTAIN";
    public const string TimedOutOutcomeUncertainErrorCode = "TOOL_TIMEOUT_OUTCOME_UNCERTAIN";
    public const string ExceptionOutcomeUncertainErrorCode = "TOOL_EXCEPTION_OUTCOME_UNCERTAIN";

    // Send is deliberately absent while the message tool only displays a preview; add it before wiring a sender.
    private static readonly HashSet<string> SideEffectCategories = new(StringComparer.Ordinal)
    {
        "app", "window", "file-create-text", "file-copy", "file-rename", "file-move", "file-archive", "file-delete", "web-download", "code"
    };

    public static bool RequiresManualVerification(string? taskCategory, bool routeStarted) =>
        routeStarted && taskCategory is not null && SideEffectCategories.Contains(taskCategory);

    public static bool IsUncertainOutcomeErrorCode(string? errorCode) => errorCode is
        CancelledOutcomeUncertainErrorCode or TimedOutOutcomeUncertainErrorCode or ExceptionOutcomeUncertainErrorCode;
}
