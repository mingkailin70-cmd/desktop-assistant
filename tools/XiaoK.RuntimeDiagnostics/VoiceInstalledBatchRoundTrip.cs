using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XiaoK.Inference;
using XiaoK.Voice;

internal static class VoiceInstalledBatchRoundTrip
{
    private static readonly string[] Phrases =
    [
        "你好，小K，请打开本地项目。",
        "我先检查文件内容，再告诉你结果。",
        "任务已经取消，我没有执行后续操作。",
        "下午三点请提醒我保存文档。",
        "发送前请再次核对联系人、正文和附件。",
        "操作结果无法确认时，不要自动重试。"
    ];

    private const string AsrRevision = "5eb144179a02acc5e5ba31e748d22b0cf3e303b0";
    private const string TtsRevision = "85e237c12c027371202489a0ec509ded67b5e4b5";

    public static async Task<int> RunAsync(string packageRootArgument, string modelsRootArgument,
        string environmentArgument, string reportArgument)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("安装版语音诊断只支持 Windows。");
            return 2;
        }

        var packageRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageRootArgument));
        var modelsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modelsRootArgument));
        var environmentRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(environmentArgument));
        var reportPath = Path.GetFullPath(reportArgument);
        if (IsWithinRoot(reportPath, packageRoot) || IsWithinRoot(reportPath, modelsRoot)
            || IsWithinRoot(reportPath, environmentRoot))
        {
            Console.Error.WriteLine("脱敏报告必须写到安装包、模型和语音环境目录之外。");
            return 2;
        }
        if (File.Exists(reportPath))
        {
            Console.Error.WriteLine("报告路径已存在；本次不会覆盖历史记录。");
            return 2;
        }

        var asrPython = Path.Combine(environmentRoot, "asr", "Scripts", "python.exe");
        var ttsPython = Path.Combine(environmentRoot, "tts", "Scripts", "python.exe");
        var audioSamples = new List<byte[]>(Phrases.Length);
        var ttsLatenciesMs = new List<double>(Phrases.Length);
        var asrLatenciesMs = new List<double>(Phrases.Length);
        var transcriptMatches = new List<bool>(Phrases.Length);
        var totalClock = Stopwatch.StartNew();
        var audioBytes = 0L;
        var languageFieldPresentCount = 0;
        var modelBrokerWasUsed = false;
        var serviceCreated = false;
        var idleUnloadObserved = false;
        var idleUnloadWaitMs = 0d;
        var postIdleRecoveryMatched = false;
        var postIdleRecoveryMs = 0d;
        var matchingWorkersAfterDispose = -1;
        string? errorType = null;
        VoiceInferenceService? voice = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));

        try
        {
            if (GetWorkerProcessIds(asrPython).Length != 0 || GetWorkerProcessIds(ttsPython).Length != 0)
                throw new InvalidOperationException("VOICE_WORKER_ALREADY_RUNNING");

            var broker = new ModelBroker();
            voice = VoiceInferenceService.TryCreateForInstallation(environmentRoot, modelsRoot, packageRoot,
                packageIdentityVerified: true, broker, out _);
            serviceCreated = voice is not null;
            if (voice is null) throw new InvalidOperationException("VOICE_SERVICE_UNAVAILABLE");

            for (var index = 0; index < Phrases.Length; index++)
            {
                var clock = Stopwatch.StartNew();
                var wav = await voice.SynthesizeChineseWavAsync(Phrases[index], deadline.Token);
                clock.Stop();
                ttsLatenciesMs.Add(clock.Elapsed.TotalMilliseconds);
                audioBytes += wav.Length;
                if (wav.Length <= 44) throw new InvalidDataException("VOICE_WAV_INVALID");
                audioSamples.Add(wav);
            }

            for (var index = 0; index < Phrases.Length; index++)
            {
                var clock = Stopwatch.StartNew();
                var recognition = await voice.TranscribeWavAsync(audioSamples[index], forceChinese: true,
                    deadline.Token);
                clock.Stop();
                asrLatenciesMs.Add(clock.Elapsed.TotalMilliseconds);
                if (!string.IsNullOrWhiteSpace(recognition.Language)) languageFieldPresentCount++;
                transcriptMatches.Add(Normalize(recognition.Text)
                    .Equals(Normalize(Phrases[index]), StringComparison.Ordinal));
            }
            modelBrokerWasUsed = broker.LastUseUtc is not null;

            if (GetWorkerProcessIds(asrPython).Length != 1)
                throw new InvalidOperationException("VOICE_ASR_WORKER_NOT_UNIQUE_BEFORE_IDLE");

            var idleClock = Stopwatch.StartNew();
            var idleDeadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(3);
            while (DateTimeOffset.UtcNow < idleDeadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), deadline.Token);
                if (GetWorkerProcessIds(asrPython).Length == 0)
                {
                    idleUnloadObserved = true;
                    break;
                }
            }
            idleClock.Stop();
            idleUnloadWaitMs = idleClock.Elapsed.TotalMilliseconds;
            if (!idleUnloadObserved) throw new TimeoutException("VOICE_IDLE_UNLOAD_NOT_OBSERVED");

            var recoveryClock = Stopwatch.StartNew();
            var recovered = await voice.TranscribeWavAsync(audioSamples[^1], forceChinese: true, deadline.Token);
            recoveryClock.Stop();
            postIdleRecoveryMs = recoveryClock.Elapsed.TotalMilliseconds;
            if (!string.IsNullOrWhiteSpace(recovered.Language)) languageFieldPresentCount++;
            postIdleRecoveryMatched = Normalize(recovered.Text)
                .Equals(Normalize(Phrases[^1]), StringComparison.Ordinal);
        }
        catch (Exception exception)
        {
            // Error messages may contain local paths or worker output; keep only the type.
            errorType = exception.GetType().Name;
        }
        finally
        {
            foreach (var sample in audioSamples) CryptographicOperations.ZeroMemory(sample);
            if (voice is not null)
            {
                try { await voice.DisposeAsync(); }
                catch (Exception exception) { errorType ??= exception.GetType().Name; }
            }
            matchingWorkersAfterDispose = GetWorkerProcessIds(asrPython).Length
                + GetWorkerProcessIds(ttsPython).Length;
        }

        totalClock.Stop();
        var passed = errorType is null && serviceCreated && modelBrokerWasUsed
            && audioSamples.Count == Phrases.Length && audioSamples.All(sample => sample.All(value => value == 0))
            && audioBytes > Phrases.Length * 44 && languageFieldPresentCount == Phrases.Length + 1
            && transcriptMatches.Count == Phrases.Length && transcriptMatches.All(match => match)
            && idleUnloadObserved && postIdleRecoveryMatched && matchingWorkersAfterDispose == 0;
        var ttsWarm = ttsLatenciesMs.Skip(1).Order().ToArray();
        var asrWarm = asrLatenciesMs.Skip(1).Order().ToArray();
        var report = new
        {
            schemaVersion = 1,
            startedAtUtc = DateTimeOffset.UtcNow - totalClock.Elapsed,
            completedAtUtc = DateTimeOffset.UtcNow,
            path = "signed installed package manifest -> locked D-drive CPU TTS/ASR environments and weights -> ModelBroker -> idle eviction -> reload",
            modelRevisions = new { asr = AsrRevision, tts = TtsRevision },
            sampleCount = Phrases.Length,
            ttsCallCount = ttsLatenciesMs.Count,
            asrCallCount = asrLatenciesMs.Count + (idleUnloadObserved ? 1 : 0),
            syntheticTextAndAudioSaved = false,
            transcriptSaved = false,
            wavBytesInMemoryTotal = audioBytes,
            wavsZeroedAfterUse = audioSamples.Count == Phrases.Length
                && audioSamples.All(sample => sample.All(value => value == 0)),
            modelBrokerObservedUse = modelBrokerWasUsed,
            ttsCallLatenciesMs = ttsLatenciesMs,
            ttsColdElapsedMs = ttsLatenciesMs.FirstOrDefault(),
            ttsWarmMedianMs = Median(ttsWarm),
            asrCallLatenciesMs = asrLatenciesMs,
            asrColdElapsedMs = asrLatenciesMs.FirstOrDefault(),
            asrWarmMedianMs = Median(asrWarm),
            normalizedTranscriptMatches = transcriptMatches.Count(match => match),
            transcriptMatchBySample = transcriptMatches.Select((match, index) => new
                { sample = $"S{index + 1:00}", matched = match }),
            languageFieldPresentCount,
            idleUnloadExpectedDelayMs = 120000,
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
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report,
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"安装版六句 CPU 语音回环：{(passed ? "通过" : "未通过")}；"
            + $"ASR {transcriptMatches.Count(match => match)}/{Phrases.Length} 匹配，"
            + $"两分钟卸载 {(idleUnloadObserved ? "已观察" : "未观察")}，"
            + $"退出后工作进程 {matchingWorkersAfterDispose}。没有保存音频或转写内容。");
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
            catch (Exception exception) when (exception is InvalidOperationException
                or System.ComponentModel.Win32Exception or NotSupportedException or UnauthorizedAccessException)
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

    private static double Median(double[] sortedValues)
    {
        if (sortedValues.Length == 0) return 0;
        var middle = sortedValues.Length / 2;
        return sortedValues.Length % 2 == 0
            ? (sortedValues[middle - 1] + sortedValues[middle]) / 2
            : sortedValues[middle];
    }

    private static string Normalize(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormKC);
        return string.Concat(normalized.Where(char.IsLetterOrDigit));
    }
}
