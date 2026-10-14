using System.Buffers.Binary;
using System.IO;
using XiaoK.Host;

namespace XiaoK.DesktopExperience.Checks;

internal static class SherpaKwsWorkerIsolationProbe
{
    public static IReadOnlyList<string> Run(string repositoryRoot)
    {
        var failures = new List<string>();
        VerifyBoundedProtocolLines(failures);
        VerifyFrameBounds(failures);
        VerifyFixedKeywordAllowlist(failures);
        VerifyProcessIsolationWiring(repositoryRoot, failures);
        return failures;
    }

    private static void VerifyBoundedProtocolLines(List<string> failures)
    {
        try
        {
            var maximum = SherpaKwsWorkerProtocol.MaximumProtocolLineLength;
            using var reader = new StringReader(new string('x', maximum) + "\r\nREADY\nfinal");
            var exactLimit = SherpaKwsWorkerProtocol.ReadLineAsync(reader, CancellationToken.None).GetAwaiter().GetResult();
            var nextLine = SherpaKwsWorkerProtocol.ReadLineAsync(reader, CancellationToken.None).GetAwaiter().GetResult();
            var finalLine = SherpaKwsWorkerProtocol.ReadLineAsync(reader, CancellationToken.None).GetAwaiter().GetResult();
            var eof = SherpaKwsWorkerProtocol.ReadLineAsync(reader, CancellationToken.None).GetAwaiter().GetResult();
            if (exactLimit != new string('x', maximum) || nextLine != "READY" || finalLine != "final" || eof is not null)
                failures.Add("KWS bounded protocol reader mishandled an exact-limit line, consecutive lines, or EOF.");

            using var oversized = new StringReader(new string('x', maximum + 1) + "\n");
            try
            {
                _ = SherpaKwsWorkerProtocol.ReadLineAsync(oversized, CancellationToken.None).GetAwaiter().GetResult();
                failures.Add("KWS bounded protocol reader accepted an oversized line.");
            }
            catch (InvalidDataException) { }

            using var empty = new StringReader(string.Empty);
            if (SherpaKwsWorkerProtocol.ReadLineAsync(empty, CancellationToken.None).GetAwaiter().GetResult() is not null)
                failures.Add("KWS bounded protocol reader returned a line for empty input.");

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            using var pending = new StringReader("READY\n");
            try
            {
                _ = SherpaKwsWorkerProtocol.ReadLineAsync(pending, cancelled.Token).GetAwaiter().GetResult();
                failures.Add("KWS bounded protocol reader ignored cancellation.");
            }
            catch (OperationCanceledException) { }
        }
        catch (Exception error)
        {
            failures.Add($"KWS bounded protocol reader probe failed: {error.GetType().Name}.");
        }
    }

    private static void VerifyFrameBounds(List<string> failures)
    {
        Span<byte> header = stackalloc byte[SherpaKwsWorkerProtocol.FrameHeaderLength];
        foreach (var allowed in new[] { 1, SherpaKwsWorkerProtocol.MaximumSamplesPerFrame })
        {
            SherpaKwsWorkerProtocol.WriteSampleCount(header, allowed);
            if (!SherpaKwsWorkerProtocol.TryReadSampleCount(header, out var decoded) || decoded != allowed)
                failures.Add("KWS audio frame protocol rejected an in-range sample count.");
        }

        foreach (var invalid in new[] { int.MinValue, -1, 0, SherpaKwsWorkerProtocol.MaximumSamplesPerFrame + 1 })
        {
            BinaryPrimitives.WriteInt32LittleEndian(header, invalid);
            if (SherpaKwsWorkerProtocol.TryReadSampleCount(header, out _))
                failures.Add("KWS audio frame protocol accepted an out-of-range sample count.");
            try
            {
                SherpaKwsWorkerProtocol.WriteSampleCount(header, invalid);
                failures.Add("KWS audio frame writer accepted an out-of-range sample count.");
            }
            catch (ArgumentOutOfRangeException) { }
        }

        if (SherpaKwsWorkerProtocol.TryReadSampleCount(header[..^1], out _)
            || SherpaKwsWorkerProtocol.TryReadSampleCount(new byte[SherpaKwsWorkerProtocol.FrameHeaderLength + 1], out _))
            failures.Add("KWS audio frame protocol accepted a header with the wrong length.");
        try
        {
            SherpaKwsWorkerProtocol.WriteSampleCount(header[..^1], 1);
            failures.Add("KWS audio frame writer accepted a header with the wrong length.");
        }
        catch (ArgumentException) { }
    }

    private static void VerifyFixedKeywordAllowlist(List<string> failures)
    {
        foreach (var accepted in new[] { "XIAOK_HI", "XIAOK_HELLO", "XIAOK_HI_EN" })
            if (!SherpaKwsWorkerProtocol.IsAllowedKeywordLabel(accepted))
                failures.Add("KWS worker rejected a fixed XiaoK wake label.");

        foreach (var rejected in new[] { "", "ERROR:KWS_WORKER_FAILED", "XIAOK_HI\nanything", "arbitrary text" })
            if (SherpaKwsWorkerProtocol.IsAllowedKeywordLabel(rejected))
                failures.Add("KWS worker accepted a response outside the fixed wake-label allowlist.");
    }

