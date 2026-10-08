namespace XiaoK.Core;

public enum ApprovalInboxKind { Confirmation, CodeReview, MessagePreview }

public enum ApprovalInboxChoice { Approve, Decline, KeepPatch, RunDotNetTests, ApplyPatch, DismissPreview, Unavailable }

public sealed record ApprovalInboxEntry(Guid Id, ApprovalInboxKind Kind, string Title, string Details,
    DateTimeOffset CreatedAtUtc, bool CanRunDotNetTests, Guid? TaskId)
{
    public bool CanApprove => Kind == ApprovalInboxKind.Confirmation;
    public bool CanDecline => Kind == ApprovalInboxKind.Confirmation;
    public bool CanKeepPatch => Kind == ApprovalInboxKind.CodeReview;
    public bool CanApplyPatch => Kind == ApprovalInboxKind.CodeReview;
    public bool CanDismissPreview => Kind == ApprovalInboxKind.MessagePreview;
}

/// <summary>Short-lived, in-memory decisions surfaced in the task center without modal windows.</summary>
public sealed class ApprovalInbox
{
    private const int MaximumPendingRequests = 16;
    private const int MaximumDetailsCharacters = 400_000;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, PendingApproval> _pending = [];

    public event Action? Changed;

    public bool HasPendingActionConfirmation
    {
        get
        {
            lock (_sync)
                return _pending.Values.Any(request => request.Entry.Kind is ApprovalInboxKind.Confirmation or ApprovalInboxKind.CodeReview);
        }
    }

    public bool HasPendingActionConfirmationForTask(Guid taskId)
    {
        lock (_sync)
            return _pending.Values.Any(request => request.Entry.TaskId == taskId
                && request.Entry.Kind is ApprovalInboxKind.Confirmation or ApprovalInboxKind.CodeReview);
    }

    public IReadOnlyList<ApprovalInboxEntry> GetPending()
    {
        lock (_sync)
            return _pending.Values.Select(request => request.Entry)
                .OrderBy(request => request.CreatedAtUtc).ToArray();
    }

    public Task<ApprovalInboxChoice> RequestAsync(string title, string details, ApprovalInboxKind kind,
        bool canRunDotNetTests, CancellationToken cancellationToken, Guid? taskId = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200)
            throw new ArgumentException("审批标题无效。", nameof(title));
        ArgumentNullException.ThrowIfNull(details);
        if (details.Length > MaximumDetailsCharacters)
            throw new ArgumentOutOfRangeException(nameof(details), "待处理内容超过内存审批上限。");
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));

        var entry = new ApprovalInboxEntry(Guid.NewGuid(), kind, title, details,
            DateTimeOffset.UtcNow, kind == ApprovalInboxKind.CodeReview && canRunDotNetTests, taskId);
        var request = new PendingApproval(entry, new TaskCompletionSource<ApprovalInboxChoice>(
            TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_sync)
        {
            if (_pending.Count >= MaximumPendingRequests)
                throw new InvalidOperationException("待处理确认队列已满；本次动作已停止，没有自动批准。");
            _pending.Add(entry.Id, request);
        }
        PublishChanged();
        return AwaitAndRemoveAsync(request, cancellationToken);
    }

    public bool Resolve(Guid id, ApprovalInboxChoice choice)
    {
        PendingApproval? request;
        lock (_sync)
        {
            if (!_pending.TryGetValue(id, out request) || !IsAllowed(request.Entry, choice)) return false;
        }
        return request.Completion.TrySetResult(choice);
    }

    private async Task<ApprovalInboxChoice> AwaitAndRemoveAsync(PendingApproval request,
        CancellationToken cancellationToken)
    {
        var defaultChoice = request.Entry.Kind switch
        {
            ApprovalInboxKind.CodeReview => ApprovalInboxChoice.KeepPatch,
            ApprovalInboxKind.MessagePreview => ApprovalInboxChoice.DismissPreview,
            _ => ApprovalInboxChoice.Decline
        };
        using var registration = cancellationToken.Register(static state =>
        {
            var (completion, fallback) = ((TaskCompletionSource<ApprovalInboxChoice>, ApprovalInboxChoice))state!;
            completion.TrySetResult(fallback);
        }, (request.Completion, defaultChoice));

        try
        {
            var choice = await request.Completion.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return choice;
        }
        finally
        {
            lock (_sync) _pending.Remove(request.Entry.Id);
            PublishChanged();
        }
    }

    private static bool IsAllowed(ApprovalInboxEntry entry, ApprovalInboxChoice choice) => entry.Kind switch
    {
        ApprovalInboxKind.Confirmation => choice is ApprovalInboxChoice.Approve or ApprovalInboxChoice.Decline,
        ApprovalInboxKind.CodeReview => choice switch
        {
            ApprovalInboxChoice.KeepPatch or ApprovalInboxChoice.ApplyPatch => true,
            ApprovalInboxChoice.RunDotNetTests => entry.CanRunDotNetTests,
            _ => false
        },
        ApprovalInboxKind.MessagePreview => choice == ApprovalInboxChoice.DismissPreview,
        _ => false
    };

    private void PublishChanged()
    {
        var handlers = Changed;
        if (handlers is null) return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception) { }
        }
    }

    private sealed record PendingApproval(ApprovalInboxEntry Entry,
        TaskCompletionSource<ApprovalInboxChoice> Completion);
}
