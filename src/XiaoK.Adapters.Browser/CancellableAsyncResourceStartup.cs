namespace XiaoK.Adapters.Browser;

/// <summary>
/// Awaits resource-producing APIs that do not accept a cancellation token. Cancellation stops the
/// caller promptly; if startup finishes later, its resource is disposed on the detached cleanup path.
/// </summary>
internal static class CancellableAsyncResourceStartup
{
    public static async Task<T> StartAsync<T>(Func<Task<T>> start, Func<T, Task> disposeLate,
        CancellationToken cancellationToken, Action? onCancellation = null,
        Func<Task>? disposeOwnerAfterLateCleanup = null)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(disposeLate);
        if ((onCancellation is null) != (disposeOwnerAfterLateCleanup is null))
            throw new ArgumentException("Late cleanup ownership transfer requires both callbacks.");
        cancellationToken.ThrowIfCancellationRequested();

        var startup = start();
        try
        {
            return await startup.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            onCancellation?.Invoke();
            _ = DisposeIfStartupCompletesAsync(startup, disposeLate, disposeOwnerAfterLateCleanup);
            throw;
        }
    }

    private static async Task DisposeIfStartupCompletesAsync<T>(Task<T> startup, Func<T, Task> disposeLate,
        Func<Task>? disposeOwnerAfterLateCleanup)
    {
        try
        {
            var resource = await startup.ConfigureAwait(false);
            await disposeLate(resource).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Observe startup/cleanup failures on this detached cleanup path. The caller has already
            // received cancellation and cannot safely resume using a late browser resource.
        }
        finally
        {
            if (disposeOwnerAfterLateCleanup is not null)
            {
                try { await disposeOwnerAfterLateCleanup().ConfigureAwait(false); }
                catch (Exception) { }
            }
        }
    }
}
