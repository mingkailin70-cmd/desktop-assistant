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
        "app", "window", "app-ui-set-text", "file-create-text", "file-create-csv", "file-create-xlsx", "file-create-docx", "file-create-pptx", "file-copy", "file-rename", "file-move", "file-archive", "file-delete", "web-download",
        // These can mutate the isolated page's in-memory state even though outbound requests and form submits are blocked.
        "browser-session-click", "browser-session-fill", "code"
    };

    // Known routes that do not commit a persistent user-visible side effect. Unknown categories fail closed so a
    // newly added route cannot silently turn a cancelled or faulted operation into an apparently safe retry.
    private static readonly HashSet<string> ReadOnlyCategories = new(StringComparer.Ordinal)
    {
        "app-status", "app-ui-snapshot", "app-ui-wait-enabled", "file", "file-content-search", "file-summary",
        "file-classify", "web-read", "web-read-dynamic", "browser-session-open", "browser-session-navigate",
        "browser-session-snapshot", "browser-session-follow-link",
        "browser-session-close", "analyze", "draft", "send", "code-inspect", "chat"
    };

    public static bool RequiresManualVerification(string? taskCategory, bool routeStarted) =>
        routeStarted && (taskCategory is null || SideEffectCategories.Contains(taskCategory)
            || !ReadOnlyCategories.Contains(taskCategory));

    public static bool IsUncertainOutcomeErrorCode(string? errorCode) => errorCode is
        CancelledOutcomeUncertainErrorCode or TimedOutOutcomeUncertainErrorCode or ExceptionOutcomeUncertainErrorCode
        or "APP_LAUNCH_OUTCOME_UNCERTAIN"
        or "FILE_COPY_OUTCOME_UNCERTAIN" or "FILE_DOWNLOAD_OUTCOME_UNCERTAIN"
        or "FILE_ARCHIVE_OUTCOME_UNCERTAIN" or "FILE_MOVE_OUTCOME_UNCERTAIN"
        or "FILE_RENAME_OUTCOME_UNCERTAIN" or "FILE_RECYCLE_OUTCOME_UNCERTAIN"
        or "TEXT_FILE_CREATE_OUTCOME_UNCERTAIN" or "DOCX_CREATE_OUTCOME_UNCERTAIN"
        or "PPTX_CREATE_OUTCOME_UNCERTAIN" or "XLSX_CREATE_OUTCOME_UNCERTAIN"
        or "CODE_PATCH_OUTCOME_UNCERTAIN" or "UIA_VALUE_VERIFY_FAILED" or "UIA_FOREGROUND_CHANGED_AFTER_SET";
}
