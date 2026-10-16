namespace XiaoK.Core;

public enum ToolInteractionOperation
{
    Unknown,
    NonInteractive,
    ReadOnlyTarget,
    TargetStateChange,
    DesktopExclusive
}

public enum InteractionTargetUsage
{
    NotInUse,
    InUse,
    Unknown
}

public interface IInteractionTargetUsageProbe
{
    ValueTask<InteractionTargetUsage> GetUsageAsync(string targetId, CancellationToken cancellationToken);
}

public sealed record InteractionAdmission(ToolResult? Denial, IAsyncDisposable Lease);

/// <summary>
/// Coordinates UI work initiated through ToolBroker. This does not lock out the user or
/// third-party UIA clients; adapters must still enforce their own activation and focus contract.
/// </summary>
public sealed class InteractionCoordinator
{
    private readonly AsyncReaderWriterLock _desktop = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, AsyncReaderWriterLock> _targets =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly IInteractionTargetUsageProbe? _targetUsage;

    internal int TrackedTargetCount => _targets.Count;

    public InteractionCoordinator(IInteractionTargetUsageProbe? targetUsage = null) => _targetUsage = targetUsage;

    public async ValueTask<InteractionAdmission> AcquireAsync(ToolInteractionOperation operation,
        ToolExecutionAccess access, string targetId, CancellationToken cancellationToken)
    {
        if (access is not (ToolExecutionAccess.BackgroundOnly or ToolExecutionAccess.ExplicitUserInteraction))
            return Denied("执行上下文无效；未执行。", "INVALID_EXECUTION_CONTEXT");

        switch (operation)
        {
            case ToolInteractionOperation.NonInteractive:
                return Allowed(NoopLease.Instance);

            case ToolInteractionOperation.ReadOnlyTarget:
            {
                if (!TryNormalizeTarget(targetId, out var key))
                    return Denied("目标应用标识无效；未访问桌面。", "INVALID_INTERACTION_TARGET");
                var lease = await _targets.GetOrAdd(key, static _ => new AsyncReaderWriterLock())
                    .AcquireReadAsync(cancellationToken).ConfigureAwait(false);
                return Allowed(lease);
            }

            case ToolInteractionOperation.TargetStateChange:
            {
                if (!TryNormalizeTarget(targetId, out var key))
                    return Denied("目标应用标识无效；未修改应用状态。", "INVALID_INTERACTION_TARGET");
                if (_targetUsage is null)
                    return Denied("无法确认目标应用是否正由你使用；未修改应用状态。", "TARGET_USAGE_UNKNOWN");

                IAsyncDisposable? desktopLease = await _desktop.AcquireReadAsync(cancellationToken).ConfigureAwait(false);
                IAsyncDisposable? targetLease = null;
                try
                {
                    targetLease = await _targets.GetOrAdd(key, static _ => new AsyncReaderWriterLock())
                        .AcquireWriteAsync(cancellationToken).ConfigureAwait(false);
                    InteractionTargetUsage usage;
                    try
                    {
                        usage = await _targetUsage.GetUsageAsync(key, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch { usage = InteractionTargetUsage.Unknown; }

                    if (usage != InteractionTargetUsage.NotInUse)
                        return usage == InteractionTargetUsage.InUse
                            ? Denied("目标应用正由你使用；为避免打断，本次未修改其界面状态。", "TARGET_IN_USE")
                            : Denied("无法确认目标应用是否正由你使用；未修改应用状态。", "TARGET_USAGE_UNKNOWN");

                    var admitted = new CompositeLease(desktopLease, targetLease);
                    targetLease = null;
                    desktopLease = null;
                    return Allowed(admitted);
                }
                finally
                {
                    if (targetLease is not null) await targetLease.DisposeAsync().ConfigureAwait(false);
                    if (desktopLease is not null) await desktopLease.DisposeAsync().ConfigureAwait(false);
                }
            }

            case ToolInteractionOperation.DesktopExclusive:
                if (access != ToolExecutionAccess.ExplicitUserInteraction)
                    return Denied("该操作需要桌面独占；后台任务未执行，也未排队。", "FOREGROUND_REQUIRED");
                return Allowed(await _desktop.AcquireWriteAsync(cancellationToken).ConfigureAwait(false));

            default:
                return Denied("未知桌面操作类型已拒绝。", "UNKNOWN_INTERACTION_OPERATION");
        }
    }

    private static InteractionAdmission Allowed(IAsyncDisposable lease) => new(null, lease);
    private static InteractionAdmission Denied(string message, string code) => new(new(false, message, code), NoopLease.Instance);

    private static bool TryNormalizeTarget(string? targetId, out string normalized)
    {
        normalized = targetId?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized.Length is > 0 and <= 160
            && normalized.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':');
    }

    private sealed class CompositeLease(IAsyncDisposable first, IAsyncDisposable second) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await second.DisposeAsync().ConfigureAwait(false);
            await first.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class NoopLease : IAsyncDisposable
    {
        public static NoopLease Instance { get; } = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class AsyncReaderWriterLock
    {
        private readonly SemaphoreSlim _turnstile = new(1, 1);
        private readonly SemaphoreSlim _readerMutex = new(1, 1);
        private readonly SemaphoreSlim _resource = new(1, 1);
        private int _readers;

        public async ValueTask<IAsyncDisposable> AcquireReadAsync(CancellationToken cancellationToken)
        {
            await _turnstile.WaitAsync(cancellationToken).ConfigureAwait(false);
            _turnstile.Release();
            await _readerMutex.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_readers == 0)
                    await _resource.WaitAsync(cancellationToken).ConfigureAwait(false);
                _readers++;
            }
            finally
            {
                _readerMutex.Release();
            }
            return new Releaser(ReleaseReadAsync);
        }

        public async ValueTask<IAsyncDisposable> AcquireWriteAsync(CancellationToken cancellationToken)
        {
            await _turnstile.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _resource.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _turnstile.Release();
                throw;
            }
            return new Releaser(() =>
            {
                _resource.Release();
                _turnstile.Release();
                return ValueTask.CompletedTask;
            });
        }

        private async ValueTask ReleaseReadAsync()
        {
            await _readerMutex.WaitAsync().ConfigureAwait(false);
            try
            {
                if (--_readers == 0)
                    _resource.Release();
            }
            finally
            {
                _readerMutex.Release();
            }
        }

        private sealed class Releaser(Func<ValueTask> release) : IAsyncDisposable
        {
            private Func<ValueTask>? _release = release;
            public ValueTask DisposeAsync() => Interlocked.Exchange(ref _release, null)?.Invoke() ?? ValueTask.CompletedTask;
        }
    }
}
