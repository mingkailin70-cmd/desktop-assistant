namespace XiaoK.Core;

/// <summary>Projects work left in flight by a prior host process as requiring manual review; never replays it.</summary>
public static class TaskHistoryRecoveryPolicy
{
    public const string HostRestartedErrorCode = "HOST_RESTARTED";
    public const string ApprovalNotRestoredErrorCode = "APPROVAL_NOT_RESTORED";

    public static TaskRecord ForDisplay(TaskRecord task, DateTimeOffset currentProcessStartedAtUtc)
    {
        if (task.UpdatedAtUtc >= currentProcessStartedAtUtc) return task;

        if (task.Status == TaskLifecycleState.AwaitingApproval)
        {
            return task with
            {
                Status = TaskLifecycleState.OutcomeUncertain,
                Result = null,
                ErrorCode = ApprovalNotRestoredErrorCode
            };
        }

        if (!IsInFlight(task.Status)) return task;

        return task with
        {
            Status = TaskLifecycleState.OutcomeUncertain,
            Result = null,
            ErrorCode = HostRestartedErrorCode
        };
    }

    public static bool IsInterruptedCodeTask(string state, DateTimeOffset updatedAtUtc,
        DateTimeOffset currentProcessStartedAtUtc) =>
        updatedAtUtc < currentProcessStartedAtUtc
        && (state is "planning" or "running" or "applying" or "awaiting_approval");

    private static bool IsInFlight(TaskLifecycleState state) =>
        state is TaskLifecycleState.Queued or TaskLifecycleState.Planning
            or TaskLifecycleState.Running or TaskLifecycleState.Verifying;
}
