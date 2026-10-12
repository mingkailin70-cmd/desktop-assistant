namespace XiaoK.Core;

/// <summary>Projects work left in flight by a prior host process as requiring manual review; never replays it.</summary>
public static class TaskHistoryRecoveryPolicy
{
    public const string HostRestartedErrorCode = "HOST_RESTARTED";
    public const string HostRestartedBeforeActionErrorCode = "HOST_RESTARTED_BEFORE_ACTION";
    public const string ApprovalNotRestoredErrorCode = "APPROVAL_NOT_RESTORED";

    public static TaskRecord ForDisplay(TaskRecord task, Guid currentHostSessionId)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (currentHostSessionId == Guid.Empty)
            throw new ArgumentException("当前Host会话标识无效。", nameof(currentHostSessionId));

        if (task.HostSessionId == currentHostSessionId) return task;

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

        if (task.HostSessionId is not null
            && (task.CheckpointStage is TaskCheckpointStage.Accepted or TaskCheckpointStage.Planning))
        {
            return task with
            {
                Status = TaskLifecycleState.Failed,
                Result = null,
                ErrorCode = HostRestartedBeforeActionErrorCode,
                CheckpointStage = TaskCheckpointStage.Terminal
            };
        }

        return task with
        {
            Status = TaskLifecycleState.OutcomeUncertain,
            Result = null,
            ErrorCode = HostRestartedErrorCode,
            CheckpointStage = TaskCheckpointStage.Terminal
        };
    }

    public static bool IsInterruptedCodeTask(string state, Guid? storedHostSessionId, Guid currentHostSessionId)
    {
        if (currentHostSessionId == Guid.Empty)
            throw new ArgumentException("当前Host会话标识无效。", nameof(currentHostSessionId));

        return (state is "planning" or "running" or "verifying" or "applying" or "awaiting_approval")
            && storedHostSessionId != currentHostSessionId;
    }

    private static bool IsInFlight(TaskLifecycleState state) =>
        state is TaskLifecycleState.Queued or TaskLifecycleState.Planning
            or TaskLifecycleState.Running or TaskLifecycleState.Verifying;
}
