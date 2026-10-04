using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XiaoK.Inference;
using XiaoK.Voice;

internal static class VoiceHostRoundTrip
{
    private const string SyntheticPhrase = "你好，小K，请打开本地项目。";
    private const string AsrRevision = "5eb144179a02acc5e5ba31e748d22b0cf3e303b0";
    private const string TtsRevision = "85e237c12c027371202489a0ec509ded67b5e4b5";

    public static Task<int> RunAsync(string workspaceArgument, string environmentArgument, string reportArgument) =>
        RunCoreAsync(workspaceArgument, null, environmentArgument, reportArgument, installationMode: false);

    public static Task<int> RunInstalledAsync(string packageRootArgument, string modelsRootArgument,
        string environmentArgument, string reportArgument) =>
        RunCoreAsync(packageRootArgument, modelsRootArgument, environmentArgument, reportArgument,
            installationMode: true);

    private static async Task<int> RunCoreAsync(string rootArgument, string? modelsRootArgument,
        string environmentArgument, string reportArgument, bool installationMode)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Host 语音诊断只支持 Windows。");
            return 2;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootArgument));
        var modelsRoot = modelsRootArgument is null
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(modelsRootArgument));
        var environment = Path.GetFullPath(environmentArgument);
        var reportPath = Path.GetFullPath(reportArgument);
        var protectedRoots = installationMode ? new[] { root, modelsRoot!, environment } : new[] { root };
        if (protectedRoots.Any(protectedRoot => IsWithinRoot(reportPath, protectedRoot)))
        {
            Console.Error.WriteLine("报告必须保存到代码、模型和运行环境目录之外，避免把本机评测数据加入项目或模型目录。");
            return 2;
        }
        if (File.Exists(reportPath))
        {
            Console.Error.WriteLine("报告路径已存在；为避免覆盖历史记录，本次未运行。");
            return 2;
        }

        var totalClock = Stopwatch.StartNew();
        var ttsColdClockMs = 0d;
        var ttsWarmClockMs = 0d;
        var asrColdClockMs = 0d;
        var asrWarmClockMs = 0d;
        var audioBytes = 0;
        var normalizedMatchCount = 0;
        var languageReportedCount = 0;
        var errorType = (string?)null;
        var modelBrokerWasUsed = false;
        var serviceCreated = false;
        var idleUnloadObserved = false;
        var postIdleRecoveryMatched = false;
        var idleUnloadWaitMs = 0d;
        var postIdleRecoveryMs = 0d;
        var matchingWorkersAfterDispose = -1;
        var wav = Array.Empty<byte>();
        VoiceInferenceService? voice = null;
        var asrPythonPath = Path.Combine(environment, "asr", "Scripts", "python.exe");
        var ttsPythonPath = Path.Combine(environment, "tts", "Scripts", "python.exe");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));

        try
        {
            if (installationMode && (GetWorkerProcessIds(asrPythonPath).Length != 0
                || GetWorkerProcessIds(ttsPythonPath).Length != 0))
                throw new InvalidOperationException("VOICE_WORKER_ALREADY_RUNNING");

            var broker = new ModelBroker();
            voice = installationMode
                ? VoiceInferenceService.TryCreateForInstallation(environment, modelsRoot!, root,
                    true, broker, out _)
                : VoiceInferenceService.TryCreateForWorkspace(root, environment,
                    Path.Combine(root, "src", "XiaoK.Voice", "voice_worker.py"), broker, out _);
            serviceCreated = voice is not null;
            if (voice is null)
                throw new InvalidOperationException("VOICE_SERVICE_UNAVAILABLE");

            var ttsClock = Stopwatch.StartNew();
            var firstWav = await voice.SynthesizeChineseWavAsync(SyntheticPhrase, deadline.Token);
            ttsClock.Stop();
            ttsColdClockMs = ttsClock.Elapsed.TotalMilliseconds;
            CryptographicOperations.ZeroMemory(firstWav);

            ttsClock.Restart();
            wav = await voice.SynthesizeChineseWavAsync(SyntheticPhrase, deadline.Token);
            ttsClock.Stop();
            ttsWarmClockMs = ttsClock.Elapsed.TotalMilliseconds;
            audioBytes = wav.Length;

            var asrClock = Stopwatch.StartNew();
            var recognition = await voice.TranscribeWavAsync(wav, forceChinese: true, deadline.Token);
            asrClock.Stop();
            asrColdClockMs = asrClock.Elapsed.TotalMilliseconds;
            if (!string.IsNullOrWhiteSpace(recognition.Language)) languageReportedCount++;
            if (Normalize(recognition.Text).Equals(Normalize(SyntheticPhrase), StringComparison.Ordinal))
                normalizedMatchCount++;

            asrClock.Restart();
            recognition = await voice.TranscribeWavAsync(wav, forceChinese: true, deadline.Token);
            asrClock.Stop();
            asrWarmClockMs = asrClock.Elapsed.TotalMilliseconds;
            if (!string.IsNullOrWhiteSpace(recognition.Language)) languageReportedCount++;
            if (Normalize(recognition.Text).Equals(Normalize(SyntheticPhrase), StringComparison.Ordinal))
                normalizedMatchCount++;

            if (installationMode)
            {
                if (GetWorkerProcessIds(asrPythonPath).Length != 1)
                    throw new InvalidOperationException("VOICE_ASR_WORKER_NOT_UNIQUE_BEFORE_IDLE");

                var idleClock = Stopwatch.StartNew();
                var idleDeadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(3);
                while (DateTimeOffset.UtcNow < idleDeadline)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), deadline.Token);
                    if (GetWorkerProcessIds(asrPythonPath).Length == 0)
                    {
                        idleUnloadObserved = true;
                        break;
                    }
                }
                idleClock.Stop();
                idleUnloadWaitMs = idleClock.Elapsed.TotalMilliseconds;
                if (!idleUnloadObserved)
                    throw new TimeoutException("VOICE_IDLE_UNLOAD_NOT_OBSERVED");

                var recoveryClock = Stopwatch.StartNew();
                recognition = await voice.TranscribeWavAsync(wav, forceChinese: true, deadline.Token);
                recoveryClock.Stop();
                postIdleRecoveryMs = recoveryClock.Elapsed.TotalMilliseconds;
                if (!string.IsNullOrWhiteSpace(recognition.Language)) languageReportedCount++;
                postIdleRecoveryMatched = Normalize(recognition.Text)
                    .Equals(Normalize(SyntheticPhrase), StringComparison.Ordinal);
                if (postIdleRecoveryMatched) normalizedMatchCount++;
                modelBrokerWasUsed = broker.LastUseUtc is not null;
            }
            modelBrokerWasUsed = broker.LastUseUtc is not null;
        }
        catch (Exception exception)
        {
            // Store only the exception type; exception messages can contain local paths or worker output.
            errorType = exception.GetType().Name;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wav);
            if (voice is not null)
            {
                try { await voice.DisposeAsync(); }
                catch (Exception exception) { errorType ??= exception.GetType().Name; }
            }
            if (installationMode)
                matchingWorkersAfterDispose = GetWorkerProcessIds(asrPythonPath).Length
                    + GetWorkerProcessIds(ttsPythonPath).Length;
        }

        totalClock.Stop();
        var expectedRecognitionCount = installationMode ? 3 : 2;
        var passed = errorType is null && serviceCreated && modelBrokerWasUsed && audioBytes > 44
            && normalizedMatchCount == expectedRecognitionCount && languageReportedCount == expectedRecognitionCount
            && ttsWarmClockMs < ttsColdClockMs && asrWarmClockMs < asrColdClockMs;
        if (installationMode)
            passed = passed && idleUnloadObserved && postIdleRecoveryMatched && matchingWorkersAfterDispose == 0;
        var report = new
        {
            schemaVersion = installationMode ? 3 : 2,
            startedAtUtc = DateTimeOffset.UtcNow - totalClock.Elapsed,
            completedAtUtc = DateTimeOffset.UtcNow,
            path = installationMode
                ? "signed installed package manifest -> external locked CPU TTS/ASR environments and models -> ModelBroker idle eviction -> in-memory WAV"
                : "VoiceInferenceService -> ModelBroker -> locked local CPU TTS -> in-memory WAV -> locked local CPU ASR",
            modelRevisions = new { asr = AsrRevision, tts = TtsRevision },
            ttsCallCount = 2,
            asrCallCount = expectedRecognitionCount,
            syntheticTextAndAudioSaved = false,
            transcriptSaved = false,
            wavBytesInMemory = audioBytes,
            wavZeroedAfterUse = wav.Length == 0 || wav.All(value => value == 0),
            modelBrokerObservedUse = modelBrokerWasUsed,
            ttsColdElapsedMs = ttsColdClockMs,
            ttsWarmElapsedMs = ttsWarmClockMs,
            asrColdElapsedMs = asrColdClockMs,
            asrWarmElapsedMs = asrWarmClockMs,
            transcriptNormalizedMatchCount = normalizedMatchCount,
            languageFieldPresentCount = languageReportedCount,
            idleUnloadExpectedDelayMs = installationMode ? 120000 : (int?)null,
            idleUnloadObserved,
            idleUnloadWaitMs,
            postIdleRecoveryMatched,
            postIdleRecoveryMs,
            matchingWorkersAfterDispose,
            totalElapsedMs = totalClock.Elapsed.TotalMilliseconds,
            errorType,
            diagnosticPassed = passed
        };

        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        Console.WriteLine($"Host 语音 CPU 暖模型复用：{(passed ? "通过" : "未通过")}；"
            + $"TTS 冷/暖 {ttsColdClockMs:F0}/{ttsWarmClockMs:F0} ms，"
            + $"ASR 冷/暖 {asrColdClockMs:F0}/{asrWarmClockMs:F0} ms，"
            + $"匹配 {normalizedMatchCount}/{expectedRecognitionCount}，"
            + (installationMode ? $"两分钟卸载 {(idleUnloadObserved ? "已观察" : "未观察")}。" : string.Empty)
            + "没有保存音频或转写内容。");
        Console.WriteLine($"匿名报告：{reportPath}");
        return passed ? 0 : 1;
    }

    private static int[] GetWorkerProcessIds(string expectedPythonPath)
    {
        if (!File.Exists(expectedPythonPath)) return [];
        var expectedPath = Path.GetFullPath(expectedPythonPath);
        var matches = new List<int>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(expectedPythonPath)))
        {
            try
            {
                var executablePath = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(executablePath)
                    && string.Equals(Path.GetFullPath(executablePath), expectedPath, StringComparison.OrdinalIgnoreCase))
                    matches.Add(process.Id);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception
                or NotSupportedException or UnauthorizedAccessException)
            {
                // An inaccessible unrelated Python process is not treated as this locked voice worker.
            }
            finally { process.Dispose(); }
        }
        return matches.ToArray();
    }

    private static bool IsWithinRoot(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && (relative == "." || relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static string Normalize(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormKC);
        return string.Concat(normalized.Where(char.IsLetterOrDigit));
    }
}
