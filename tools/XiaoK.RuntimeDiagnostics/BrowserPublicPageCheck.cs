using System.Diagnostics;
using System.Text.Json;
using XiaoK.Adapters.Browser;
using XiaoK.Core;

internal static class BrowserPublicPageCheck
{
    private const string Url = "https://example.com/";
    private const string NextUrl = "https://example.org/";

    public static async Task<int> RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var manager = new PlaywrightIsolatedBrowserSessionManager();
        var clock = Stopwatch.StartNew();
        string phase = "open";
        string? sessionId = null;
        var openSucceeded = false;
        var snapshotSucceeded = false;
        var initialPageSeen = false;
        var navigationSucceeded = false;
        var nextPageSeen = false;
        var snapshotRotated = false;
        var staleSnapshotRejected = false;
        var closeSucceeded = false;
        string? errorCode = null;

        try
        {
            var opened = await manager.OpenAsync(Url, timeout.Token).ConfigureAwait(false);
            openSucceeded = opened.Success;
            errorCode = opened.ErrorCode;
            if (opened.Success && opened.Data is { } openedSnapshot)
            {
                sessionId = ReadField(openedSnapshot, "会话ID：");
                initialPageSeen = openedSnapshot.Contains("网址:https://example.com/", StringComparison.Ordinal)
                    || openedSnapshot.Contains("网址：https://example.com/", StringComparison.Ordinal);
                initialPageSeen &= openedSnapshot.Contains("Example Domain", StringComparison.Ordinal);

                phase = "snapshot";
                var snapshot = sessionId is null
                    ? new ToolResult(false, "诊断没有取得会话标识。", "BROWSER_DIAGNOSTIC_SESSION_ID_MISSING")
                    : await manager.SnapshotAsync(sessionId, timeout.Token).ConfigureAwait(false);
                snapshotSucceeded = snapshot.Success && snapshot.Data is not null;
                var currentSnapshotId = snapshotSucceeded ? ReadField(snapshot.Data!, "快照ID：") : null;
                initialPageSeen &= snapshotSucceeded
                    && snapshot.Data!.Contains("网址：https://example.com/", StringComparison.Ordinal)
                    && snapshot.Data.Contains("Example Domain", StringComparison.Ordinal);
                errorCode = snapshot.Success ? null : snapshot.ErrorCode;

                phase = "navigate";
                var navigated = sessionId is null || currentSnapshotId is null
                    ? new ToolResult(false, "诊断没有取得当前页面令牌。", "BROWSER_DIAGNOSTIC_SNAPSHOT_ID_MISSING")
                    : await manager.NavigateAsync(sessionId, currentSnapshotId, NextUrl, timeout.Token).ConfigureAwait(false);
                navigationSucceeded = navigated.Success && navigated.Data is not null;
                var nextSnapshotId = navigationSucceeded ? ReadField(navigated.Data!, "快照ID：") : null;
                snapshotRotated = nextSnapshotId is not null && currentSnapshotId is not null
                    && !string.Equals(nextSnapshotId, currentSnapshotId, StringComparison.Ordinal);
                nextPageSeen = navigationSucceeded
                    && navigated.Data!.Contains("网址：https://example.org/", StringComparison.Ordinal)
                    && navigated.Data.Contains("Example Domain", StringComparison.Ordinal)
                    && snapshotRotated;
                errorCode = navigated.Success ? null : navigated.ErrorCode;

                phase = "stale-snapshot";
                var staleReplay = sessionId is null || currentSnapshotId is null
                    ? new ToolResult(false, "诊断没有取得旧页面令牌。", "BROWSER_DIAGNOSTIC_SNAPSHOT_ID_MISSING")
                    : await manager.NavigateAsync(sessionId, currentSnapshotId, Url, timeout.Token).ConfigureAwait(false);
                staleSnapshotRejected = !staleReplay.Success && staleReplay.ErrorCode == "BROWSER_STALE_SNAPSHOT";
                if (!staleSnapshotRejected) errorCode = staleReplay.ErrorCode ?? "BROWSER_STALE_SNAPSHOT_NOT_REJECTED";

                phase = "post-navigation-snapshot";
                var afterNavigation = sessionId is null
                    ? new ToolResult(false, "诊断没有取得会话标识。", "BROWSER_DIAGNOSTIC_SESSION_ID_MISSING")
                    : await manager.SnapshotAsync(sessionId, timeout.Token).ConfigureAwait(false);
                nextPageSeen &= afterNavigation.Success && afterNavigation.Data is not null
                    && afterNavigation.Data.Contains("网址：https://example.org/", StringComparison.Ordinal)
                    && afterNavigation.Data.Contains("Example Domain", StringComparison.Ordinal)
                    && snapshotRotated;
                if (!afterNavigation.Success) errorCode = afterNavigation.ErrorCode;
            }

            if (sessionId is not null)
            {
                phase = "close";
                var closed = await manager.CloseAsync(sessionId, timeout.Token).ConfigureAwait(false);
                closeSucceeded = closed.Success;
                if (!closed.Success) errorCode = closed.ErrorCode;
            }

            clock.Stop();
            var passed = openSucceeded && snapshotSucceeded && initialPageSeen && navigationSucceeded
                && nextPageSeen && staleSnapshotRejected && closeSucceeded;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                passed,
                urlHosts = new[] { "example.com", "example.org" },
                openSucceeded,
                snapshotSucceeded,
                initialPageSeen,
                navigationSucceeded,
                nextPageSeen,
                snapshotRotated,
                staleSnapshotRejected,
                closeSucceeded,
                rawPageTextLogged = false,
                diagnosticReportWritten = false,
                elapsedMilliseconds = (long)clock.Elapsed.TotalMilliseconds,
                errorCode
            }, new JsonSerializerOptions { WriteIndented = true }));
            return passed ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            clock.Stop();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                passed = false,
                phase,
                cancelledOrTimedOut = true,
                elapsedMilliseconds = (long)clock.Elapsed.TotalMilliseconds,
                rawPageTextLogged = false,
                diagnosticReportWritten = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 2;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException and not AccessViolationException)
        {
            clock.Stop();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                passed = false,
                phase,
                failureType = exception.GetType().Name,
                elapsedMilliseconds = (long)clock.Elapsed.TotalMilliseconds,
                rawPageTextLogged = false,
                diagnosticReportWritten = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 2;
        }
    }

    private static string? ReadField(string snapshot, string label)
    {
        foreach (var line in snapshot.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith(label, StringComparison.Ordinal))
            {
                var value = trimmed[label.Length..].Trim();
                return value.Length is > 0 and <= 128 ? value : null;
            }
        }
        return null;
    }
}
