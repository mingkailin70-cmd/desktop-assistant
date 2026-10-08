namespace XiaoK.Core;

public enum TaskExecutionAdmissionState { Queued, Running, CancelledBeforeStart, Completed }

/// <summary>Arbitrates the race between dequeuing a task and cancelling it while it is still queued.</summary>
public sealed class TaskExecutionAdmissionGate
{
    private int _state = (int)TaskExecutionAdmissionState.Queued;

    public TaskExecutionAdmissionState State =>
        (TaskExecutionAdmissionState)Volatile.Read(ref _state);

    public bool TryStart() => Interlocked.CompareExchange(ref _state,
        (int)TaskExecutionAdmissionState.Running,
        (int)TaskExecutionAdmissionState.Queued) == (int)TaskExecutionAdmissionState.Queued;

    public bool TryCancelBeforeStart() => Interlocked.CompareExchange(ref _state,
        (int)TaskExecutionAdmissionState.CancelledBeforeStart,
        (int)TaskExecutionAdmissionState.Queued) == (int)TaskExecutionAdmissionState.Queued;

    public bool TryComplete()
    {
        return Interlocked.CompareExchange(ref _state,
            (int)TaskExecutionAdmissionState.Completed,
            (int)TaskExecutionAdmissionState.Running) == (int)TaskExecutionAdmissionState.Running;
    }
}
