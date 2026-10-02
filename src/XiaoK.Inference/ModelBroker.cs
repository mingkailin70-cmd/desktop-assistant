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
    private Exception? _terminalFailure;
    private long _lastUseUtcTicks;

    public ModelBroker(IManagedModelRuntime? runtime = null) => _runtime = runtime;

    public Task<T> RunInteractiveAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
        RunAsync(operation, cancellationToken, interactive: true, operationRuntime: _runtime, competingRuntime: false);

    /// <summary>Runs one background inference step and yields the model lease when it completes.</summary>
    public Task<T> RunBackgroundStepAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
        RunAsync(operation, cancellationToken, interactive: false, operationRuntime: _runtime, competingRuntime: false);

    /// <summary>
    /// Runs an interactive operation in another local model process. It shares the exclusive
    /// priority queue and unloads the primary model before the competing process may start.
    /// </summary>
    public Task<T> RunCompetingModelInteractiveAsync<T>(IManagedModelRuntime externalRuntime,
        Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
        RunAsync(operation, cancellationToken, interactive: true, operationRuntime: externalRuntime,
            competingRuntime: true);

    /// <summary>Runs one competing-model background step and yields to queued interactive work.</summary>
    public Task<T> RunCompetingModelBackgroundStepAsync<T>(IManagedModelRuntime externalRuntime,
        Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
        RunAsync(operation, cancellationToken, interactive: false, operationRuntime: externalRuntime,
            competingRuntime: true);

    public DateTimeOffset? LastUseUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastUseUtcTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    private async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken,
        bool interactive, IManagedModelRuntime? operationRuntime, bool competingRuntime)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (competingRuntime)
        {
            ArgumentNullException.ThrowIfNull(operationRuntime);
            if (ReferenceEquals(operationRuntime, _runtime))
                throw new ArgumentException("竞争模型必须使用独立的托管运行时。", nameof(operationRuntime));
        }

        using var lease = await AcquireAsync(interactive, cancellationToken).ConfigureAwait(false);
        IAsyncDisposable? runtimeLease = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (competingRuntime && _runtime is not null)
            {
                try
                {
                    await _runtime.UnloadIfIdleAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Poison(ex);
                    throw new ModelBrokerUnavailableException(ex);
                }
            }
            if (operationRuntime is not null)
                runtimeLease = await operationRuntime.AcquireAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Exception? cleanupFailure = null;
            try
            {
                if (runtimeLease is not null) await runtimeLease.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                cleanupFailure = ex;
            }

            if (competingRuntime && operationRuntime is not null)
            {
                try
                {
                    await operationRuntime.UnloadIfIdleAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    cleanupFailure ??= ex;
                }
            }

            Interlocked.Exchange(ref _lastUseUtcTicks, DateTimeOffset.UtcNow.UtcDateTime.Ticks);
            if (cleanupFailure is not null)
            {
                Poison(cleanupFailure);
                throw new ModelBrokerUnavailableException(cleanupFailure);
            }
        }
    }

    private void Poison(Exception cause)
    {
        lock (_gate) _terminalFailure ??= cause;
    }

    private async Task<ModelLease> AcquireAsync(bool interactive, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Waiter? waiter = null;
        lock (_gate)
        {
            if (_terminalFailure is not null)
                throw new ModelBrokerUnavailableException(_terminalFailure);

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
            if (_terminalFailure is not null)
            {
                FailWaiters(_interactiveWaiters, _terminalFailure);
                FailWaiters(_backgroundWaiters, _terminalFailure);
                _modelInUse = false;
                return;
            }

            while (TryTakeNext(out var waiter))
            {
                if (waiter.Completion.TrySetResult(new ModelLease(this))) return;
            }
            _modelInUse = false;
        }
    }

    private static void FailWaiters(Queue<Waiter> queue, Exception cause)
    {
        while (queue.TryDequeue(out var waiter))
            waiter.Completion.TrySetException(new ModelBrokerUnavailableException(cause));
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

public sealed class ModelBrokerUnavailableException(Exception cause)
    : Exception("本地模型资源状态无法确认；调度器已停止启动模型。", cause);
