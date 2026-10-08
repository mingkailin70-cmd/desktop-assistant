namespace XiaoK.Core;

/// <summary>Builds the terminal record for accepted work cancelled before its worker starts.</summary>
public static class TaskExecutionShutdownPolicy
{
    public const string ErrorCode = "SHUTDOWN_BEFORE_START";

    public static TaskRecord CancelBeforeStart(TaskRecord task, DateTimeOffset shutdownAtUtc)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Status != TaskLifecycleState.Queued)
            throw new ArgumentException("Only queued work can be marked cancelled before start.", nameof(task));
        if (shutdownAtUtc < task.CreatedAtUtc)
            throw new ArgumentOutOfRangeException(nameof(shutdownAtUtc));

        return task with
        {
            Status = TaskLifecycleState.Cancelled,
            UpdatedAtUtc = shutdownAtUtc,
            ErrorCode = ErrorCode
        };
    }
}
