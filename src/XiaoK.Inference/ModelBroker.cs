namespace XiaoK.Inference;

/// <summary>
/// Grants one model call at a time. Interactive calls take precedence over queued
/// background steps; background callers must release the lease between tool steps.
/// GPU admission and model process lifecycle remain separate responsibilities.
/// </summary>
public sealed class ModelBroker
{
    private const int MaximumQueuedRequests = 128;
    private readonly object _gate = new();
    private readonly Queue<Waiter> _interactiveWaiters = new();
    private readonly Queue<Waiter> _backgroundWaiters = new();
    private readonly IManagedModelRuntime? _runtime;
    private bool _modelInUse;
    private long _lastUseUtcTicks;

    public ModelBroker(IManagedModelRuntime? runtime = null) => _runtime = runtime;

    public Task<T> RunInteractiveAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
        RunAsync(operation, cancellationToken, interactive: true);

    /// <summary>Runs one background inference step and yields the model lease when it completes.</summary>
    public Task<T> RunBackgroundStepAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
        RunAsync(operation, cancellationToken, interactive: false);

    public DateTimeOffset? LastUseUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastUseUtcTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    private async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken, bool interactive)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var lease = await AcquireAsync(interactive, cancellationToken).ConfigureAwait(false);
        IAsyncDisposable? runtimeLease = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_runtime is not null) runtimeLease = await _runtime.AcquireAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (runtimeLease is not null) await runtimeLease.DisposeAsync().ConfigureAwait(false);
            Interlocked.Exchange(ref _lastUseUtcTicks, DateTimeOffset.UtcNow.UtcDateTime.Ticks);
        }
    }

    private async Task<ModelLease> AcquireAsync(bool interactive, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Waiter? waiter = null;
        lock (_gate)
        {
            PruneCompleted(_interactiveWaiters);
            PruneCompleted(_backgroundWaiters);
            if (!_modelInUse && _interactiveWaiters.Count == 0 && _backgroundWaiters.Count == 0)
            {
                _modelInUse = true;
                return new ModelLease(this);
            }

            if (_interactiveWaiters.Count + _backgroundWaiters.Count >= MaximumQueuedRequests)
                throw new ModelQueueFullException();

            waiter = new Waiter();
            (interactive ? _interactiveWaiters : _backgroundWaiters).Enqueue(waiter);
        }

        using var registration = cancellationToken.Register(static state =>
        {
            var (pending, token) = ((Waiter, CancellationToken))state!;
            pending.Completion.TrySetCanceled(token);
        }, (waiter, cancellationToken));
        return await waiter.Completion.Task.ConfigureAwait(false);
    }

    private void Release()
    {
        lock (_gate)
        {
            while (TryTakeNext(out var waiter))
            {
                if (waiter.Completion.TrySetResult(new ModelLease(this))) return;
            }
            _modelInUse = false;
        }
    }

    private bool TryTakeNext(out Waiter waiter)
    {
        while (_interactiveWaiters.TryDequeue(out waiter!))
            if (!waiter.Completion.Task.IsCompleted) return true;
        while (_backgroundWaiters.TryDequeue(out waiter!))
            if (!waiter.Completion.Task.IsCompleted) return true;
        waiter = null!;
        return false;
    }

    private static void PruneCompleted(Queue<Waiter> queue)
    {
        var count = queue.Count;
        while (count-- > 0)
        {
            var waiter = queue.Dequeue();
            if (!waiter.Completion.Task.IsCompleted) queue.Enqueue(waiter);
        }
    }

    private sealed class Waiter
    {
        public TaskCompletionSource<ModelLease> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ModelLease(ModelBroker owner) : IDisposable
    {
        private ModelBroker? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}

public sealed class ModelQueueFullException() : Exception("本地模型等待队列已满；本次请求未排队。");
