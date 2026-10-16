using System.Collections.Immutable;
using System.Text.Json;

namespace XiaoK.Core;

public enum TaskLifecycleState { Queued, Planning, AwaitingApproval, Running, Verifying, Completed, Failed, Cancelled, OutcomeUncertain }

/// <summary>粗粒度的持久任务检查点；只记录固定阶段，不记录请求、目标路径或模型文本。</summary>
public enum TaskCheckpointStage
{
    None = 0,
    Accepted = 1,
    Planning = 2,
    ActionStarted = 3,
    ApprovalPending = 4,
    Terminal = 5
}

public sealed record TaskRecord(Guid Id, string Kind, string Summary, TaskLifecycleState Status, DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc, string? Result = null, string? ErrorCode = null, Guid? HostSessionId = null,
    TaskCheckpointStage CheckpointStage = TaskCheckpointStage.None);
public sealed record ApprovalAuditRecord(Guid Id, string ActionId, string Outcome, DateTimeOffset CreatedAtUtc);

[Flags]
public enum ToolPrecondition
{
    None = 0,
    ApplicationAllowlisted = 1,
    ExistingWindow = 2,
    ConfiguredSearchRoot = 4,
    UserProvidedSingleMessage = 8,
    CompleteMessagePreview = 16,
    ConfiguredProjectAndIsolatedWorkspace = 32,
    VerifiedPrivateNotice = 64,
    ConfiguredFileExportRoot = 128,
    UserProvidedPublicWebPageUrl = 256,
    ConfiguredClassificationDirectory = 512,
    ConfiguredMoveDestination = 1024,
    UserProvidedPublicFileUrl = 2048,
    UserProvidedDynamicPublicWebPageUrl = 4096,
    UserRequestedIsolatedBrowserSession = 8192
}

public enum ToolExpectedOutcome
{
    None = 0,
    ApplicationWindowVisible = 1,
    TargetWindowInForeground = 2,
    MatchingFilesListed = 3,
    LocalMessageAnalysis = 4,
    ReplyDraftOnly = 5,
    PreviewConfirmedBeforeSend = 6,
    ReviewablePatchCreated = 7,
    CodeExplanationReturned = 8,
    MessageSendPreviewShown = 9,
    FileCopiedToConfiguredExport = 10,
    FileRenamedInConfiguredSearchRoot = 11,
    PublicWebPageSnapshotReturned = 12,
    FileClassificationPreviewReturned = 13,
    FileMovedWithinConfiguredSearchRoots = 14,
    PublicFileDownloadedToConfiguredExport = 15,
    FileArchivedToConfiguredExport = 16,
    MatchingFileContentLocationsListed = 17,
    FileSentToRecycleBin = 18,
    DynamicPublicWebPageSnapshotReturned = 19,
    LocalTextFileRead = 20,
    LocalDocumentTextExtracted = 21,
    BrowserSessionOpened = 22,
    BrowserSessionSnapshotReturned = 23,
    BrowserControlActionCompleted = 24,
    BrowserSessionClosed = 25,
    BrowserSessionNavigated = 26,
    TextFileCreatedInConfiguredExport = 27,
    ApplicationStatusObserved = 28,
    ApplicationUiSnapshotReturned = 29,
    ApplicationUiConditionObserved = 30,
    CsvFileCreatedInConfiguredExport = 31,
    XlsxWorkbookCreatedInConfiguredExport = 32,
    DocxDocumentCreatedInConfiguredExport = 33,
    PptxPresentationCreatedInConfiguredExport = 34,
    ApplicationUiValueSet = 35,
    ApplicationUiActionInvoked = 36,
    ApplicationUiToggleStateSet = 37
}

public static class ApprovalAuditCatalog
{
    public const string MessageSendAction = "message.send.v1";
    public const string CodeTaskAction = "code.task.create.v1";
    public const string CodePatchApplyAction = "code.patch.apply.v1";
    public const string FileRecycleAction = "file.delete.recycle-bin.v1";
    public const string ApplicationUiTextSetAction = "app.ui.set-text.v1";
    public const string ApplicationUiForegroundTextSetAction = "app.ui.set-text-foreground.v1";
    public const string ApplicationUiButtonInvokeAction = "app.ui.invoke-button.v1";
    public const string ApplicationUiForegroundToggleSetAction = "app.ui.set-toggle-foreground.v1";
    public const string Confirmed = "confirmed";
    public const string Declined = "declined";
    public const string RunDotNetTests = "run_dotnet_tests";
}

public sealed record ToolProposal(string ToolId, ImmutableDictionary<string, string> Arguments, string Target,
    ToolPrecondition Preconditions, ToolExpectedOutcome ExpectedOutcome);
public sealed record ToolResult(bool Success, string Summary, string? ErrorCode = null, string? Data = null, TaskLifecycleState? FinalState = null);
public sealed record MessageNotice(string ApplicationId, string SourceAppId, string? ConversationId, string? SenderDisplayName,
    bool IsPrivateConversation, string? Body, DateTimeOffset ReceivedAtUtc, string DeduplicationKey);
public sealed record NoticeDecision(bool Accepted, bool AnalyzeBody, string UserMessage, MessageNotice? Notice = null);
public sealed record PrivateNoticeAnalysisResult(string ApplicationId, bool Success, string Text);
public sealed record MessageSendIntent(string ApplicationId, string Recipient, string Text);
public sealed record MessageAttachmentPreview(string DisplayName, long SizeBytes, string Sha256);
public sealed record MessageSendPreview(string ApplicationId, string Recipient, string Text,
    IReadOnlyList<MessageAttachmentPreview> Attachments);

