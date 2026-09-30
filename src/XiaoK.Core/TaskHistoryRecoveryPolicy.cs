namespace XiaoK.Core;

/// <summary>Projects work left in flight by a prior host process as requiring manual review; never replays it.</summary>
public static class TaskHistoryRecoveryPolicy
{
    public const string HostRestartedErrorCode = "HOST_RESTARTED";

    public static TaskRecord ForDisplay(TaskRecord task, DateTimeOffset currentProcessStartedAtUtc)
    {
        if (task.UpdatedAtUtc >= currentProcessStartedAtUtc || !IsInFlight(task.Status)) return task;

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
        && (state is "planning" or "running");

    private static bool IsInFlight(TaskLifecycleState state) =>
        state is TaskLifecycleState.Queued or TaskLifecycleState.Planning
            or TaskLifecycleState.Running or TaskLifecycleState.Verifying;
}
