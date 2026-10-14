namespace XiaoK.Adapters.Browser;

/// <summary>
/// Awaits resource-producing APIs that do not accept a cancellation token. Cancellation stops the
/// caller promptly; if startup finishes later, its resource is disposed on the detached cleanup path.
/// </summary>
internal static class CancellableAsyncResourceStartup
{
    public static async Task<T> StartAsync<T>(Func<Task<T>> start, Func<T, Task> disposeLate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(disposeLate);
        cancellationToken.ThrowIfCancellationRequested();

        var startup = start();
        try
        {
            return await startup.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = DisposeIfStartupCompletesAsync(startup, disposeLate);
            throw;
        }
    }

    private static async Task DisposeIfStartupCompletesAsync<T>(Task<T> startup, Func<T, Task> disposeLate)
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
    }
}
