using System.Collections.Immutable;
using System.Text.Json;

namespace XiaoK.Core;

public enum TaskLifecycleState { Queued, Planning, AwaitingApproval, Running, Verifying, Completed, Failed, Cancelled, OutcomeUncertain }

public sealed record TaskRecord(Guid Id, string Kind, string Summary, TaskLifecycleState Status, DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc, string? Result = null, string? ErrorCode = null);
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
    UserProvidedPublicFileUrl = 2048
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
    FileSentToRecycleBin = 18
}

public static class ApprovalAuditCatalog
{
    public const string MessageSendAction = "message.send.v1";
    public const string CodeTaskAction = "code.task.create.v1";
    public const string CodePatchApplyAction = "code.patch.apply.v1";
    public const string FileRecycleAction = "file.delete.recycle-bin.v1";
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
    Task<bool> ConfirmAsync(string actionId, string title, string details, CancellationToken cancellationToken);
}

public interface ILiveApprovalStateProvider
{
    bool HasPendingActionConfirmation { get; }
}

public interface IMessageSendPreviewPresenter
{
    Task ShowMessageSendPreviewAsync(MessageSendPreview preview, CancellationToken cancellationToken);
}

public enum CodeTaskReviewDecision { KeepPatch, RunDotNetTests, ApplyPatchToProject }

public interface ICodeTaskReviewPresenter
{
    Task<CodeTaskReviewDecision> ReviewAsync(string projectPath, string workspacePath, string diff,
        string? dotNetTestTarget, string? commandPreview, CancellationToken cancellationToken);
}
