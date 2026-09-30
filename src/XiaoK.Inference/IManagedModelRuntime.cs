namespace XiaoK.Inference;

/// <summary>Owns the lifetime of one local model process and its loaded weights.</summary>
public interface IManagedModelRuntime : IAsyncDisposable
{
    string Status { get; }
    ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken);
}

public sealed class ModelRuntimeUnavailableException(string message) : Exception(message);

/// <summary>Blocks inference when an explicitly configured managed runtime is invalid.</summary>
public sealed class UnavailableModelRuntime(string status) : IManagedModelRuntime
{
    public string Status { get; } = status;

    public ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken) =>
        ValueTask.FromException<IAsyncDisposable>(new ModelRuntimeUnavailableException(Status));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
