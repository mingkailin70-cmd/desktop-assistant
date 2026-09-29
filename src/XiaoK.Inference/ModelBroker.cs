namespace XiaoK.Inference;

/// <summary>Serializes model calls. Process ownership and VRAM admission checks are P0 release blockers.</summary>
public sealed class ModelBroker
{
    private readonly SemaphoreSlim _exclusiveLease = new(1, 1);
    private DateTimeOffset? _lastUse;

    public async Task<T> RunInteractiveAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        await _exclusiveLease.WaitAsync(cancellationToken);
        try { return await operation(cancellationToken); }
        finally { _lastUse = DateTimeOffset.UtcNow; _exclusiveLease.Release(); }
    }

    public DateTimeOffset? LastUseUtc => _lastUse;
}