public interface IPublicWebPageReader
{
    Task<ToolResult> ReadPageAsync(string url, CancellationToken cancellationToken);
}

public interface IDynamicPublicWebPageReader
{
    Task<ToolResult> ReadDynamicPageAsync(string url, CancellationToken cancellationToken);
}

/// <summary>Performs bounded, transient read-only checks against an explicitly selected allowlisted app UI.</summary>
public interface IReadOnlyUiAutomationSnapshotReader
{
    Task<ToolResult> ReadSnapshotAsync(string applicationId, CancellationToken cancellationToken);
    Task<ToolResult> ReadScopedSnapshotAsync(string applicationId, string automationId, string rootControlType,
        CancellationToken cancellationToken);
    Task<ToolResult> WaitForEnabledControlAsync(string applicationId, string automationId,
        CancellationToken cancellationToken);
}

/// <summary>Writes an explicitly confirmed value to one unique non-password edit control.</summary>
public interface IBackgroundUiAutomationTextSetter
{
    Task<ToolResult> SetTextAsync(string applicationId, string automationId, string value,
        CancellationToken cancellationToken);

    Task<ToolResult> SetTextInForegroundAsync(string applicationId, string automationId, string value,
        CancellationToken cancellationToken) => Task.FromResult(new ToolResult(false,
            "前台文本填写器未配置；没有访问或修改应用界面。", "UIA_FOREGROUND_TEXT_SETTER_UNAVAILABLE"));
}

/// <summary>Invokes one unique visible button in an allowlisted non-sensitive app after explicit approval.</summary>
public interface IBackgroundUiAutomationButtonInvoker
{
    Task<ToolResult> InvokeButtonAsync(string applicationId, string automationId,
        CancellationToken cancellationToken);
}

/// <summary>Sets one uniquely identified ordinary checkbox to an explicit state in the user-focused foreground app.</summary>
public interface IBackgroundUiAutomationToggleSetter
{
    Task<ToolResult> SetToggleStateInForegroundAsync(string applicationId, string automationId, string state,
        CancellationToken cancellationToken) => Task.FromResult(new ToolResult(false,
            "前台复选框设置器未配置；没有访问或修改应用界面。", "UIA_FOREGROUND_TOGGLE_SETTER_UNAVAILABLE"));
}

/// <summary>短时、无用户浏览器登录态的受限网页会话；状态只保留在进程内存中。</summary>
public interface IIsolatedBrowserSessionManager : IAsyncDisposable
{
    Task<ToolResult> OpenAsync(string url, CancellationToken cancellationToken);
    Task<ToolResult> NavigateAsync(string sessionId, string snapshotId, string url, CancellationToken cancellationToken);
    Task<ToolResult> SnapshotAsync(string sessionId, CancellationToken cancellationToken);
    Task<ToolResult> FollowLinkAsync(string sessionId, string snapshotId, string accessibleName,
        CancellationToken cancellationToken);
    Task<ToolResult> ClickButtonAsync(string sessionId, string snapshotId, string accessibleName,
        CancellationToken cancellationToken);
    Task<ToolResult> FillTextAsync(string sessionId, string snapshotId, string role, string accessibleName,
        string value, CancellationToken cancellationToken);
    Task<ToolResult> CloseAsync(string sessionId, CancellationToken cancellationToken);
}

public sealed record PublicFileDownloadResult(bool Success, string Summary, string? ErrorCode = null,
    string? FileName = null, string? MediaType = null, byte[]? Content = null, string? Sha256 = null);

public interface IPublicFileDownloader
{
    Task<PublicFileDownloadResult> DownloadAsync(string url, CancellationToken cancellationToken);
}

public interface ITaskStore
{
    Task SaveAsync(TaskRecord task, CancellationToken cancellationToken);
    Task<IReadOnlyList<TaskRecord>> GetRecentAsync(int count, CancellationToken cancellationToken);
}
public interface IInferenceClient
{
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken);

    Task<string> CompleteAsync(string systemPrompt, string userPrompt, InferenceRequestOptions options,
        CancellationToken cancellationToken) => CompleteAsync(systemPrompt, userPrompt, cancellationToken);
}

public sealed record InferenceRequestOptions(bool DisableThinking = false, bool JsonObject = false,
    JsonElement? JsonSchema = null, float? Temperature = null, int? Seed = null);
public interface ITool
{
    string Id { get; }
    Task<ToolResult> ExecuteAsync(ToolProposal proposal, CancellationToken cancellationToken);
}
public interface IApprovalPresenter
{
    Task<bool> ConfirmAsync(string actionId, string title, string details, CancellationToken cancellationToken,
        Guid? taskId = null);
}

public interface ILiveApprovalStateProvider
{
    bool HasPendingActionConfirmationForTask(Guid taskId);
}

public interface IMessageSendPreviewPresenter
{
    Task ShowMessageSendPreviewAsync(MessageSendPreview preview, CancellationToken cancellationToken);
}

public enum CodeTaskReviewDecision { KeepPatch, RunDotNetTests, ApplyPatchToProject }

public interface ICodeTaskReviewPresenter
{
    Task<CodeTaskReviewDecision> ReviewAsync(string projectPath, string workspacePath, string diff,
        string? dotNetTestTarget, string? commandPreview, CancellationToken cancellationToken, Guid? taskId = null);
}