    private static void VerifyProcessIsolationWiring(string repositoryRoot, List<string> failures)
    {
        var hostDirectory = Path.Combine(repositoryRoot, "src", "XiaoK.Host");
        var listener = File.ReadAllText(Path.Combine(hostDirectory, "SherpaAudioGraphWakeWordListener.cs"));
        var worker = File.ReadAllText(Path.Combine(hostDirectory, "SherpaKwsWorkerEntryPoint.cs"));
        var job = File.ReadAllText(Path.Combine(hostDirectory, "WindowsKillOnCloseJob.cs"));
        var app = File.ReadAllText(Path.Combine(hostDirectory, "App.xaml.cs"));

        var launcherStart = listener.IndexOf(
            "private static async Task<(Process Process, WindowsKillOnCloseJob Job)> StartKwsWorkerProcessAsync(",
            StringComparison.Ordinal);
        var launcherEnd = listener.IndexOf("private static async Task DrainWorkerStandardErrorAsync", launcherStart,
            StringComparison.Ordinal);
        var launcher = launcherStart >= 0 && launcherEnd > launcherStart
            ? listener[launcherStart..launcherEnd]
            : string.Empty;
        var assignIndex = launcher.IndexOf("job.Assign(process)", StringComparison.Ordinal);
        var startCommandIndex = launcher.IndexOf("StandardInput.WriteLineAsync(\"START\"", StringComparison.Ordinal);
        if (launcher.Length == 0 || assignIndex < 0 || startCommandIndex <= assignIndex
            || !launcher.Contains("ProcessPriorityClass.BelowNormal", StringComparison.Ordinal)
            || !launcher.Contains("RedirectStandardInput = true", StringComparison.Ordinal)
            || !launcher.Contains("RedirectStandardOutput = true", StringComparison.Ordinal)
            || !launcher.Contains("SherpaKwsWorkerProtocol.ReadLineAsync(process.StandardOutput", StringComparison.Ordinal))
            failures.Add("KWS worker must be assigned to its kill-on-close job before model startup and use redirected low-priority pipes.");

        if (!job.Contains("JobObjectLimitKillOnJobClose", StringComparison.Ordinal)
            || !job.Contains("AssignProcessToJobObject", StringComparison.Ordinal))
            failures.Add("KWS worker is not wired to a kill-on-Host-exit Windows job object.");

        if (worker.Contains("using Windows.Media.Audio", StringComparison.Ordinal)
            || worker.Contains("AudioGraph.CreateAsync", StringComparison.Ordinal)
            || worker.Contains("AudioDeviceInputNode", StringComparison.Ordinal)
            || worker.Contains("CreateDeviceInputNode", StringComparison.Ordinal)
            || worker.Contains("MediaCapture", StringComparison.Ordinal)
            || !worker.Contains("Console.OpenStandardInput()", StringComparison.Ordinal)
            || worker.Contains("Console.In.ReadLineAsync", StringComparison.Ordinal)
            || !worker.Contains("SherpaKwsWorkerProtocol.ReadLineAsync(Console.In", StringComparison.Ordinal)
            || !worker.Contains("CryptographicOperations.ZeroMemory(payload)", StringComparison.Ordinal))
            failures.Add("KWS worker must decode bounded pipe frames without opening a microphone and clear its audio buffer.");

        var appRoute = app.IndexOf("string.Equals(e.Args[0], \"--kws-worker\"", StringComparison.Ordinal);
        var normalStartup = app.IndexOf("RestoreMessageId = RegisterWindowMessage", StringComparison.Ordinal);
        if (appRoute < 0 || normalStartup <= appRoute || !app.Contains("SherpaKwsWorkerEntryPoint.RunAsync(e.Args)", StringComparison.Ordinal))
            failures.Add("KWS worker entry point is not isolated before normal Host startup.");

        var stopStart = listener.IndexOf("private async Task StopCaptureCoreAsync()", StringComparison.Ordinal);
        var stopEnd = listener.IndexOf("private static async Task<bool> StopWorkerProcessAsync", stopStart,
            StringComparison.Ordinal);
        var stop = stopStart >= 0 && stopEnd > stopStart ? listener[stopStart..stopEnd] : string.Empty;
        var releaseMicrophone = stop.IndexOf("graph?.Dispose()", StringComparison.Ordinal);
        var stopWorker = stop.IndexOf("StopWorkerProcessAsync(worker", StringComparison.Ordinal);
        if (stop.Length == 0 || releaseMicrophone < 0 || stopWorker <= releaseMicrophone
            || !stop.Contains("_inputNode?.Dispose()", StringComparison.Ordinal))
            failures.Add("Stopping KWS must release the AudioGraph and microphone before waiting for or killing the worker.");
    }
}
