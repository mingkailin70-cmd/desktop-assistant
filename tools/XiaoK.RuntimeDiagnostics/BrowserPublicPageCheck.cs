using System.Diagnostics;
using System.Text.Json;
using XiaoK.Adapters.Browser;
using XiaoK.Core;

internal static class BrowserPublicPageCheck
{
    private const string Url = "https://example.com/";

    public static async Task<int> RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var manager = new PlaywrightIsolatedBrowserSessionManager();
        var clock = Stopwatch.StartNew();
        string phase = "open";
        string? sessionId = null;
        var openSucceeded = false;
        var snapshotSucceeded = false;
        var expectedPageSeen = false;
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
                phase = "snapshot";
                var snapshot = sessionId is null
                    ? new ToolResult(false, "诊断没有取得会话标识。", "BROWSER_DIAGNOSTIC_SESSION_ID_MISSING")
                    : await manager.SnapshotAsync(sessionId, timeout.Token).ConfigureAwait(false);
                snapshotSucceeded = snapshot.Success && snapshot.Data is not null;
                expectedPageSeen = snapshotSucceeded
                    && snapshot.Data!.Contains("网址：https://example.com/", StringComparison.Ordinal)
                    && snapshot.Data.Contains("Example Domain", StringComparison.Ordinal);
                errorCode = snapshot.Success ? null : snapshot.ErrorCode;
            }

            if (sessionId is not null)
            {
                phase = "close";
                var closed = await manager.CloseAsync(sessionId, timeout.Token).ConfigureAwait(false);
                closeSucceeded = closed.Success;
                if (!closed.Success) errorCode = closed.ErrorCode;
            }

            clock.Stop();
            var passed = openSucceeded && snapshotSucceeded && expectedPageSeen && closeSucceeded;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                passed,
                urlHost = "example.com",
                openSucceeded,
                snapshotSucceeded,
                expectedPageSeen,
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
