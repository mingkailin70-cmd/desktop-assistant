using System.Collections.Immutable;

namespace XiaoK.Core;

public enum TaskLifecycleState { Queued, Planning, AwaitingApproval, Running, Verifying, Completed, Failed, Cancelled, OutcomeUncertain }

public sealed record TaskRecord(Guid Id, string Kind, string Summary, TaskLifecycleState Status, DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc, string? Result = null, string? ErrorCode = null);
public sealed record ApprovalAuditRecord(Guid Id, string ActionId, string Outcome, DateTimeOffset CreatedAtUtc);

public static class ApprovalAuditCatalog
{
    public const string MessageSendAction = "message.send.v1";
    public const string CodeTaskAction = "code.task.create.v1";
    public const string Confirmed = "confirmed";
    public const string Declined = "declined";
    public const string RunDotNetTests = "run_dotnet_tests";
}

public sealed record ToolProposal(string ToolId, ImmutableDictionary<string, string> Arguments, string Target, string ExpectedOutcome);
public sealed record ToolResult(bool Success, string Summary, string? ErrorCode = null, string? Data = null, TaskLifecycleState? FinalState = null);
public sealed record MessageNotice(string ApplicationId, string SourceAppId, string? ConversationId, string? SenderDisplayName,
    bool IsPrivateConversation, string? Body, DateTimeOffset ReceivedAtUtc, string DeduplicationKey);
public sealed record NoticeDecision(bool Accepted, bool AnalyzeBody, string UserMessage, MessageNotice? Notice = null);

public interface ITaskStore
{
    Task SaveAsync(TaskRecord task, CancellationToken cancellationToken);
    Task<IReadOnlyList<TaskRecord>> GetRecentAsync(int count, CancellationToken cancellationToken);
}
public interface IInferenceClient
{
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken);
}
public interface ITool
{
    string Id { get; }
    Task<ToolResult> ExecuteAsync(ToolProposal proposal, CancellationToken cancellationToken);
}
public interface IApprovalPresenter
{
    Task<bool> ConfirmAsync(string actionId, string title, string details, CancellationToken cancellationToken);
}

public enum CodeTaskReviewDecision { KeepPatch, RunDotNetTests }

public interface ICodeTaskReviewPresenter
{
    Task<CodeTaskReviewDecision> ReviewAsync(string projectPath, string workspacePath, string diff,
        string? dotNetTestTarget, string? commandPreview, CancellationToken cancellationToken);
}
