namespace XiaoK.Inference;

/// <summary>Owns the lifetime of one local model process and its loaded weights.</summary>
public interface IManagedModelRuntime : IAsyncDisposable
{
    string Status { get; }
    ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stops this model after the shared broker has excluded every other model operation.
    /// The runtime remains reusable by a later primary-model request.
    /// </summary>
    ValueTask UnloadIfIdleAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A competing runtime that may keep one verified model process warm for a bounded
/// idle period. The broker unloads it before switching to a different runtime.
/// </summary>
public interface IIdleRetainedModelRuntime : IManagedModelRuntime
{
    ValueTask ScheduleIdleUnloadAsync(CancellationToken cancellationToken);
}

public sealed class ModelRuntimeUnavailableException(string message) : Exception(message);

/// <summary>Blocks inference when an explicitly configured managed runtime is invalid.</summary>
public sealed class UnavailableModelRuntime(string status) : IManagedModelRuntime
{
    public string Status { get; } = status;

    public ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken) =>
        ValueTask.FromException<IAsyncDisposable>(new ModelRuntimeUnavailableException(Status));

    public ValueTask UnloadIfIdleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
