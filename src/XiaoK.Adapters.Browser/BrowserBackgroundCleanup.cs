namespace XiaoK.Adapters.Browser;

/// <summary>Runs best-effort browser cleanup without letting callback failures escape or wait indefinitely.</summary>
internal static class BrowserBackgroundCleanup
{
    public static async Task RunBestEffortAsync(Func<Task> cleanup, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

        try
        {
            var pending = cleanup();
            if (pending is null) return;

            // Observe failures even when the bounded wait expires before the browser operation does.
            _ = ObserveCompletionAsync(pending);
            await pending.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cleanup is best-effort; it must not replace a cancellation or the original browser error.
        }
    }

    internal static async Task ObserveCompletionAsync(Task pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        try { await pending.ConfigureAwait(false); }
        catch (Exception) { }
    }
}
